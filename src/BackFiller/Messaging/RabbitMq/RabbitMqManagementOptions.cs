namespace VectorNNTP.BackFiller.Messaging.RabbitMq
{
    /// <summary>
    /// RabbitMQ Management HTTP API settings used for BackFiller ArticleWork availability discovery.
    /// </summary>
    /// <remarks>
    /// Nested under <see cref="RabbitMqOptions"/> as <c>RabbitMQ:Management</c>.
    /// Optional for connectivity-only hosts. Credentials are
    /// <see cref="RabbitMqOptions.Username"/> / <see cref="RabbitMqOptions.Password"/>
    /// (the same RabbitMQ account used for management access). Never log those secrets.
    /// </remarks>
    internal sealed class RabbitMqManagementOptions
    {
        /// <summary>
        /// Absolute base URL of the RabbitMQ Management HTTP API root
        /// (scheme + host + port only; no <c>/api</c> path). Example:
        /// <c>http://rabbit-01.example.net:15672</c>.
        /// </summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// Per-request timeout for Management API calls, in seconds.
        /// </summary>
        public int? RequestTimeoutSeconds { get; set; } = 5;
    }
}
