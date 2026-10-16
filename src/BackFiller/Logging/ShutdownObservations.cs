namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>Debug records for shutdown and cleanup paths that continue after an expected failure.</summary>
    /// <remarks>
    /// Each method writes at <see cref="LogLevel.Debug"/>. Host log filtering can suppress a call without changing runtime behavior.
    /// These methods do not throw; they only record that shutdown continued.
    /// </remarks>
    internal static partial class ShutdownObservations
    {
        /// <summary>Records that a cancelled operation ended a shutdown wait.</summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Operation">Short name of the wait that was cancelled.</param>
        [LoggerMessage(
            EventId = 9100,
            Level = LogLevel.Debug,
            Message = "Shutdown continued after {Operation} was canceled")]
        internal static partial void Canceled(ILogger logger, string Operation);

        /// <summary>Records that cleanup found a resource that was already disposed.</summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Operation">Short name of the cleanup that was skipped.</param>
        [LoggerMessage(
            EventId = 9101,
            Level = LogLevel.Debug,
            Message = "Cleanup skipped for {Operation} because the resource was already disposed")]
        internal static partial void AlreadyDisposed(ILogger logger, string Operation);

        /// <summary>Records that a bounded wait ended because its timeout elapsed.</summary>
        /// <param name="logger">Logger that receives the debug event.</param>
        /// <param name="Operation">Short name of the wait that timed out.</param>
        [LoggerMessage(
            EventId = 9102,
            Level = LogLevel.Debug,
            Message = "Shutdown continued after {Operation} timed out")]
        internal static partial void TimedOut(ILogger logger, string Operation);
    }
}
