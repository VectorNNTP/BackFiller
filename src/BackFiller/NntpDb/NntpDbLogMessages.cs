namespace VectorNNTP.BackFiller.NntpDb
{
    /// <summary>Source-generated NntpDB lifecycle log messages. Never includes credentials.</summary>
    internal static partial class NntpDbLogMessages
    {
        /// <summary>Information event 2200, <c>Initializing NntpDB</c>. Filtering can suppress it.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2200,
            Level = LogLevel.Information,
            Message = "Initializing NntpDB")]
        public static partial void Initializing(ILogger logger);

        /// <summary>Information event 2201 after <c>SELECT 1</c> returns 1. Filtering can suppress it.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2201,
            Level = LogLevel.Information,
            Message = "NntpDB startup connectivity check succeeded (SELECT 1)")]
        public static partial void StartupCheckSucceeded(ILogger logger);

        /// <summary>
        /// Error event 2202 when startup fails for a reason other than a bad connection string or cancellation.
        /// The exception is recorded, not thrown. Filtering can suppress the event.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Failure recorded on the event.</param>
        [LoggerMessage(
            EventId = 2202,
            Level = LogLevel.Error,
            Message = "NntpDB startup connectivity check failed; application will not start")]
        public static partial void StartupCheckFailed(ILogger logger, Exception exception);

        /// <summary>Information event 2203 when disposal begins. Filtering can suppress it.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2203,
            Level = LogLevel.Information,
            Message = "NntpDB shutting down")]
        public static partial void ShuttingDown(ILogger logger);

        /// <summary>Information event 2204 after shutdown is recorded. Filtering can suppress it. No connections are closed here.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 2204,
            Level = LogLevel.Information,
            Message = "NntpDB stopped")]
        public static partial void Stopped(ILogger logger);

        /// <summary>
        /// Warning event 2205 before a transient startup retry. <paramref name="exception"/> is recorded, not thrown.
        /// Filtering can suppress the event.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Attempt">How many connect attempts have run, including the one that just failed.</param>
        /// <param name="exception">Transient failure recorded on the event.</param>
        [LoggerMessage(
            EventId = 2205,
            Level = LogLevel.Warning,
            Message = "NntpDB startup connectivity retry {Attempt} after a transient failure")]
        public static partial void StartupRetry(ILogger logger, int Attempt, Exception exception);

        /// <summary>
        /// Error event 2206 for a connection string rejected before retry. <paramref name="exception"/> is recorded, not thrown.
        /// Filtering can suppress the event. The reason text must not contain the connection string.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">Sanitized validation reason.</param>
        /// <param name="exception">Configuration failure recorded on the event.</param>
        [LoggerMessage(
            EventId = 2206,
            Level = LogLevel.Error,
            Message = "NntpDB connection string is invalid: {Reason}")]
        public static partial void InvalidConnectionString(ILogger logger, string Reason, Exception exception);
    }
}
