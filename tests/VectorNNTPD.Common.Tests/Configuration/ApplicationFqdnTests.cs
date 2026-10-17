using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks the shared FQDN builder and the distinct application prefixes.
    /// </summary>
    public sealed class ApplicationFqdnTests
    {
        /// <summary>
        /// Asserts that <see cref="ApplicationFqdn.Build"/> returns the prefix, a two-digit invariant server id, and the DNS suffix, and that <see cref="ApplicationFqdn.FormatHostLabel"/> is that prefix plus the same id.
        /// </summary>
        /// <param name="prefix">Application host prefix passed to the builder.</param>
        /// <param name="serverId">Server id that must be formatted as two digits.</param>
        /// <param name="dnsSuffix">DNS suffix appended after the host label.</param>
        /// <param name="expected">Canonical FQDN the builder must return.</param>
        [Theory]
        [InlineData("nntpd", 1, "usenet.ninja", "nntpd01.usenet.ninja")]
        [InlineData("nntpd", 8, "usenet.ninja", "nntpd08.usenet.ninja")]
        [InlineData("nntpd", 99, "usenet.ninja", "nntpd99.usenet.ninja")]
        [InlineData("backfiller", 1, "usenet.ninja", "backfiller01.usenet.ninja")]
        [InlineData("backfiller", 8, "usenet.ninja", "backfiller08.usenet.ninja")]
        [InlineData("backfiller", 99, "usenet.ninja", "backfiller99.usenet.ninja")]
        public void Build_uses_fixed_prefix_two_digit_id_and_suffix(
            string prefix,
            int serverId,
            string dnsSuffix,
            string expected)
        {
            Assert.Equal(expected, ApplicationFqdn.Build(prefix, serverId, dnsSuffix));
            Assert.Equal(
                prefix + serverId.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                ApplicationFqdn.FormatHostLabel(prefix, serverId));
        }

        /// <summary>
        /// Asserts that the same server id under the <c>nntpd</c> and <c>backfiller</c> prefixes produces different <c>usenet.ninja</c> names, each starting with its own prefix.
        /// </summary>
        /// <param name="serverId">Shared server id applied to both prefixes.</param>
        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(99)]
        public void Distinct_application_prefixes_never_share_an_fqdn(int serverId)
        {
            var nntpd = ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja");
            var backfiller = ApplicationFqdn.Build("backfiller", serverId, "usenet.ninja");
            Assert.NotEqual(nntpd, backfiller);
            Assert.StartsWith("nntpd", nntpd, StringComparison.Ordinal);
            Assert.StartsWith("backfiller", backfiller, StringComparison.Ordinal);
        }

        /// <summary>
        /// Asserts that mixed-case prefixes and DNS suffixes, including a trailing dot, canonicalize to invariant lowercase FQDNs.
        /// </summary>
        [Fact]
        public void Build_canonicalizes_to_invariant_lowercase()
        {
            Assert.Equal(
                "nntpd01.usenet.ninja",
                ApplicationFqdn.Build("NNTPD", 1, "Usenet.Ninja."));
            Assert.Equal(
                "backfiller08.usenet.ninja",
                ApplicationFqdn.Build("BackFiller", 8, "USENET.NINJA"));
        }
    }
}
