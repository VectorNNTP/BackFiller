using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Tests.Nntp
{
    /// <summary>
    /// NNTP session pool leasing, replenishment, shrink, shutdown, and DATE keep-alive with scripted transports.
    /// </summary>

    public sealed class NntpSessionPoolTests
    {
        /// <summary>
        /// EnsureDesiredSessions opens MaxSessions connections before any lease is taken.
        /// </summary>

        [Fact]

        public async Task EnsureDesiredSessions_connects_max_sessions_before_any_lease()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 3);
            var pool = CreatePool(factory, max: 3);

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            Assert.Equal(3, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(3, factory.ConnectAttempts.Count);
            await pool.DisposeAsync();
        }

        /// <summary>
        /// BindProvider shrink retires excess idle sessions without additional connect attempts.
        /// </summary>

        [Fact]

        public async Task ShrinkToBound_retires_excess_idle_sessions_without_reconnect()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 4);
            var pool = CreatePool(factory, max: 4);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            var seen = new HashSet<NntpProviderSession>();
            for (var i = 0; i < 4; i++)
            {
                await using var lease = await pool.AcquireAsync(CancellationToken.None);
                seen.Add(lease.Session);
            }

            pool.BindProvider(
                new BackFillerProviderDefinition(
                    "Giganews",
                    "127.0.0.1",
                    119,
                    false,
                    null,
                    null,
                    0,
                    2));
            await pool.ShrinkToBoundAsync(CancellationToken.None);

            Assert.Equal(2, pool.LiveSessionCount);
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(4, factory.ConnectAttempts.Count);
            await using var reused = await pool.AcquireAsync(CancellationToken.None);
            Assert.Contains(reused.Session, seen);
            await reused.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Released healthy lease reuses the same connection when max is one.
        /// </summary>

        [Fact]

        public async Task Pool_reuses_a_healthy_session_and_does_not_exceed_max()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 1);

            await using var first = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(1, pool.LiveSessionCount);
            await first.DisposeAsync();

            await using var second = await pool.AcquireAsync(CancellationToken.None);
            Assert.Single(factory.ConnectAttempts);
            Assert.Equal(1, pool.ActiveLeaseCount);
            await second.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Concurrent acquires receive distinct sessions up to pool capacity.
        /// </summary>

        [Fact]

        public async Task Pool_does_not_allow_concurrent_use_of_one_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 2);
            var first = await pool.AcquireAsync(CancellationToken.None);
            var second = await pool.AcquireAsync(CancellationToken.None);

            Assert.NotSame(first.Session, second.Session);
            Assert.Equal(2, pool.LiveSessionCount);
            await first.DisposeAsync();
            await second.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Canceled wait on full pool and dispose while waiting fail without leaking leases.
        /// </summary>

        [Fact]

        public async Task Acquire_cancellation_and_shutdown_while_waiting()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var held = await pool.AcquireAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var waiting = pool.AcquireAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            var shutdownWait = pool.AcquireAsync(CancellationToken.None);
            var disposing = pool.DisposeAsync();
            await held.DisposeAsync();
            await disposing;
            await Assert.ThrowsAnyAsync<Exception>(() => shutdownWait);
        }

        /// <summary>
        /// Non-reusable session after protocol error is replaced on next acquire.
        /// </summary>

        [Fact]

        public async Task Broken_session_is_not_reused()
        {
            var factory = new ScriptedNntpTransportFactory();
            var firstServer = new ScriptedNntpServer();
            firstServer.Respond(static _ => "not-status\r\n");
            factory.Enqueue(firstServer);
            factory.Enqueue(new ScriptedNntpServer());
            var pool = CreatePool(factory, max: 1);

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.False(result.SessionReusable);
                lease.Retire();
            }

            var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            await replacement.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Double dispose on a lease does not put the same session back twice.
        /// </summary>

        [Fact]

        public async Task Double_release_does_not_return_the_session_twice()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var lease = await pool.AcquireAsync(CancellationToken.None);
            await lease.DisposeAsync();
            Assert.Equal(0, pool.ActiveLeaseCount);

            var again = await pool.AcquireAsync(CancellationToken.None);
            Assert.Single(factory.ConnectAttempts);
            await again.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Pool dispose waits for active lease release then rejects further acquires.
        /// </summary>

        [Fact]

        public async Task Shutdown_with_active_lease_waits_then_closes()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var lease = await pool.AcquireAsync(CancellationToken.None);
            var dispose = pool.DisposeAsync();
            await lease.DisposeAsync();
            await dispose;

            await Assert.ThrowsAnyAsync<Exception>(() => pool.AcquireAsync(CancellationToken.None));
        }

        /// <summary>
        /// Unreusable session after failed ARTICLE is not returned to the idle pool.
        /// </summary>

        [Fact]

        public async Task Session_failure_during_release_does_not_return_the_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            var first = new ScriptedNntpServer { CompleteAfterResponse = true };
            first.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n");
            factory.Enqueue(first);
            factory.Enqueue(new ScriptedNntpServer());
            var pool = CreatePool(factory, max: 1);

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.False(result.SessionReusable);
            }

            var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            await replacement.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Failed connect on acquire does not leave ActiveLeaseCount elevated.
        /// </summary>

        [Fact]

        public async Task Connect_failure_does_not_leak_a_lease_slot()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("down"),
            };
            var pool = CreatePool(factory, max: 1);

            await Assert.ThrowsAsync<NntpProviderConnectException>(() => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal(0, pool.ActiveLeaseCount);
            await pool.DisposeAsync();
        }

        /// <summary>Enqueues scripted NNTP servers that accept AUTHINFO and otherwise return 430 missing.</summary>
        /// <param name="factory">Transport factory receiving ready server instances.</param>
        /// <param name="count">Number of identical servers to enqueue.</param>
        internal static void EnqueueReadyServers(ScriptedNntpTransportFactory factory, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var server = new ScriptedNntpServer();
                server.Respond(static command =>
                    command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                        ? "281 authentication accepted\r\n"
                        : "430 missing\r\n");
                factory.Enqueue(server);
            }
        }

        /// <summary>
        /// Replenish connect fault is logged; pool can drop to zero active sessions without crashing.
        /// </summary>

        [Fact]

        public async Task Unexpected_replenish_failure_is_logged_and_does_not_fault_the_unobserved_fill()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(factory, max: 1, logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            factory.ConnectException = new NotSupportedException("observed replenish fault");
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                lease.Retire();
            }

            await WaitUntilAsync(() => logger.Messages.Any(static message =>
                message.Contains("NNTP session replenish failed", StringComparison.Ordinal)
                && message.Contains("Giganews", StringComparison.Ordinal)
                && message.Contains("observed replenish fault", StringComparison.Ordinal)));
            Assert.Equal(0, pool.ActiveSessionCount);
        }

        /// <summary>
        /// Shutdown during gated establishment logs cancel distinct from replenish failure.
        /// </summary>

        [Fact]

        public async Task Shutdown_during_observed_fill_is_logged_separately_from_replenishment_failure()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            using var gate = CreateEstablishmentGate(1);
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: logger);
            var gateHeld = false;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                await gate.WaitAsync(CancellationToken.None);
                gateHeld = true;

                await using (var lease = await pool.AcquireAsync(CancellationToken.None))
                {
                    lease.Retire();
                }

                await WaitUntilAsync(() => pool.LiveSessionCount == 1);
                var shutdownField = typeof(NntpSessionPool).GetField(
                    "_shutdown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var shutdown = Assert.IsType<CancellationTokenSource>(shutdownField?.GetValue(pool));
                await shutdown.CancelAsync();
                await WaitUntilAsync(() => logger.Messages.Any(static message =>
                    message.Contains(
                        "Shutdown continued after observed session fill was canceled",
                        StringComparison.Ordinal)));
                Assert.DoesNotContain(
                    logger.Messages,
                    static message => message.Contains("NNTP session replenish failed", StringComparison.Ordinal));
            }
            finally
            {
                if (gateHeld)
                {
                    gate.Release();
                }
            }
        }

        /// <summary>Creates a session-establishment gate capped at <paramref name="maxConcurrent"/> parallel connects.</summary>
        /// <param name="maxConcurrent">Maximum simultaneous provider session establishments.</param>
        internal static ProviderSessionEstablishmentGate CreateEstablishmentGate(int maxConcurrent = 1024) =>
            new(maxConcurrent);

        /// <summary>Constructs an <see cref="NntpSessionPool"/> wired to a scripted factory with test timeouts and backbone label.</summary>
        /// <param name="factory">Scripted transport factory supplying NNTP connections.</param>
        /// <param name="max">Maximum concurrent sessions for the provider definition.</param>
        /// <param name="min">Minimum desired sessions.</param>
        /// <param name="keepAliveSeconds">DATE keep-alive interval; zero disables keep-alive.</param>
        /// <param name="time">Optional time provider for keep-alive timers.</param>
        /// <param name="commandTimeout">Optional per-command timeout override.</param>
        /// <param name="establishment">Optional establishment gate; defaults to a high concurrency gate.</param>
        /// <param name="backbone">Provider backbone name used in logs.</param>
        /// <param name="logger">Logger instance; defaults to null logger.</param>
        internal static NntpSessionPool CreatePool(
            ScriptedNntpTransportFactory factory,
            int max,
            int min = 0,
            byte keepAliveSeconds = 0,
            TimeProvider? time = null,
            TimeSpan? commandTimeout = null,
            ProviderSessionEstablishmentGate? establishment = null,
            string backbone = "Giganews",
            ILogger? logger = null)
        {
            return new NntpSessionPool(
                new BackFillerProviderDefinition(
                    backbone,
                    "127.0.0.1",
                    119,
                    false,
                    null,
                    null,
                    min,
                    max,
                    keepAliveSeconds),
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                factory,
                logger ?? NullLogger.Instance,
                establishment ?? CreateEstablishmentGate(),
                shutdownGrace: TimeSpan.FromSeconds(2),
                timeProvider: time);
        }

        /// <summary>Scripted server answering DATE, AUTHINFO, and ARTICLE with optional synchronization gates.</summary>
        /// <param name="dateResponse">Raw DATE command response line including CRLF.</param>
        /// <param name="dateStarted">Optional signal when a DATE command is received.</param>
        /// <param name="blockDate">Optional gate that blocks DATE handling until released.</param>
        /// <param name="articleStarted">Optional signal when an ARTICLE command is received.</param>
        /// <param name="blockArticle">Optional gate that blocks ARTICLE handling until released.</param>
        internal static ScriptedNntpServer CreateDateAwareServer(
            string dateResponse = "111 20260926120000\r\n",
            TaskCompletionSource? dateStarted = null,
            TaskCompletionSource? blockDate = null,
            TaskCompletionSource? articleStarted = null,
            TaskCompletionSource? blockArticle = null)
        {
            var server = new ScriptedNntpServer
            {
                DateStarted = dateStarted,
                BlockDate = blockDate,
                ArticleStarted = articleStarted,
                BlockArticle = blockArticle,
            };
            server.Respond(command =>
            {
                if (command.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                {
                    return dateResponse;
                }

                if (command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase))
                {
                    return "281 authentication accepted\r\n";
                }

                if (command.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase))
                {
                    return "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n";
                }

                return "430 missing\r\n";
            });
            return server;
        }

        /// <summary>
        /// Pool dispose logs DATE keep-alive cancellation and completes.
        /// </summary>

        [Fact]

        public async Task Keepalive_cancellation_is_logged_and_disposal_completes()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: new IdleTimeProvider(),
                logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            await pool.DisposeAsync();

            Assert.Contains(
                logger.Messages,
                static message => message.Contains(
                    "Shutdown continued after DATE keepalive was canceled",
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// DATE keep-alive fault retires the session and is logged.
        /// </summary>

        [Fact]

        public async Task Unexpected_keepalive_failure_retires_the_session()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: new OnceFailingTimeProvider(),
                logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            await WaitUntilAsync(() => logger.Messages.Any(static message =>
                message.Contains("NNTP session retired", StringComparison.Ordinal)
                && message.Contains("DATE keepalive failed", StringComparison.Ordinal)));
            Assert.Equal(0, pool.ActiveLeaseCount);
        }

        /// <summary>Polls <paramref name="predicate"/> with yields until true or a two-second timeout.</summary>
        /// <param name="predicate">Condition that must become true.</param>
        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>
        /// TimeProvider returning inert timers for keep-alive tests.
        /// </summary>

        private sealed class IdleTimeProvider : TimeProvider
        {
            /// <summary>Returns <see cref="IdleTimer"/> instances so DATE keep-alive timers never fire during the scenario.</summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
                new IdleTimer();
        }

        /// <summary>
        /// TimeProvider whose first timer throws to simulate keep-alive clock failure.
        /// </summary>

        private sealed class OnceFailingTimeProvider : TimeProvider
        {
            /// <summary>Number of <see cref="CreateTimer"/> invocations; the first triggers retirement via thrown exception.</summary>
            private int _calls;

            /// <summary>First timer creation throws <see cref="NotSupportedException"/>; later calls return inert timers like <see cref="IdleTimeProvider"/>.</summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    throw new NotSupportedException("keepalive clock");
                }

                return new IdleTimer();
            }
        }

        /// <summary>
        /// No-op ITimer used when keep-alive timers should not fire.
        /// </summary>

        private sealed class IdleTimer : ITimer
        {
            /// <summary>Always returns false so the timer is never rescheduled.</summary>
            /// <param name="dueTime">Ignored.</param>
            /// <param name="period">Ignored.</param>
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            /// <summary>No-op timer disposal.</summary>
            public void Dispose()
            {
            }

            /// <summary>Completes immediately; no asynchronous timer teardown.</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
