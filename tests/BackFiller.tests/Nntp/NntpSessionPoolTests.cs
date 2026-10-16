using System.Collections.Concurrent;
using System.Net.Sockets;
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
        /// An <see cref="IOException"/> still releases live membership, the created count, and the connection number,
        /// and the single establishment permit is returned so a later acquire can connect.
        /// </summary>

        [Fact]

        public async Task Connect_failure_does_not_leak_a_lease_slot()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("down"),
            };
            using var gate = CreateEstablishmentGate(1);
            var pool = CreatePool(factory, max: 1, establishment: gate);

            await Assert.ThrowsAsync<NntpProviderConnectException>(() => pool.AcquireAsync(CancellationToken.None));
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            await recovered.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// <see cref="SocketException"/> during connect fails the acquire, removes the session from the live set,
        /// and returns created-count and connection-number capacity so the next connect can use slot 1.
        /// </summary>

        [Fact]

        public async Task Socket_exception_during_connect_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => pool.AcquireAsync(CancellationToken.None));

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, failure.Kind);
            Assert.Equal(nameof(SocketException), failure.Message);
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Parallel and repeated socket failures do not accumulate live sessions or created-count reservations.
        /// After the transport recovers, the pool fills exactly <c>MaxSessions</c> and reuses connection numbers 1 and 2.
        /// </summary>

        [Fact]

        public async Task Repeated_socket_exceptions_do_not_leak_slots_and_recovery_fills_the_pool()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            await using var pool = CreatePool(factory, max: 2);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                AssertNoReservedCapacity(pool);
            }

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 2);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            Assert.Equal(2, pool.LiveSessionCount);
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(2, CreatedSessionCount(pool));
            Assert.Equal(2, ReservedConnectionCount(pool));

            await using var first = await pool.AcquireAsync(CancellationToken.None);
            await using var second = await pool.AcquireAsync(CancellationToken.None);
            Assert.NotSame(first.Session, second.Session);
            var connectionNumbers = new[] { first.Session.ConnectionNumber, second.Session.ConnectionNumber };
            Array.Sort(connectionNumbers);
            Assert.Equal(new[] { 1, 2 }, connectionNumbers);
        }

        /// <summary>
        /// The first periodic tick keeps a socket failure inside session creation and does not log a replenish failure.
        /// The second tick throws <see cref="NotSupportedException"/>, which the replenish supervisor logs, and rolls the reservation back.
        /// The third tick restores one ready session. Shutdown then completes the loop without entering another delay.
        /// </summary>

        [Fact]

        public async Task Periodic_replenish_logs_unexpected_failure_and_restores_capacity()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var delays = new ReplenishDelaySource();
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            await using var pool = CreatePool(factory, max: 1, time: delays, logger: logger);

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            AssertNoReservedCapacity(pool);

            var releaseFirst = await delays.WaitForNextAsync();
            releaseFirst();
            var releaseSecond = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            AssertNoReservedCapacity(pool);
            Assert.DoesNotContain(
                logger.Entries,
                static entry => entry.EventId.Id == 5404);

            factory.ConnectException = new NotSupportedException("replenish tick fault");
            releaseSecond();
            var releaseThird = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            Assert.False(ReplenishTask(pool).IsFaulted);
            AssertNoReservedCapacity(pool);
            Assert.Contains(
                logger.Entries,
                static entry => entry.Level == LogLevel.Warning
                    && entry.EventId.Id == 5404
                    && entry.Message.Contains("replenish tick fault", StringComparison.Ordinal));

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            releaseThird();
            await WaitUntilAsync(() => pool.ActiveSessionCount == 1);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                Assert.Equal(1, lease.Session.ConnectionNumber);
                Assert.Equal(NntpSessionState.Ready, lease.Session.State);
            }

            _ = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            var timersAtCapacity = delays.TimersCreated;
            var shutdownField = typeof(NntpSessionPool).GetField(
                "_shutdown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var shutdown = Assert.IsType<CancellationTokenSource>(shutdownField?.GetValue(pool));
            await shutdown.CancelAsync();
            await WaitUntilAsync(() => ReplenishTask(pool).IsCompleted);
            Assert.Equal(TaskStatus.RanToCompletion, ReplenishTask(pool).Status);
            Assert.Equal(timersAtCapacity, delays.TimersCreated);
        }

        /// <summary>
        /// A throwing active-count subscriber on a successful connect retires that session once.
        /// With one permitted session, the next acquire reuses connection number 1.
        /// </summary>

        [Fact]

        public async Task Success_notification_failure_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            var notifications = 0;
            pool.ActiveSessionCountChanged += () =>
            {
                if (Interlocked.Increment(ref notifications) == 1)
                {
                    throw new InvalidOperationException("active count subscriber");
                }
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal("active count subscriber", failure.Message);
            AssertNoReservedCapacity(pool);

            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Equal(3, Volatile.Read(ref notifications));
        }

        /// <summary>
        /// A completion-log failure after a ready connect retires that session once and leaves the establishment permit free.
        /// The same logger accepts the next completion, and the next acquire gets connection number 1.
        /// </summary>

        [Fact]

        public async Task Completion_log_failure_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            using var gate = CreateEstablishmentGate(1);
            var logger = new ThrowOnceOnCompletionLogger();
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: logger);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal("completion log failed", failure.Message);
            AssertNoReservedCapacity(pool);

            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Equal(1, logger.CompletionFailures);
        }

        /// <summary>
        /// A retirement callback that blocks cannot drop a replacement session's capacity notification.
        /// The subscriber throws after the replacement is published, and a later callback still reports that session.
        /// </summary>

        [Fact]

        public async Task Capacity_notification_during_a_blocked_retirement_callback_is_delivered()
        {
            var delays = new ReplenishDelaySource();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(factory, max: 1, time: delays);
            var retirementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveredCapacity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observations = new ConcurrentQueue<int>();
            pool.ActiveSessionCountChanged += () =>
            {
                var count = pool.ActiveSessionCount;
                observations.Enqueue(count);
                if (count == 0 && retirementEntered.TrySetResult())
                {
                    if (!releaseRetirement.Task.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Retirement callback was not released.");
                    }

                    throw new InvalidOperationException("retirement subscriber");
                }

                if (count > 0 && retirementEntered.Task.IsCompleted)
                {
                    recoveredCapacity.TrySetResult();
                }
            };

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.ActiveSessionCount);

            var retirement = Task.Run(async () =>
            {
                await using var lease = await pool.AcquireAsync(CancellationToken.None);
                lease.Retire();
            });

            await retirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            releaseRetirement.TrySetResult();
            await recoveredCapacity.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, pool.ActiveSessionCount);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Contains(observations, static count => count == 1);
            Assert.Equal(pool.ActiveSessionCount, observations.Last());

            await using var recovered = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
        }

        /// <summary>
        /// A logger that fails while a connect failure is recorded does not replace that failure or keep the reservation.
        /// </summary>

        [Fact]

        public async Task Logger_failure_during_connect_failure_preserves_the_connect_exception_and_releases_capacity()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: new ThrowingEstablishmentLogger());

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, failure.Kind);
            Assert.Equal(nameof(SocketException), failure.Message);
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Cancelling while the new session is registered but still waiting for the establishment gate
        /// removes that registration once. The same connection number can be acquired afterward.
        /// </summary>

        [Fact]

        public async Task Cancellation_before_connect_rolls_back_the_registered_session_once()
        {
            using var gate = CreateEstablishmentGate(1);
            var gateHeld = false;
            await gate.WaitAsync(CancellationToken.None);
            gateHeld = true;
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            try
            {
                using var cts = new CancellationTokenSource();
                var acquire = pool.AcquireAsync(cts.Token);
                await WaitUntilAsync(() => pool.LiveSessionCount == 1);
                Assert.Equal(1, CreatedSessionCount(pool));
                await cts.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
                AssertNoReservedCapacity(pool);

                gate.Release();
                gateHeld = false;
                await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, recovered.Session.ConnectionNumber);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.Equal(1, ReservedConnectionCount(pool));
            }
            finally
            {
                if (gateHeld)
                {
                    gate.Release();
                }
            }
        }

        /// <summary>
        /// Cancelling an in-flight connect retires the registered session once and returns the establishment permit.
        /// A second acquire then uses connection number 1.
        /// </summary>

        [Fact]

        public async Task Cancellation_during_connect_rolls_back_capacity_without_double_release()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            using var cts = new CancellationTokenSource();
            var acquire = pool.AcquireAsync(cts.Token);
            await WaitUntilAsync(() => factory.CurrentConcurrentConnects == 1 && pool.LiveSessionCount == 1);
            await cts.CancelAsync();

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => acquire.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ArticleRetrievalKind.Cancelled, failure.Kind);
            AssertNoReservedCapacity(pool);

            factory.BlockConnect = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Disposing the pool while connect is blocked rolls the partial registration back once.
        /// A second retire from dispose does not drive the created count negative or free another session's number.
        /// </summary>

        [Fact]

        public async Task Dispose_during_connect_rolls_back_the_reservation_once()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var pool = CreatePool(factory, max: 1);
            var acquire = pool.AcquireAsync(CancellationToken.None);
            await WaitUntilAsync(() => factory.CurrentConcurrentConnects == 1 && pool.LiveSessionCount == 1);
            Assert.Equal(1, CreatedSessionCount(pool));

            var disposing = pool.DisposeAsync().AsTask();
            var acquireFinished = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(acquire, acquireFinished);
            await Assert.ThrowsAnyAsync<Exception>(() => acquire);

            var disposeFinished = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(disposing, disposeFinished);
            await disposing;

            AssertNoReservedCapacity(pool);
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

        /// <summary>
        /// Asserts a finished failed create left no live session, lease, created-count reservation, or connection number.
        /// </summary>
        /// <param name="pool">Pool observed after the failed attempt has returned.</param>
        private static void AssertNoReservedCapacity(NntpSessionPool pool)
        {
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, CreatedSessionCount(pool));
            Assert.Equal(0, ReservedConnectionCount(pool));
        }

        /// <summary>Reads <c>_created</c> after the pool's create path has quiesced.</summary>
        /// <param name="pool">Pool whose reservation count is observed.</param>
        /// <returns>The current created-session count.</returns>
        private static int CreatedSessionCount(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (int)field.GetValue(pool)!;
        }

        /// <summary>
        /// Reads how many connection numbers are reserved after creation has quiesced.
        /// The read is not taken under the pool slot lock; callers wait until no create is in progress.
        /// </summary>
        /// <param name="pool">Pool whose connection-number set is observed.</param>
        /// <returns>The number of reserved connection numbers.</returns>
        private static int ReservedConnectionCount(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_usedSlots", BindingFlags.Instance | BindingFlags.NonPublic);
            var slots = Assert.IsType<HashSet<int>>(field?.GetValue(pool));
            return slots.Count;
        }

        /// <summary>Reads the periodic replenish task started by the pool.</summary>
        /// <param name="pool">Pool whose replenish loop is running.</param>
        /// <returns>The loop task. It stays incomplete while the loop is waiting for the next interval.</returns>
        private static Task ReplenishTask(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_replenishTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var task = field.GetValue(pool) as Task;
            Assert.NotNull(task);
            return task!;
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
        /// Holds replenish timers until the test releases them.
        /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> creates these timers; the production interval is unchanged.
        /// </summary>
        private sealed class ReplenishDelaySource : TimeProvider
        {
            /// <summary>Replenish timers entered and not yet observed by the test, in entry order.</summary>
            private readonly ConcurrentQueue<HeldTimer> _pending = new();

            /// <summary>How many replenish delays have been entered, including one the test has already observed.</summary>
            private int _timersCreated;

            /// <summary>How many replenish delays <see cref="CreateTimer"/> has captured.</summary>
            internal int TimersCreated => Volatile.Read(ref _timersCreated);

            /// <summary>Waits until the replenish loop enters its next delay.</summary>
            /// <returns>A callback that fires that delay.</returns>
            /// <exception cref="TimeoutException">The loop does not enter another delay within five seconds.</exception>
            public async Task<Action> WaitForNextAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!timeout.IsCancellationRequested)
                {
                    if (_pending.TryDequeue(out var next))
                    {
                        return next.Release;
                    }

                    await Task.Yield();
                }

                throw new TimeoutException("Periodic replenishment did not enter its next delay.");
            }

            /// <summary>Captures the delay callback instead of arming a real timer.</summary>
            /// <param name="callback">Completion callback supplied by <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</param>
            /// <param name="state">State passed back to <paramref name="callback"/> when the test releases the delay.</param>
            /// <param name="dueTime">Accepted for the timer contract. The test decides when the callback runs.</param>
            /// <param name="period">Accepted for the timer contract. Replenish delays are one-shot.</param>
            /// <returns>A timer that fires only when the test releases it. Disposal does not fire it.</returns>
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _ = dueTime;
                _ = period;
                Interlocked.Increment(ref _timersCreated);
                var timer = new HeldTimer(callback, state);
                _pending.Enqueue(timer);
                return timer;
            }

            /// <summary>One replenish delay whose callback the test invokes.</summary>
            /// <param name="callback">Timer callback supplied by the delay.</param>
            /// <param name="state">State object supplied with <paramref name="callback"/>.</param>
            private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
            {
                /// <summary>Zero until <see cref="Release"/> has invoked the callback.</summary>
                private int _fired;

                /// <summary>Invokes the delay callback once. A disposed or already released timer does not invoke it again.</summary>
                public void Release()
                {
                    if (Interlocked.Exchange(ref _fired, 1) == 0)
                    {
                        callback(state);
                    }
                }

                /// <summary>Accepts reschedule requests. The test, not the due time, fires the callback.</summary>
                /// <param name="dueTime">Ignored.</param>
                /// <param name="period">Ignored.</param>
                /// <returns><see langword="true"/>. The timer remains under test control.</returns>
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                public void Dispose()
                {
                    Interlocked.Exchange(ref _fired, 1);
                }

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                /// <returns>A completed task.</returns>
                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }

        /// <summary>
        /// Logger that throws from establishment and retirement diagnostics and accepts every other message.
        /// </summary>
        private sealed class ThrowingEstablishmentLogger : ILogger
        {
            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => EmptyScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>
            /// Throws <see cref="InvalidOperationException"/> for establishment-failure, establishment-cancellation,
            /// and session-retirement messages. Other messages are ignored so transport disposal can finish.
            /// </summary>
            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                if (message.Contains("establishment failed", StringComparison.Ordinal)
                    || message.Contains("establishment cancelled", StringComparison.Ordinal)
                    || message.Contains("session retired", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("logger failed");
                }
            }

            /// <summary>Scope returned by <see cref="BeginScope{TState}"/>. Disposal does nothing.</summary>
            private sealed class EmptyScope : IDisposable
            {
                /// <summary>Shared scope instance.</summary>
                internal static EmptyScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Logger that throws once from the establishment-completed event and then accepts every later call.
        /// </summary>
        private sealed class ThrowOnceOnCompletionLogger : ILogger
        {
            /// <summary>How many establishment-completed logs have thrown.</summary>
            private int _completionFailures;

            /// <summary>How many establishment-completed logs have thrown.</summary>
            internal int CompletionFailures => Volatile.Read(ref _completionFailures);

            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => EmptyScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>
            /// Throws <see cref="InvalidOperationException"/> the first time the establishment-completed message is logged.
            /// Later calls, including retirement diagnostics, are ignored.
            /// </summary>
            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                if (message.Contains("establishment completed", StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _completionFailures, 1, 0) == 0)
                {
                    throw new InvalidOperationException("completion log failed");
                }
            }

            /// <summary>Scope returned by <see cref="BeginScope{TState}"/>. Disposal does nothing.</summary>
            private sealed class EmptyScope : IDisposable
            {
                /// <summary>Shared scope instance.</summary>
                internal static EmptyScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
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
        /// TimeProvider whose DATE keepalive timer throws to simulate a keepalive clock failure.
        /// The periodic replenish timer on the same clock stays inert so this failure stays on the DATE path.
        /// </summary>

        private sealed class OnceFailingTimeProvider : TimeProvider
        {
            /// <summary>
            /// Throws when the due time is the 30-second DATE interval used by the keepalive fault test.
            /// Replenish waits use a different due time and receive an inert timer.
            /// </summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == TimeSpan.FromSeconds(30))
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
