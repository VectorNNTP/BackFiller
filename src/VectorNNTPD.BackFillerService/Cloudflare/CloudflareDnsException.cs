namespace VectorNNTPD.BackFillerService.Cloudflare
{
    /// <summary>
    /// Raised when a Cloudflare DNS API operation fails.
    /// </summary>
    /// <remarks>
    /// Messages must never include API credentials. HTTP authorization headers are not stored.
    /// An <see cref="IsOutcomeUncertain"/> failure means the remote DNS state may already include
    /// the attempted mutation; callers must re-read Cloudflare state rather than assume no change.
    /// <see cref="IsPermanentFailure"/> failures must not be retried as if they were transient.
    /// </remarks>
    internal sealed class CloudflareDnsException : Exception
    {
        /// <summary>Creates a DNS API failure with message only; status and Cloudflare codes remain unset.</summary>
        /// <param name="message">Safe diagnostic message without secrets.</param>
        internal CloudflareDnsException(string message)
            : base(message)
        {
        }

        /// <summary>Creates a DNS API failure wrapping a transport or parse exception.</summary>
        /// <param name="message">Safe diagnostic message without secrets.</param>
        /// <param name="innerException">Transport, timeout, or deserialization failure.</param>
        internal CloudflareDnsException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>HTTP status when the failure originated from a Cloudflare response; null for pre-request failures.</summary>
        internal int? StatusCode { get; init; }

        /// <summary>Cloudflare <c>errors[].code</c> values copied from the envelope when present.</summary>
        internal IReadOnlyList<int> CloudflareErrorCodes { get; init; } = [];

        /// <summary>True when <see cref="StatusCode"/> is 429.</summary>
        private bool IsRateLimited => StatusCode == 429;

        /// <summary>
        /// When true, Cloudflare may already have applied a create/update/delete despite the local failure
        /// (timeout, transport error, or cancel after send).
        /// </summary>
        internal bool IsOutcomeUncertain { get; init; }

        /// <summary>
        /// When true, callers must not retry as transient (auth, config, validation, or explicit 4xx except 429).
        /// </summary>
        internal bool IsPermanentFailure { get; init; }

        /// <summary>Short operation label stored on the exception for logs (for example <c>List</c> or HTTP method plus path).</summary>
        internal string? FailedOperation { get; init; }

        /// <summary>
        /// Returns whether an HTTP status should be treated as a permanent Cloudflare API failure.
        /// </summary>
        /// <remarks>429 is transient. Other 4xx (including 401/403) are permanent for this host.</remarks>
        internal static bool IsPermanentHttpStatus(int? statusCode) =>
            statusCode is >= 400 and < 500 and not 429;
    }
}
