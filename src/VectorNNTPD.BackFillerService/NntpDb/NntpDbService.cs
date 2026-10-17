using MySqlConnector;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Core;

namespace VectorNNTPD.BackFillerService.NntpDb
{
    /// <summary>
    /// Application-level NntpDB service: configuration, lifecycle, and startup connectivity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hosting application owns startup ordering. MySqlConnector owns physical
    /// connection pooling. This type does not cache, idle-reap, or reserve connections,
    /// and it does not poll or own shared configuration.
    /// </para>
    /// <para>
    /// Startup validates the connection string with
    /// <c>MySqlConnectionStringBuilder</c>, opens a logical connection, executes
    /// <c>SELECT 1</c>, disposes that connection, and fails the host when the check
    /// does not succeed. A malformed connection string fails immediately without
    /// retry. There is no degraded or in-memory fallback.
    /// </para>
    /// </remarks>
    internal sealed class NntpDbService : IApplicationService, IAsyncDisposable
    {
        /// <summary>Delay between transient startup attempts, shortened when less than this remains before the deadline.</summary>
        private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>Opens logical connections. This service does not dispose the factory.</summary>
        private readonly INntpDbConnectionFactory _factory;

        /// <summary>Connection string and startup timeout. Read at start and on each <see cref="OpenAsync"/>.</summary>
        private readonly IOptions<NntpDbOptions> _options;

        /// <summary>Lifecycle logger. Events are defined on <see cref="NntpDbLogMessages"/> and can be filtered out.</summary>
        private readonly ILogger<NntpDbService> _logger;

        /// <summary>Clock for the startup deadline and the delay between retries.</summary>
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// <c>1</c> while <see cref="StartAsync"/> is in progress or has succeeded. A failed start sets it back to <c>0</c>.
        /// A second call returns immediately while the value is <c>1</c>.
        /// </summary>
        private int _started;

        /// <summary><c>1</c> only after <c>SELECT 1</c> succeeds. Dispose and a failed start set it to <c>0</c>, which makes <see cref="OpenAsync"/> throw.</summary>
        private int _accepting;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>. Later calls return immediately.</summary>
        private int _disposed;

        /// <summary>
        /// Creates the service with the system clock. Startup runs <c>SELECT 1</c> under a configured deadline and fails the host when MySQL is unavailable.
        /// </summary>
        /// <param name="factory">Opens short-lived logical connections.</param>
        /// <param name="options">Validated NntpDB options.</param>
        /// <param name="logger">Lifecycle logger.</param>
        public NntpDbService(
            INntpDbConnectionFactory factory,
            IOptions<NntpDbOptions> options,
            ILogger<NntpDbService> logger)
            : this(factory, options, logger, TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates the service with an injectable clock for deterministic startup retry tests.
        /// </summary>
        /// <param name="factory">Opens short-lived logical connections.</param>
        /// <param name="options">Validated NntpDB options.</param>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="timeProvider">Clock used for the startup deadline and retry delay.</param>
        internal NntpDbService(
            INntpDbConnectionFactory factory,
            IOptions<NntpDbOptions> options,
            ILogger<NntpDbService> logger,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            _factory = factory;
            _options = options;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        /// <inheritdoc />
        public string Name => "NntpDb";

        /// <inheritdoc />
        public Task? Execution => null;

        /// <summary>
        /// Whether <see cref="StartAsync"/> entered its critical section and was not rolled back by failure (tests).
        /// </summary>
        private bool HasStarted => Volatile.Read(ref _started) == 1;

        /// <summary>
        /// Whether <see cref="OpenAsync"/> may open sessions after successful startup health check (tests).
        /// </summary>
        private bool IsAccepting => Volatile.Read(ref _accepting) == 1;

        /// <summary>Startup <c>SELECT 1</c> attempts counted across transient retries (tests).</summary>
        private int StartupConnectAttempts { get; set; }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            NntpDbLogMessages.Initializing(_logger);

            try
            {
                await ConnectWithStartupBudgetAsync(_options.Value, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _accepting, 1);
            }
            catch (Exception ex)
            {
                if (ex is NntpDbConfigurationException configuration)
                {
                    NntpDbLogMessages.InvalidConnectionString(_logger, configuration.Reason, configuration);
                }
                else if (ex is not OperationCanceledException)
                {
                    NntpDbLogMessages.StartupCheckFailed(_logger, ex);
                }

                Volatile.Write(ref _accepting, 0);
                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Opens a logical MySQL connection using the configured NntpDB connection string.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the open.</param>
        /// <returns>An open logical session. Dispose returns it to MySqlConnector's pool.</returns>
        /// <exception cref="InvalidOperationException">Startup has not succeeded, or the service has stopped.</exception>
        internal async ValueTask<INntpDbSession> OpenAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _accepting) == 0)
            {
                throw new InvalidOperationException("NntpDB is not accepting database work.");
            }

            return await _factory.OpenAsync(_options.Value.ConnectionString, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            Volatile.Write(ref _accepting, 0);
            NntpDbLogMessages.ShuttingDown(_logger);
            NntpDbLogMessages.Stopped(_logger);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Validates the connection string, then opens a session and requires <c>SELECT 1</c> to return 1 before the startup deadline.
        /// </summary>
        /// <param name="options">Connection string and <see cref="NntpDbOptions.StartupTimeout"/>.</param>
        /// <param name="cancellationToken">Cancels the open, the query, and the retry delay. Cancellation is not retried.</param>
        /// <remarks>
        /// A malformed connection string, an authentication failure, a <c>SELECT 1</c> result other than 1, and cancellation propagate immediately.
        /// Transient provider, I/O, and timeout failures wait up to one second, or the time remaining, and try again until the deadline.
        /// The deadline failure is <see cref="NntpDbUnavailableException"/>.
        /// </remarks>
        private async Task ConnectWithStartupBudgetAsync(NntpDbOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NntpDbConnectionString.Validate(options.ConnectionString);

            var deadline = _timeProvider.GetUtcNow() + options.StartupTimeout;
            StartupConnectAttempts = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartupConnectAttempts++;
                try
                {
                    await using var connection = await _factory
                        .OpenAsync(options.ConnectionString, cancellationToken)
                        .ConfigureAwait(false);
                    var result = await connection.SelectOneAsync(cancellationToken).ConfigureAwait(false);
                    if (result != 1)
                    {
                        throw new NntpDbUnavailableException("MySQL health query SELECT 1 did not return 1.");
                    }

                    NntpDbLogMessages.StartupCheckSucceeded(_logger);
                    return;
                }
                catch (NntpDbConfigurationException)
                {
                    throw;
                }
                catch (NntpDbAuthenticationException)
                {
                    throw;
                }
                catch (NntpDbUnavailableException ex) when (IsSelectOneFailure(ex))
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is NntpDbUnavailableException or MySqlException or IOException or TimeoutException)
                {
                    if (_timeProvider.GetUtcNow() >= deadline)
                    {
                        throw new NntpDbUnavailableException(
                            "NntpDB startup connectivity check failed after the configured startup timeout.",
                            ex);
                    }

                    NntpDbLogMessages.StartupRetry(_logger, StartupConnectAttempts, ex);
                    var remaining = deadline - _timeProvider.GetUtcNow();
                    var delay = remaining < StartupRetryDelay ? remaining : StartupRetryDelay;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Returns whether <paramref name="exception"/> is the wrapped health-query failure that must not be retried.
        /// </summary>
        /// <param name="exception">Failure from <see cref="INntpDbSession.SelectOneAsync"/>.</param>
        /// <returns><see langword="true"/> when the message contains the ordinal text <c>SELECT 1</c>.</returns>
        private static bool IsSelectOneFailure(NntpDbUnavailableException exception) =>
            exception.Message.Contains("SELECT 1", StringComparison.Ordinal);
    }
}
