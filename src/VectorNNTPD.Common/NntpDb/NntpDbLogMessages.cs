namespace VectorNNTPD.Common.NntpDb
{
    /// <summary>Source-generated NntpDB lifecycle log messages. Never includes credentials.</summary>
    internal static partial class NntpDbLogMessages
    {
        /// <summary>Emits event 2200, "Initializing NntpDB".</summary>
        /// <param name="logger">NntpDB logger.</param>
        [LoggerMessage(
            EventId = 2200,
            Level = LogLevel.Information,
            Message = "Initializing NntpDB")]
        public static partial void Initializing(ILogger logger);

        /// <summary>Emits event 2201 after <c>SELECT 1</c> returns 1.</summary>
        /// <param name="logger">NntpDB logger.</param>
        [LoggerMessage(
            EventId = 2201,
            Level = LogLevel.Information,
            Message = "NntpDB startup connectivity check succeeded (SELECT 1)")]
        public static partial void StartupCheckSucceeded(ILogger logger);

        /// <summary>Emits event 2202 when the startup check fails and the host will not start.</summary>
        /// <param name="logger">NntpDB logger.</param>
        /// <param name="exception">Failure that prevented startup. The connection string is not included.</param>
        [LoggerMessage(
            EventId = 2202,
            Level = LogLevel.Error,
            Message = "NntpDB startup connectivity check failed; application will not start")]
        public static partial void StartupCheckFailed(ILogger logger, Exception exception);

        /// <summary>Emits event 2203 when dispose begins.</summary>
        /// <param name="logger">NntpDB logger.</param>
        [LoggerMessage(
            EventId = 2203,
            Level = LogLevel.Information,
            Message = "NntpDB shutting down")]
        public static partial void ShuttingDown(ILogger logger);

        /// <summary>Emits event 2204 when dispose has stopped accepting sessions.</summary>
        /// <param name="logger">NntpDB logger.</param>
        [LoggerMessage(
            EventId = 2204,
            Level = LogLevel.Information,
            Message = "NntpDB stopped")]
        public static partial void Stopped(ILogger logger);

        /// <summary>Emits event 2205 before another startup attempt after a transient failure.</summary>
        /// <param name="logger">NntpDB logger.</param>
        /// <param name="Attempt">One-based attempt count, including the attempt that just failed.</param>
        /// <param name="exception">Transient failure. The connection string is not included.</param>
        [LoggerMessage(
            EventId = 2205,
            Level = LogLevel.Warning,
            Message = "NntpDB startup connectivity retry {Attempt} after a transient failure")]
        public static partial void StartupRetry(ILogger logger, int Attempt, Exception exception);

        /// <summary>Emits event 2206 when the connection string is rejected.</summary>
        /// <param name="logger">NntpDB logger.</param>
        /// <param name="Reason">Rejection text that does not include the connection string.</param>
        /// <param name="exception">Parser or validation failure.</param>
        [LoggerMessage(
            EventId = 2206,
            Level = LogLevel.Error,
            Message = "NntpDB connection string is invalid: {Reason}")]
        public static partial void InvalidConnectionString(ILogger logger, string Reason, Exception exception);
    }
}
