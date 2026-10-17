namespace VectorNNTPD.BackFillerService.Dns
{
    /// <summary>
    /// DNS resource record TYPE/QTYPE and CLASS constants used by the owned ACME DNS wire stack (RFC 1035).
    /// </summary>
    /// <remarks>
    /// Only A, AAAA, NS, and TXT are implemented. EDNS, DNSSEC, MX, CNAME, SRV, and PTR are intentionally absent.
    /// </remarks>
    internal static class DnsRecordType
    {
        /// <summary>A (IPv4 address).</summary>
        internal const ushort A = 1;

        /// <summary>NS (authoritative name server).</summary>
        internal const ushort Ns = 2;

        /// <summary>TXT (character-string payloads; DNS-01 challenge tokens).</summary>
        internal const ushort Txt = 16;

        /// <summary>AAAA (IPv6 address).</summary>
        internal const ushort Aaaa = 28;

        /// <summary>CLASS IN (Internet).</summary>
        internal const ushort ClassIn = 1;
    }
}
