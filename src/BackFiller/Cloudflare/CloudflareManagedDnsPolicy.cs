namespace VectorNNTP.BackFiller.Cloudflare
{
    /// <summary>
    /// Managed A/AAAA DNS attributes enforced by reconciliation.
    /// </summary>
    internal static class CloudflareManagedDnsPolicy
    {
        /// <summary>TTL (seconds) required on every managed A/AAAA record.</summary>
        internal const int ManagedTtl = 300;

        /// <summary>Proxy flag required on every managed A/AAAA record (DNS-only).</summary>
        internal const bool ManagedProxied = false;

        /// <summary>
        /// Returns whether a listed A/AAAA record already matches managed content attributes
        /// (TTL and proxy), given that its content is already the desired address.
        /// </summary>
        /// <param name="record">Cloudflare list result. Null TTL or proxy returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when TTL is <see cref="ManagedTtl"/> and proxied is <see cref="ManagedProxied"/>.</returns>
        internal static bool MatchesManagedAttributes(CloudflareDnsRecord record) =>
            record is { Ttl: ManagedTtl, Proxied: ManagedProxied };
    }
}
