using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp
{
    public sealed class NntpSessionPoolTests
    {
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

        internal static ProviderSessionEstablishmentGate CreateEstablishmentGate(int maxConcurrent = 1024) =>
            new(maxConcurrent);

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

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private sealed class IdleTimeProvider : TimeProvider
        {
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
                new IdleTimer();
        }

        private sealed class OnceFailingTimeProvider : TimeProvider
        {
            private int _calls;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    throw new NotSupportedException("keepalive clock");
                }

                return new IdleTimer();
            }
        }

        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
