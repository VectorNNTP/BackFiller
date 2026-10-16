using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using VectorNNTP.BackFiller.Logging;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Bounded per-provider NNTP session pool. One lease owns one session.
    /// </summary>
    /// <remarks>
    /// Idle sessions are reused by <see cref="AcquireAsync"/>. DATE keepalive runs only while a session sits idle and <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
    /// A background fill replaces retired sessions up to the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// </remarks>
    internal sealed class NntpSessionPool : IAsyncDisposable
    {
        /// <summary>Provider snapshot. Replaced by <see cref="BindProvider"/>; not a frozen constructor copy.</summary>
        private BackFillerProviderDefinition _provider;

        /// <summary>
        /// <see cref="BackFillerProviderDefinition.MaxSessions"/> captured at construction.
        /// Shrink math treats this as the semaphore's original capacity. <see cref="BindProvider"/> does not change it.
        /// </summary>
        private readonly int _leaseCeiling;

        /// <summary>Timeouts and buffers copied into every session.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Factory used by <see cref="CreateReadySessionAsync"/>.</summary>
        private readonly INntpTransportFactory _transport;

        /// <summary>
        /// Application-wide establishment concurrency gate shared by every pool.
        /// Held only around <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </summary>
        private readonly ProviderSessionEstablishmentGate _establishment;

        /// <summary>Pool logger. Also passed to each <see cref="NntpProviderSession"/>.</summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Clock for DATE keepalive delays and the periodic replenish wait.
        /// Production uses <see cref="TimeProvider.System"/>, which keeps <see cref="ReplenishInterval"/>.
        /// </summary>
        private readonly TimeProvider _time;

        /// <summary>How long <see cref="DisposeAsync"/> and <see cref="AwaitKeepAlivesAsync"/> wait. Defaults to two seconds.</summary>
        private readonly TimeSpan _shutdownGrace;

        /// <summary>
        /// Acquire permits. The initial count and the semaphore maximum are the constructor <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// <see cref="ReturnAsync"/> releases a permit only while outstanding leases are below the current max.
        /// </summary>
        private readonly SemaphoreSlim _leases;

        /// <summary>DATE loop for each idle session that has keepalive enabled. Absent while the session is leased.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, KeepAliveRegistration> _keepAlives = new();

        /// <summary>Sessions waiting for <see cref="AcquireAsync"/>.</summary>
        private readonly ConcurrentQueue<NntpProviderSession> _idle = new();

        /// <summary>Sessions created and not yet retired. The byte value is unused.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, byte> _live = new();

        /// <summary>Connection numbers currently allocated. Guarded by <see cref="_slotGate"/>.</summary>
        private readonly HashSet<int> _usedSlots = [];

        /// <summary>
        /// Non-recursive lock for <see cref="_usedSlots"/>.
        /// No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _slotGate = new();

        /// <summary>Cancelled at the start of dispose. Linked into acquire waits and DATE loops.</summary>
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Serializes session creation and <see cref="ShrinkToBoundAsync"/>.</summary>
        private readonly SemaphoreSlim _fillGate = new(1, 1);

        /// <summary>
        /// Sessions counted against the current max.
        /// Incremented before a connection number is reserved and decremented exactly once when that attempt is abandoned or the live session is retired.
        /// </summary>
        private int _created;

        /// <summary>Leases issued by <see cref="Issue"/> and not yet finished by <see cref="ReturnAsync"/>.</summary>
        private int _activeLeases;

        /// <summary>Zero until dispose begins. The first dispose wins.</summary>
        private int _disposed;

        /// <summary>Zero until <see cref="StartPeriodicReplenish"/> publishes <see cref="_replenishTask"/>.</summary>
        private int _replenishStarted;

        /// <summary>
        /// Periodic deficit supervisor. Null before the first start.
        /// It stays incomplete across failed fill attempts and completes when shutdown stops the loop.
        /// </summary>
        private Task? _replenishTask;

        /// <summary>No caller is inside <see cref="NotifyActiveSessionCountChanged"/>.</summary>
        private const int CapacityNotificationIdle = 0;

        /// <summary>One caller is invoking <see cref="ActiveSessionCountChanged"/>.</summary>
        private const int CapacityNotificationActive = 1;

        /// <summary>
        /// A notification is in progress and a later state change has requested another delivery.
        /// The active caller replays after the current callback returns.
        /// </summary>
        private const int CapacityNotificationPending = 2;

        /// <summary>
        /// <see cref="CapacityNotificationIdle"/>, <see cref="CapacityNotificationActive"/>, or
        /// <see cref="CapacityNotificationPending"/>. Coordinated with <see cref="Interlocked"/> so a callback
        /// does not run nested, and a state change during that callback is still delivered afterward.
        /// </summary>
        private int _capacityNotification;

        /// <summary>
        /// Completes when no lease is outstanding. Replaced with a new incomplete source when <see cref="Issue"/> raises the count from zero.
        /// </summary>
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Delay between periodic deficit fills toward <see cref="BackFillerProviderDefinition.MaxSessions"/>.</summary>
        private static readonly TimeSpan ReplenishInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Creates a pool whose lease permits and connection-number ceiling start at <see cref="BackFillerProviderDefinition.MaxSessions"/> on <paramref name="provider"/>.
        /// </summary>
        /// <param name="provider">
        /// Provider identity. <see cref="BackFillerProviderDefinition.MaxSessions"/> must be at least 1.
        /// <see cref="BackFillerProviderDefinition.MinSessions"/> must be from 0 through that max. It is not used to size the pool.
        /// </param>
        /// <param name="options">Timeouts and buffers copied into each session.</param>
        /// <param name="transport">Opens sockets for new sessions.</param>
        /// <param name="logger">Pool and session logger.</param>
        /// <param name="establishment">
        /// Application-wide gate shared by every pool. Held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for leases. Two seconds when null.</param>
        /// <param name="timeProvider">Clock for DATE delays and replenish waits. <see cref="TimeProvider.System"/> when null.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="provider"/>, <paramref name="options"/>, <paramref name="transport"/>,
        /// <paramref name="logger"/>, or <paramref name="establishment"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">The session bounds on <paramref name="provider"/> are outside the constructor checks.</exception>
        internal NntpSessionPool(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            INntpTransportFactory transport,
            ILogger logger,
            ProviderSessionEstablishmentGate establishment,
            TimeSpan? shutdownGrace = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(establishment);
            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            if (provider.MinSessions < 0 || provider.MinSessions > provider.MaxSessions)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MinSessions must be between 0 and MaxSessions.");
            }

            _provider = provider;
            _options = options;
            _transport = transport;
            _establishment = establishment;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2);
            _leaseCeiling = provider.MaxSessions;
            _leases = new SemaphoreSlim(provider.MaxSessions, provider.MaxSessions);
            _drained.TrySetResult();
        }

        /// <summary>Gets <see cref="BackFillerProviderDefinition.Backbone"/> from the bound provider snapshot.</summary>
        internal string Backbone => _provider.Backbone;

        /// <summary>Gets the provider snapshot this pool currently serves.</summary>
        /// <remarks>Updated by <see cref="BindProvider"/>.</remarks>
        internal BackFillerProviderDefinition Provider => _provider;

        /// <summary>Gets the number of session objects still in the live set, including sessions that are connecting and not yet retired.</summary>
        internal int LiveSessionCount => _live.Count;

        /// <summary>
        /// Gets the number of ACTIVE sessions: attached sessions that have completed connect
        /// (<see cref="NntpSessionState.Ready"/> or <see cref="NntpSessionState.Busy"/>).
        /// Connecting, failed, and retired sessions are not counted.
        /// </summary>
        internal int ActiveSessionCount
        {
            get
            {
                return _live.Keys.Count(session => session.State is NntpSessionState.Ready or NntpSessionState.Busy);
            }
        }

        /// <summary>Outstanding <see cref="NntpSessionLease"/> instances that have not yet been disposed.</summary>
        internal int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

        /// <summary>Raised after the active session count may have changed.</summary>
        /// <remarks>
        /// Invoked synchronously on the caller that changed membership or finished a successful connect.
        /// Subscribers read <see cref="ActiveSessionCount"/> themselves. A callback is not nested inside another
        /// callback. A state change that requests notification while a callback is running is delivered again
        /// after that callback returns, so a retirement callback cannot hide a replacement session that became ready.
        /// A subscriber exception during successful publication fails that publication: the pool retires the session
        /// and the caller does not receive it. A subscriber exception during retirement is contained after the
        /// reservation is released, so disposal still runs and the reservation is not released twice.
        /// </remarks>
        internal event Action? ActiveSessionCountChanged;

        /// <summary>
        /// Connects the desired <see cref="BackFillerProviderDefinition.MaxSessions"/> slots.
        /// Does not require Article Work. Failed attempts do not fail the call.
        /// </summary>
        /// <param name="cancellationToken">Cancels in-flight connects. Cancellation fails this call. A connect failure does not.</param>
        /// <exception cref="ObjectDisposedException">The pool is already disposed.</exception>
        /// <remarks>
        /// Starts the periodic replenish loop once. Missing slots are scheduled concurrently, but
        /// <see cref="ProviderSessionEstablishmentGate"/> bounds how many
        /// <see cref="NntpProviderSession.ConnectAsync"/> calls run at once across all pools.
        /// </remarks>
        internal async Task EnsureDesiredSessionsAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            StartPeriodicReplenish();
            var missing = Math.Max(0, _provider.MaxSessions - _live.Count);
            if (missing == 0)
            {
                NotifyActiveSessionCountChanged();
                return;
            }

            var attempts = new Task[missing];
            for (var i = 0; i < missing; i++)
            {
                attempts[i] = TryCreateIdleSessionAsync(cancellationToken);
            }

            await Task.WhenAll(attempts).ConfigureAwait(false);
            NotifyActiveSessionCountChanged();
        }

        /// <summary>Acquires an exclusive session lease, reusing an idle session or opening one up to the bound max.</summary>
        /// <param name="cancellationToken">Cancels the permit wait and connect. Pool shutdown cancels the same wait.</param>
        /// <returns>A lease that owns one ready session. Disposing the lease returns or retires it.</returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed before the permit is taken or before a session is issued.</exception>
        /// <exception cref="OperationCanceledException">The caller token or <see cref="_shutdown"/> cancels the wait.</exception>
        /// <exception cref="InvalidOperationException">The created-session count is already at <see cref="BackFillerProviderDefinition.MaxSessions"/>.</exception>
        /// <exception cref="NntpProviderConnectException">
        /// The new session did not become <see cref="NntpSessionState.Ready"/>. It is retired without replenish.
        /// </exception>
        /// <remarks>
        /// Each idle candidate dequeued before the fill gate is held, if it is <see cref="NntpSessionState.Busy"/>, is waited on until it leaves that state, then kept only when <see cref="NntpProviderSession.IsReusable"/>.
        /// An idle candidate taken later, while the fill gate is held, is retired as soon as it is not reusable, including when it is still busy.
        /// A failed acquire releases the permit. A successful acquire holds it until <see cref="ReturnAsync"/>.
        /// A publication failure inside <see cref="CreateReadySessionAsync"/> retires the new session before this method returns, so the caller does not receive a lease.
        /// </remarks>
        internal async Task<NntpSessionLease> AcquireAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await _leases.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                while (_idle.TryDequeue(out var idle))
                {
                    StopKeepAlive(idle);
                    if (idle.State == NntpSessionState.Busy)
                    {
                        await WaitUntilNotBusyAsync(idle, linked.Token).ConfigureAwait(false);
                    }

                    if (idle.IsReusable)
                    {
                        return Issue(idle);
                    }

                    await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                }

                await _fillGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    while (_idle.TryDequeue(out var idle))
                    {
                        StopKeepAlive(idle);
                        if (idle.IsReusable)
                        {
                            return Issue(idle);
                        }

                        await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                    }

                    if (Volatile.Read(ref _created) >= _provider.MaxSessions)
                    {
                        throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
                    }

                    var created = await CreateReadySessionAsync(linked.Token).ConfigureAwait(false);
                    return Issue(created);
                }
                finally
                {
                    _fillGate.Release();
                }
            }
            catch
            {
                _leases.Release();
                throw;
            }
        }

        /// <summary>
        /// Ends one lease. A reusable session returns to the idle queue unless the pool is shutting down or the live count is above the bound max.
        /// </summary>
        /// <param name="session">Session previously issued by this pool.</param>
        /// <param name="retire"><see langword="true"/> disposes <paramref name="session"/> instead of idling it.</param>
        /// <remarks>
        /// The lease count is decremented even when retirement fails. The acquire permit is released only while outstanding leases are below the current max.
        /// A return that finds the live count above that max retires the session and consumes the permit so a shrunk pool does not hand the slot out again.
        /// An idle return starts DATE keepalive when <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
        /// </remarks>
        internal async Task ReturnAsync(NntpProviderSession session, bool retire)
        {
            try
            {
                if (retire || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
                {
                    await RetireSessionAsync(session, retire ? "lease requested retirement" : "pool is shutting down")
                        .ConfigureAwait(false);
                }
                else if (session.IsReusable)
                {
                    if (_live.Count > _provider.MaxSessions)
                    {
                        await RetireSessionAsync(session, "max sessions shrink", replenish: false)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        EnqueueIdle(session);
                    }
                }
                else
                {
                    await RetireSessionAsync(session, "session is no longer reusable").ConfigureAwait(false);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeLeases) <= 0)
                {
                    _drained.TrySetResult();
                }

                try
                {
                    if (Volatile.Read(ref _activeLeases) < _provider.MaxSessions)
                    {
                        _leases.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                    ShutdownObservations.AlreadyDisposed(_logger, "session lease release");
                }
            }
        }

        /// <summary>
        /// Updates the bound provider snapshot. Used when only <see cref="BackFillerProviderDefinition.MaxSessions"/> shrinks.
        /// </summary>
        /// <param name="provider">The new snapshot with the same connection identity.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="provider"/> fails <see cref="BackFillerProviderDefinition.HasSameConnectionIdentity"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="BackFillerProviderDefinition.MaxSessions"/> is below 1.</exception>
        /// <remarks>Does not retire sessions, change permits, or reconnect. <see cref="ShrinkToBoundAsync"/> applies the new ceiling.</remarks>
        internal void BindProvider(BackFillerProviderDefinition provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!provider.HasSameConnectionIdentity(_provider))
            {
                throw new ArgumentException("Provider connection identity does not match this pool.", nameof(provider));
            }

            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            _provider = provider;
        }

        /// <summary>
        /// Retires idle sessions above the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// Leased sessions above the bound retire when returned. Does not reconnect retained sessions.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait for <see cref="_fillGate"/>. Idle retirement after the gate is acquired does not observe this token.</param>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <remarks>
        /// Available acquire permits are consumed until they do not exceed the new bound minus permits already held.
        /// The semaphore maximum stays at <see cref="_leaseCeiling"/>.
        /// </remarks>
        internal async Task ShrinkToBoundAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bound = _provider.MaxSessions;
                while (_live.Count > bound && _idle.TryDequeue(out var idle))
                {
                    StopKeepAlive(idle);
                    await RetireSessionAsync(idle, "max sessions shrink", replenish: false).ConfigureAwait(false);
                }

                var permitHolders = _leaseCeiling - _leases.CurrentCount;
                var desiredPermits = Math.Max(0, bound - permitHolders);
                while (_leases.CurrentCount > desiredPermits && _leases.Wait(0, CancellationToken.None))
                {
                    // Intentionally empty: this loop only consumes excess permits.
                }
            }
            finally
            {
                _fillGate.Release();
            }

            NotifyActiveSessionCountChanged();
        }

        /// <summary>
        /// Stops new leases, waits for outstanding leases to return, then retires remaining sessions.
        /// Used when the control plane replaces this pool. Does not force-close a leased session unless
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">
        /// Bounds the wait for outstanding leases. When it is cancelled, the wait ends and remaining sessions are still retired.
        /// </param>
        /// <returns>The dispose task. This path does not apply <see cref="_shutdownGrace"/> itself.</returns>
        internal Task DrainAndDisposeAsync(CancellationToken cancellationToken) =>
            DisposeCoreAsync(forceAfterGrace: false, cancellationToken);

        /// <summary>
        /// Stops new leases, waits up to <see cref="_shutdownGrace"/> for outstanding leases, then retires every remaining session.
        /// </summary>
        /// <returns>A task that completes after remaining sessions have been retired.</returns>
        /// <remarks>
        /// The drain wait uses <see cref="CancellationToken.None"/> plus the grace timeout.
        /// A grace timeout does not abandon remaining sessions. A second call returns immediately.
        /// </remarks>
        public ValueTask DisposeAsync() => new(DisposeCoreAsync(forceAfterGrace: true, CancellationToken.None));

        /// <summary>
        /// Cancels shutdown, stops replenish and keepalive, waits for leases, then retires every session still tracked.
        /// </summary>
        /// <param name="forceAfterGrace">
        /// <see langword="true"/> waits on <see cref="_drained"/> for <see cref="_shutdownGrace"/>.
        /// <see langword="false"/> waits on <paramref name="cancellationToken"/> instead.
        /// </param>
        /// <param name="cancellationToken">Drain budget when <paramref name="forceAfterGrace"/> is false. Ignored for the lease wait when it is true.</param>
        /// <remarks>
        /// Timeout and cancellation of the lease wait are swallowed, and retirement still runs.
        /// <see cref="SuperviseReplenishAsync"/> logs an unexpected fill failure and keeps the replenish task alive until shutdown.
        /// A failure of that supervisor task itself is logged here and does not skip retirement.
        /// Keepalive tasks are given <see cref="_shutdownGrace"/> and then abandoned if they are still running.
        /// </remarks>
        private async Task DisposeCoreAsync(bool forceAfterGrace, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _shutdown.CancelAsync().ConfigureAwait(false);
            var replenish = _replenishTask;
            if (replenish is not null)
            {
                try
                {
                    await replenish.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ShutdownObservations.Canceled(_logger, "session replenish");
                }
                // codeql[cs/catch-of-all-exceptions] Keepalive and replenishment failures must retire or log the session and must not escape the background task.
                catch (Exception ex)
                {
                    NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
                }
            }

            await AwaitKeepAlivesAsync().ConfigureAwait(false);
            try
            {
                if (forceAfterGrace)
                {
                    await _drained.Task.WaitAsync(_shutdownGrace, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                ShutdownObservations.TimedOut(_logger, "pool dispose grace");
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "pool dispose grace");
            }

            while (_idle.TryDequeue(out var idle))
            {
                await RetireSessionAsync(idle, "pool dispose").ConfigureAwait(false);
            }

            foreach (var live in _live.Keys)
            {
                await RetireSessionAsync(live, "pool dispose").ConfigureAwait(false);
            }

            _leases.Dispose();
            _fillGate.Dispose();
            _shutdown.Dispose();
        }

        /// <summary>Counts a new outstanding lease and returns it. The first lease of a generation replaces <see cref="_drained"/>.</summary>
        /// <param name="session">Ready session the lease will own.</param>
        /// <returns>A lease whose disposal calls <see cref="ReturnAsync"/>.</returns>
        private NntpSessionLease Issue(NntpProviderSession session)
        {
            if (Interlocked.Increment(ref _activeLeases) == 1)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new NntpSessionLease(this, session);
        }

        /// <summary>Allocates a connection number, connects a session, and returns it only when it is <see cref="NntpSessionState.Ready"/>.</summary>
        /// <param name="cancellationToken">
        /// Cancels the establishment-gate wait and <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <returns>The ready session. It is in <see cref="_live"/> and is not yet idle.</returns>
        /// <exception cref="InvalidOperationException">
        /// <see cref="_created"/> would exceed <see cref="BackFillerProviderDefinition.MaxSessions"/>, no connection number is free,
        /// or a subscriber retired the session during publication before this method returned it.
        /// </exception>
        /// <exception cref="NntpProviderConnectException">
        /// Connect returned a failure, the session is not ready, or the transport threw <see cref="SocketException"/>.
        /// The created-count, connection number, and live membership for this attempt are released before this exception propagates.
        /// </exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> cancels the gate wait or connect. The same reservation is released.</exception>
        /// <remarks>
        /// The application-wide <see cref="_establishment"/> gate is held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/> (TCP through Ready). Slot allocation and
        /// session construction run before the wait so cancelled waiters do not leave half-open sockets.
        /// Every unsuccessful path after the <see cref="_created"/> increment releases that reservation exactly once.
        /// <see cref="RetireSessionAsync"/> does the release when the session was inserted into <see cref="_live"/>;
        /// a concurrent retire or dispose that already removed it does not decrement again.
        /// A reservation that never reached <see cref="_live"/> is released by <see cref="ReleaseUnregisteredReservation"/>.
        /// <see cref="NntpProviderSession.ConnectAsync"/> classifies <see cref="SocketException"/> as a provider failure.
        /// A socket exception that still escapes is translated to <see cref="NntpProviderConnectException"/> here so
        /// replenishment treats it as a failed attempt. The gate is released once, and only after this attempt acquired it.
        /// This method owns the reservation until it returns the session. The completion log and
        /// <see cref="ActiveSessionCountChanged"/> run before that return. If either throws, or a subscriber retires
        /// the session before the return, the session is retired when this method still owns it and the exception
        /// propagates. The caller does not receive the session. Expected disposal failures during that rollback are
        /// logged and do not replace the original exception. Accounting is committed before those observers run, so a
        /// later observer failure cannot skip the release or apply it twice.
        /// </remarks>
        private async Task<NntpProviderSession> CreateReadySessionAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _created) > _provider.MaxSessions)
            {
                Interlocked.Decrement(ref _created);
                throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
            }

            int connectionNumber;
            try
            {
                connectionNumber = AllocateConnectionNumber();
            }
            catch
            {
                Interlocked.Decrement(ref _created);
                throw;
            }

            NntpProviderSession session;
            try
            {
                session = new NntpProviderSession(_provider, _options, _logger, connectionNumber);
            }
            catch
            {
                ReleaseUnregisteredReservation(connectionNumber);
                throw;
            }

            var registered = false;
            var gateHeld = false;
            try
            {
                _live[session] = 0;
                registered = true;

                NntpLogMessages.SessionEstablishmentWaiting(_logger, _provider.Backbone, connectionNumber);
                await _establishment.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateHeld = true;

                NntpLogMessages.SessionEstablishmentStarted(_logger, _provider.Backbone, connectionNumber);
                var failure = await session.ConnectAsync(_transport, cancellationToken).ConfigureAwait(false);
                if (failure is null && session.State == NntpSessionState.Ready)
                {
                    NntpLogMessages.SessionEstablishmentCompleted(_logger, _provider.Backbone, connectionNumber);
                    NotifyActiveSessionCountChanged();
                    if (session.State == NntpSessionState.Ready && _live.ContainsKey(session))
                    {
                        return session;
                    }

                    throw new InvalidOperationException("NNTP session was retired during publication.");
                }

                if (failure?.Kind == ArticleRetrievalKind.Cancelled)
                {
                    TryLogSessionEstablishmentCancelled(connectionNumber);
                }
                else
                {
                    TryLogSessionEstablishmentFailed(connectionNumber, failure?.Reason ?? "connect failed");
                }

                throw new NntpProviderConnectException(
                    failure?.Kind ?? ArticleRetrievalKind.ProviderFailure,
                    failure?.StatusCode,
                    failure?.Reason ?? "NNTP connect failed.");
            }
            // codeql[cs/catch-of-all-exceptions] Failed creation must release its own reservation for every exception type and must not leave the session in the live set.
            catch (Exception ex)
            {
                if (registered && ex is not NntpProviderConnectException)
                {
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        TryLogSessionEstablishmentCancelled(connectionNumber);
                    }
                    else
                    {
                        TryLogSessionEstablishmentFailed(connectionNumber, ex.GetType().Name);
                    }
                }

                try
                {
                    await AbandonFailedCreationAsync(
                            session,
                            connectionNumber,
                            registered,
                            CreationFailureReason(ex, cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (Exception cleanup) when (IsExpectedSessionCleanupFailure(cleanup))
                {
                    TryLogSessionEstablishmentFailed(connectionNumber, cleanup.GetType().Name);
                }

                if (ex is SocketException)
                {
                    throw new NntpProviderConnectException(
                        ArticleRetrievalKind.ProviderFailure,
                        null,
                        ex.GetType().Name);
                }

                throw;
            }
            finally
            {
                if (gateHeld)
                {
                    _establishment.Release();
                }
            }
        }

        /// <summary>
        /// Releases the capacity reserved for a session that did not become ready.
        /// </summary>
        /// <param name="session">Session constructed for this attempt.</param>
        /// <param name="connectionNumber">Slot reserved by <see cref="AllocateConnectionNumber"/>.</param>
        /// <param name="registered">
        /// <see langword="true"/> when <paramref name="session"/> was inserted into <see cref="_live"/>.
        /// </param>
        /// <param name="reason">Retirement text used when <paramref name="registered"/> is <see langword="true"/>.</param>
        /// <remarks>
        /// A registered session is removed through <see cref="RetireSessionAsync"/>, which decrements <see cref="_created"/>
        /// and frees the connection number only when this call wins <c>TryRemove</c>.
        /// A concurrent retire or dispose that already removed the session leaves the counters unchanged.
        /// An unregistered reservation is released by <see cref="ReleaseUnregisteredReservation"/> before the session is disposed.
        /// Expected disposal failures are logged and do not propagate. Replenishment is not requested.
        /// The periodic loop retries on its existing interval.
        /// </remarks>
        private async Task AbandonFailedCreationAsync(
            NntpProviderSession session,
            int connectionNumber,
            bool registered,
            string reason)
        {
            if (registered)
            {
                await RetireSessionAsync(session, reason, replenish: false).ConfigureAwait(false);
                return;
            }

            ReleaseUnregisteredReservation(connectionNumber);
            await DisposeDetachedSessionAsync(session, connectionNumber).ConfigureAwait(false);
        }

        /// <summary>Chooses the retirement text for a creation attempt that threw.</summary>
        /// <param name="failure">Exception that aborted the attempt.</param>
        /// <param name="cancellationToken">Token supplied to <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>
        /// The connect-failure message when <paramref name="failure"/> is <see cref="NntpProviderConnectException"/>.
        /// <c>NNTP connect was cancelled</c> when <paramref name="cancellationToken"/> cancelled an <see cref="OperationCanceledException"/>.
        /// Otherwise the exception type name, which matches <see cref="NntpProviderSession.ConnectAsync"/> failure reasons.
        /// </returns>
        private static string CreationFailureReason(Exception failure, CancellationToken cancellationToken)
        {
            if (failure is NntpProviderConnectException)
            {
                return failure.Message;
            }

            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return "NNTP connect was cancelled";
            }

            return failure.GetType().Name;
        }

        /// <summary>
        /// Returns a connection number and created-count reservation that never entered <see cref="_live"/>.
        /// </summary>
        /// <param name="connectionNumber">Slot from <see cref="AllocateConnectionNumber"/> for this attempt.</param>
        /// <remarks>
        /// Used only before live registration. After registration, <see cref="RetireSessionAsync"/> owns the same release.
        /// Calling both for one attempt frees another session's number or drops <see cref="_created"/> below the live set.
        /// </remarks>
        private void ReleaseUnregisteredReservation(int connectionNumber)
        {
            ReleaseConnectionNumber(connectionNumber);
            Interlocked.Decrement(ref _created);
        }

        /// <summary>Queues <paramref name="session"/> for acquire and starts its DATE loop when keepalive is enabled.</summary>
        /// <param name="session">Reusable session that is no longer leased.</param>
        private void EnqueueIdle(NntpProviderSession session)
        {
            _idle.Enqueue(session);
            StartKeepAlive(session);
        }

        /// <summary>Starts a DATE loop for <paramref name="session"/> unless keepalive is disabled, the pool is disposed, or shutdown has started.</summary>
        /// <param name="session">Idle session. A second registration for the same instance cancels the new loop.</param>
        /// <remarks>The loop task is stored on the registration before the dictionary add. A failed add cancels that new source.</remarks>
        private void StartKeepAlive(NntpProviderSession session)
        {
            if (!_provider.DateKeepAliveEnabled || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var registration = new KeepAliveRegistration(cts)
            {
                Task = RunKeepAliveAsync(session, cts)
            };
            if (!_keepAlives.TryAdd(session, registration))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    ShutdownObservations.AlreadyDisposed(_logger, "duplicate DATE keepalive");
                }
            }
        }

        /// <summary>Cancels and detaches the DATE loop for <paramref name="session"/>. Does not wait for the loop to exit.</summary>
        /// <param name="session">Session leaving the idle queue or being retired.</param>
        private void StopKeepAlive(NntpProviderSession session)
        {
            if (!_keepAlives.TryRemove(session, out var registration))
            {
                return;
            }

            try
            {
                registration.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                ShutdownObservations.AlreadyDisposed(_logger, "DATE keepalive stop");
            }
        }

        /// <summary>
        /// Waits <see cref="BackFillerProviderDefinition.KeepAliveSeconds"/>, then sends DATE without waiting for a busy article command.
        /// </summary>
        /// <param name="session">Idle session this loop owns.</param>
        /// <param name="cts">Loop cancellation, linked to <see cref="_shutdown"/>. Disposed when the loop exits.</param>
        /// <remarks>
        /// A non-reusable session or a failed DATE retires <paramref name="session"/>.
        /// <see cref="NntpProviderSession.SendDateKeepAliveAsync"/> is called with <see cref="_shutdown"/> and without waiting for the busy lock, so an in-use session skips DATE and stays leased.
        /// Cancellation of <paramref name="cts"/> ends the loop without retirement. Any other exception retires the session.
        /// The registration is removed and <paramref name="cts"/> is disposed on the way out.
        /// </remarks>
        private async Task RunKeepAliveAsync(NntpProviderSession session, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var interval = TimeSpan.FromSeconds(_provider.KeepAliveSeconds);
            using (cts)
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(interval, _time, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!session.IsReusable)
                        {
                            await RetireSessionAsync(session, "keepalive found a non-reusable session").ConfigureAwait(false);
                            return;
                        }

                        if (!await session.SendDateKeepAliveAsync(_shutdown.Token).ConfigureAwait(false))
                        {
                            await RetireSessionAsync(session, "DATE keepalive failed").ConfigureAwait(false);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ShutdownObservations.Canceled(_logger, "DATE keepalive");
                }
                // codeql[cs/catch-of-all-exceptions] Keepalive and replenishment failures must retire or log the session and must not escape the background task.
                catch (Exception)
                {
                    // DATE keepalive owns this session. Any transport or clock failure retires it here so the
                    // loop task stays observed and the pool can replace the session. Cancellation is handled above.
                    await RetireSessionAsync(session, "DATE keepalive failed").ConfigureAwait(false);
                }
                finally
                {
                    _ = _keepAlives.TryRemove(session, out _);
                }
        }

        /// <summary>Waits for current DATE loops up to <see cref="_shutdownGrace"/>, then returns even if they are still running.</summary>
        /// <remarks>Timeout and cancellation are swallowed.</remarks>
        private async Task AwaitKeepAlivesAsync()
        {
            var tasks = _keepAlives.Values.Select(static registration => registration.Task).ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).WaitAsync(_shutdownGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ShutdownObservations.TimedOut(_logger, "DATE keepalive drain");
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "DATE keepalive drain");
            }
        }

        /// <summary>
        /// Stops keepalive, frees the connection number, disposes <paramref name="session"/>, and optionally asks the pool to replace it.
        /// </summary>
        /// <param name="session">Live session to drop. A session that is already absent is left alone.</param>
        /// <param name="reason">Text passed to <see cref="NntpLogMessages.SessionRetired"/>.</param>
        /// <param name="replenish"><see langword="true"/> schedules <see cref="RequestReplenish"/> after a session was actually removed.</param>
        /// <remarks>
        /// <c>TryRemove</c> wins the decrement and connection-number release. A second retire, dispose, or creation rollback
        /// observes the loss and does not change the counters again. Those releases run before the retirement log,
        /// the active-count notification, and disposal. Observer failures are contained by <see cref="SignalSessionRetired"/>
        /// so they cannot skip disposal, decrement twice, or recurse through another retire. Expected disposal failures
        /// are logged. Any other disposal exception still requests replenishment when <paramref name="replenish"/> is
        /// <see langword="true"/>, then propagates.
        /// </remarks>
        private async Task RetireSessionAsync(NntpProviderSession session, string reason, bool replenish = true)
        {
            StopKeepAlive(session);
            if (!_live.TryRemove(session, out _))
            {
                return;
            }

            Interlocked.Decrement(ref _created);
            ReleaseConnectionNumber(session.ConnectionNumber);
            SignalSessionRetired(session, reason);
            try
            {
                await DisposeDetachedSessionAsync(session, session.ConnectionNumber).ConfigureAwait(false);
            }
            finally
            {
                if (replenish)
                {
                    RequestReplenish();
                }
            }
        }

        /// <summary>
        /// Logs retirement and notifies <see cref="ActiveSessionCountChanged"/> after the session has left <see cref="_live"/>.
        /// </summary>
        /// <param name="session">Session already removed from <see cref="_live"/>. Its connection number is used only in a diagnostic.</param>
        /// <param name="reason">Text passed to <see cref="NntpLogMessages.SessionRetired"/>.</param>
        /// <remarks>
        /// Both calls are observers. Ordinary failures are contained so <see cref="RetireSessionAsync"/> still disposes
        /// the session once and does not enter retirement again. Process-integrity failures propagate.
        /// A logger that fails is not asked to record its own failure. A subscriber failure is recorded when the logger still works.
        /// </remarks>
        private void SignalSessionRetired(NntpProviderSession session, string reason)
        {
            try
            {
                NntpLogMessages.SessionRetired(_logger, _provider.Backbone, reason);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // Accounting is already committed. The failing logger cannot record this diagnostic. Disposal still runs.
            }

            try
            {
                NotifyActiveSessionCountChanged();
            }
            catch (Exception observer) when (!IsProcessIntegrityFailure(observer))
            {
                TryLogSessionEstablishmentFailed(session.ConnectionNumber, observer.GetType().Name);
            }
        }

        /// <summary>
        /// Disposes a session whose created-count and connection number have already been released.
        /// </summary>
        /// <param name="session">Detached session.</param>
        /// <param name="connectionNumber">Connection number already returned to the free set. Used only in the cleanup diagnostic.</param>
        /// <remarks>
        /// <see cref="OperationCanceledException"/>, <see cref="SocketException"/>, <see cref="IOException"/>, and
        /// <see cref="ObjectDisposedException"/> are logged and do not propagate.
        /// <see cref="ObjectDisposedException"/> is expected when pool dispose and session dispose close the same
        /// transport, and <see cref="StopKeepAlive"/> already treats it as shutdown. Other exceptions propagate so a
        /// programming error is not hidden. This method does not change pool accounting.
        /// </remarks>
        private async Task DisposeDetachedSessionAsync(NntpProviderSession session, int connectionNumber)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) when (IsExpectedSessionCleanupFailure(cleanup))
            {
                TryLogSessionEstablishmentFailed(connectionNumber, cleanup.GetType().Name);
            }
        }

        /// <summary>Reports whether <paramref name="exception"/> is an expected failure while disposing a detached session.</summary>
        /// <param name="exception">Exception thrown by session disposal or another cleanup call on that session.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="OperationCanceledException"/>, <see cref="SocketException"/>,
        /// <see cref="IOException"/>, and <see cref="ObjectDisposedException"/>.
        /// </returns>
        /// <remarks>
        /// <see cref="ObjectDisposedException"/> is included because concurrent pool and session disposal close the same
        /// transport and cancellation source. Programming errors are excluded so they still propagate after the reservation
        /// has been released.
        /// </remarks>
        private static bool IsExpectedSessionCleanupFailure(Exception exception) =>
            exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException;

        /// <summary>Starts <see cref="SuperviseReplenishAsync"/> once for this pool.</summary>
        /// <remarks>A later fill failure does not start a second supervisor. The running supervisor waits one interval and tries again.</remarks>
        private void StartPeriodicReplenish()
        {
            if (Interlocked.Exchange(ref _replenishStarted, 1) == 1)
            {
                return;
            }

            _replenishTask = SuperviseReplenishAsync(_shutdown.Token);
        }

        /// <summary>Starts the periodic loop if needed and runs one observed fill unless the pool is disposed or shutting down.</summary>
        /// <remarks>The observed fill is not awaited. Its failures are logged by <see cref="FillDeficitObservedAsync"/>.</remarks>
        private void RequestReplenish()
        {
            if (Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            StartPeriodicReplenish();
            _ = FillDeficitObservedAsync(_shutdown.Token);
        }

        /// <summary>Fills the deficit once every <see cref="ReplenishInterval"/> until <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation propagates to <see cref="SuperviseReplenishAsync"/>.</param>
        /// <remarks>
        /// Connect failures do not reach this method. <see cref="TryCreateIdleSessionAsync"/> consumes
        /// <see cref="NntpProviderConnectException"/> and <see cref="InvalidOperationException"/>, and
        /// <see cref="CreateReadySessionAsync"/> translates an escaping <see cref="SocketException"/> into
        /// <see cref="NntpProviderConnectException"/> before it leaves. Those attempts end the current round only.
        /// Any other exception, including shutdown cancellation, propagates to <see cref="SuperviseReplenishAsync"/>.
        /// The wait is <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <see cref="_time"/>.
        /// <see cref="TimeProvider.System"/> keeps <see cref="ReplenishInterval"/>.
        /// </remarks>
        private async Task RunReplenishAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ReplenishInterval, _time, cancellationToken).ConfigureAwait(false);
                await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Restarts <see cref="RunReplenishAsync"/> after an unexpected fill failure and stops it on shutdown.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation returns without another pass.</param>
        /// <remarks>
        /// <see cref="NntpProviderConnectException"/> and <see cref="SocketException"/> do not escape
        /// <see cref="FillDeficitAsync"/>: the session classifies a socket failure, and
        /// <see cref="TryCreateIdleSessionAsync"/> consumes the resulting connect exception and
        /// <see cref="InvalidOperationException"/>. Catching those types here would not run.
        /// Any other non-process-integrity exception is an open set. It is logged at warning and the next pass
        /// begins with another <see cref="ReplenishInterval"/> wait inside <see cref="RunReplenishAsync"/>, so the
        /// retry is not immediate and the pool is not left underfilled. Process-integrity failures fault this task.
        /// <see cref="DisposeCoreAsync"/> logs that fault and still retires sessions. Shutdown returns without another pass.
        /// </remarks>
        private async Task SuperviseReplenishAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunReplenishAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ShutdownObservations.Canceled(_logger, "session fill");
                    return;
                }
                catch (Exception ex) when (!IsProcessIntegrityFailure(ex))
                {
                    NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
                }
            }
        }

        /// <summary>
        /// Runs one unawaited fill started by <see cref="RequestReplenish"/>.
        /// </summary>
        /// <param name="cancellationToken">Shutdown token forwarded to the fill.</param>
        /// <remarks>
        /// Nothing awaits this task. Shutdown cancellation is logged and is not a replenishment failure.
        /// Any other non-process-integrity exception is logged and does not replace an exception already thrown to an
        /// awaited caller. Process-integrity failures fault this unawaited task instead of being discarded.
        /// </remarks>
        private async Task FillDeficitObservedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ShutdownObservations.Canceled(_logger, "observed session fill");
            }
            // codeql[cs/catch-of-all-exceptions] This unawaited fill has no caller. Non-process-integrity failures are logged so the task does not become unobserved. Process-integrity failures are excluded by the filter.
            catch (Exception ex) when (!IsProcessIntegrityFailure(ex))
            {
                NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
            }
        }

        /// <summary>
        /// Creates idle sessions until the live count reaches the bound max or one create fails.
        /// A connect failure ends this round. <see cref="SuperviseReplenishAsync"/> waits <see cref="ReplenishInterval"/> before the next pass.
        /// </summary>
        /// <param name="cancellationToken">Cancels the fill-gate wait and session creation. A disposed pool returns without creating.</param>
        private async Task FillDeficitAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (!cancellationToken.IsCancellationRequested
                       && Volatile.Read(ref _disposed) == 0
                       && _live.Count < _provider.MaxSessions)
                {
                    if (!await TryCreateIdleSessionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _fillGate.Release();
            }
        }

        /// <summary>Connects one session and queues it when it becomes ready.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>
        /// <see langword="true"/> when an idle session was queued.
        /// <see langword="false"/> when connect failed, including a <see cref="SocketException"/> reported as <see cref="NntpProviderConnectException"/>, or the pool is already at its max.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        /// <remarks>
        /// <see cref="NntpProviderConnectException"/> and <see cref="InvalidOperationException"/> end the attempt and return
        /// <see langword="false"/>. <see cref="SocketException"/> is translated before it leaves
        /// <see cref="CreateReadySessionAsync"/>, so it does not reach <see cref="RunReplenishAsync"/>.
        /// Any other exception propagates to <see cref="SuperviseReplenishAsync"/>, which logs it and waits one
        /// <see cref="ReplenishInterval"/> before the next pass. <see cref="FillDeficitObservedAsync"/> logs the same
        /// exception for an unawaited fill.
        /// </remarks>
        private async Task<bool> TryCreateIdleSessionAsync(CancellationToken cancellationToken)
        {
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                var session = await CreateReadySessionAsync(cancellationToken).ConfigureAwait(false);
                EnqueueIdle(session);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NntpProviderConnectException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Invokes <see cref="ActiveSessionCountChanged"/> synchronously for the latest pool state.
        /// </summary>
        /// <remarks>
        /// One callback runs at a time, so a subscriber cannot re-enter notification on the same stack.
        /// A caller that arrives during that callback records a pending delivery and returns. The active caller
        /// checks for that pending delivery after the callback returns, including when the callback throws, and
        /// invokes subscribers again. The pending flag is cleared only by the active caller, and the idle transition
        /// loses to a concurrent pending mark, so an update that arrives as the callback finishes is replayed or
        /// starts a new delivery. Subscribers are not invoked while this method holds a pool lock.
        /// An ordinary subscriber exception is replayed past, then rethrown, so publication can still retire the
        /// session. Process-integrity failures propagate without a replay.
        /// </remarks>
        /// <exception cref="Exception">The first ordinary subscriber exception from this delivery, after any pending replay.</exception>
        private void NotifyActiveSessionCountChanged()
        {
            if (!TryBecomeCapacityNotifier())
            {
                return;
            }

            Exception? firstObserverFailure = null;
            var released = false;
            try
            {
                while (true)
                {
                    try
                    {
                        ActiveSessionCountChanged?.Invoke();
                    }
                    catch (Exception observer) when (!IsProcessIntegrityFailure(observer))
                    {
                        firstObserverFailure ??= observer;
                    }

                    if (Interlocked.CompareExchange(
                            ref _capacityNotification,
                            CapacityNotificationActive,
                            CapacityNotificationPending) == CapacityNotificationPending)
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(
                            ref _capacityNotification,
                            CapacityNotificationIdle,
                            CapacityNotificationActive) == CapacityNotificationActive)
                    {
                        released = true;
                        break;
                    }
                }
            }
            finally
            {
                if (!released)
                {
                    Interlocked.Exchange(ref _capacityNotification, CapacityNotificationIdle);
                }
            }

            if (firstObserverFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstObserverFailure).Throw();
            }
        }

        /// <summary>
        /// Becomes the single caller running <see cref="NotifyActiveSessionCountChanged"/>, or records a pending delivery.
        /// </summary>
        /// <returns><see langword="true"/> when the caller must invoke subscribers. <see langword="false"/> when another caller will deliver this update.</returns>
        private bool TryBecomeCapacityNotifier()
        {
            while (true)
            {
                var state = Volatile.Read(ref _capacityNotification);
                if (state == CapacityNotificationIdle
                    && Interlocked.CompareExchange(
                        ref _capacityNotification,
                        CapacityNotificationActive,
                        CapacityNotificationIdle) == CapacityNotificationIdle)
                {
                    return true;
                }

                if (state == CapacityNotificationPending)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _capacityNotification,
                        CapacityNotificationPending,
                        CapacityNotificationActive) == CapacityNotificationActive)
                {
                    return false;
                }
            }
        }

        /// <summary>Writes a cancelled-establishment diagnostic. A logger failure is ignored.</summary>
        /// <param name="connectionNumber">Connection number for the attempt being abandoned.</param>
        /// <remarks>
        /// Called before reservation release. <see cref="ILogger.Log{TState}"/> has no closed exception set.
        /// Ordinary logger failures are ignored so they cannot skip that release or replace the creation exception.
        /// Process-integrity failures propagate. A second log would call the same failing logger.
        /// </remarks>
        private void TryLogSessionEstablishmentCancelled(int connectionNumber)
        {
            try
            {
                NntpLogMessages.SessionEstablishmentCancelled(_logger, _provider.Backbone, connectionNumber);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // The failing logger cannot record this diagnostic.
            }
        }

        /// <summary>Writes an establishment-failure diagnostic. A logger failure is ignored.</summary>
        /// <param name="connectionNumber">Connection number for the attempt. Already released when this records cleanup.</param>
        /// <param name="reason">Exception type name or connect-failure text. No credentials or socket message.</param>
        /// <remarks>
        /// Used both before rollback and after accounting is committed. <see cref="ILogger.Log{TState}"/> has no closed
        /// exception set. Ordinary logger failures are ignored so the original establishment outcome is preserved and
        /// disposal can continue. Process-integrity failures propagate. A second log would call the same failing logger.
        /// </remarks>
        private void TryLogSessionEstablishmentFailed(int connectionNumber, string reason)
        {
            try
            {
                NntpLogMessages.SessionEstablishmentFailed(_logger, _provider.Backbone, connectionNumber, reason);
            }
            catch (Exception diagnostic) when (!IsProcessIntegrityFailure(diagnostic))
            {
                // The failing logger cannot record this diagnostic.
            }
        }

        /// <summary>Reports whether <paramref name="exception"/> must leave an observer or supervisor boundary.</summary>
        /// <param name="exception">Failure from a subscriber, logger, or replenishment pass.</param>
        /// <returns>
        /// <see langword="true"/> for <see cref="OutOfMemoryException"/>, <see cref="StackOverflowException"/>,
        /// <see cref="AccessViolationException"/>, <see cref="AppDomainUnloadedException"/>,
        /// <see cref="BadImageFormatException"/>, and <see cref="CannotUnloadAppDomainException"/>.
        /// </returns>
        /// <remarks>
        /// Subscriber and logger failures are an open set, so the contained failures are every other exception.
        /// These six are the process-integrity failures that must not be logged and continued.
        /// <see cref="StackOverflowException"/> is not reliably catchable; the check documents the policy when a runtime surfaces it.
        /// </remarks>
        private static bool IsProcessIntegrityFailure(Exception exception) =>
            exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or AppDomainUnloadedException
                or BadImageFormatException
                or CannotUnloadAppDomainException;

        /// <summary>Reserves the lowest unused connection number from 1 through the current max.</summary>
        /// <returns>The reserved one-based slot.</returns>
        /// <exception cref="InvalidOperationException">Every slot in that range is already reserved.</exception>
        private int AllocateConnectionNumber()
        {
            lock (_slotGate)
            {
                var limit = _provider.MaxSessions;
                for (var slot = 1; slot <= limit; slot++)
                {
                    if (_usedSlots.Add(slot))
                    {
                        return slot;
                    }
                }
            }

            throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
        }

        /// <summary>Returns <paramref name="connectionNumber"/> to the free set. A number that is not reserved is ignored.</summary>
        /// <param name="connectionNumber">Slot previously returned by <see cref="AllocateConnectionNumber"/>.</param>
        private void ReleaseConnectionNumber(int connectionNumber)
        {
            lock (_slotGate)
            {
                _usedSlots.Remove(connectionNumber);
            }
        }

        /// <summary>Yields until <paramref name="session"/> leaves <see cref="NntpSessionState.Busy"/>.</summary>
        /// <param name="session">Session just taken from the idle queue.</param>
        /// <param name="cancellationToken">Checked on each yield. There is no separate timeout.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        private static async Task WaitUntilNotBusyAsync(NntpProviderSession session, CancellationToken cancellationToken)
        {
            while (session.State == NntpSessionState.Busy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>Cancellation and task for one idle session's DATE loop.</summary>
        /// <param name="cts">Linked to pool shutdown. Disposed when <see cref="RunKeepAliveAsync"/> exits.</param>
        private sealed class KeepAliveRegistration(CancellationTokenSource cts)
        {
            /// <summary>Cancels the DATE loop. Cancelling does not dispose this source.</summary>
            internal CancellationTokenSource Cts { get; } = cts;

            /// <summary>
            /// Loop task. Starts as <see cref="Task.CompletedTask"/> and is replaced before the registration is published.
            /// </summary>
            internal Task Task { get; init; } = Task.CompletedTask;
        }
    }

    /// <summary>Connect-time failure that preserves retrieval classification.</summary>
    internal sealed class NntpProviderConnectException : InvalidOperationException
    {
        /// <summary>Wraps a failed connect so callers map the failure to <see cref="ArticleRetrievalKind"/> without parsing the message.</summary>
        /// <param name="kind">Retrieval classification copied from the failed connect.</param>
        /// <param name="statusCode">NNTP status when the server produced one; otherwise null.</param>
        /// <param name="reason">Exception message. Diagnostic text from the session.</param>
        internal NntpProviderConnectException(ArticleRetrievalKind kind, int? statusCode, string reason)
            : base(reason)
        {
            Kind = kind;
            StatusCode = statusCode;
        }

        /// <summary>Article Work retrieval kind assigned when connect failed before Ready.</summary>
        internal ArticleRetrievalKind Kind { get; }

        /// <summary>Three-digit NNTP response code when the server returned one during connect; otherwise null.</summary>
        internal int? StatusCode { get; }
    }
}
