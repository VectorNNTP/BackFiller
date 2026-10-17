namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Application health used for systemd watchdog keep-alive decisions.
    /// </summary>
    internal interface IApplicationHealth
    {
        /// <summary>
        /// Gets a value indicating whether watchdog keep-alives may be sent.
        /// </summary>
        /// <remarks>
        /// True only after successful host start (all hosted services started), while shutdown
        /// has not begun, no unexpected supervised-service termination has been observed, and the
        /// published TLS certificate is not blocking keep-alives. The certificate condition clears
        /// when a usable replacement is published. Unexpected termination does not.
        /// </remarks>
        bool IsHealthyForWatchdog { get; }
    }
}
