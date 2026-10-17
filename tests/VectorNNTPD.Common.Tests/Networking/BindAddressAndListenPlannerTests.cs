using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Tests.TestDoubles;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.Common.Tests.Networking
{
    /// <summary>
    /// Locks bind-address resolution, DNS eligibility, cleartext port validation, and listen-endpoint planning.
    /// </summary>
    public sealed class BindAddressAndListenPlannerTests
    {
        /// <summary>
        /// Asserts that two explicit assigned addresses resolve as one IPv4 address and one IPv6 address.
        /// </summary>
        [Fact]
        public void Resolve_ExplicitIpv4AndIpv6()
        {
            var v4 = IPAddress.Parse("198.18.0.66");
            var v6 = IPAddress.Parse("2001:db8::66");
            var resolved = CreateResolver(v4, v6).Resolve(Options([v4.ToString(), v6.ToString()]));

            Assert.Equal(2, resolved.All.Count);
            Assert.Contains(v4, resolved.IPv4);
            Assert.Contains(v6, resolved.IPv6);
        }

        /// <summary>
        /// Asserts that <c>*</c> expands to the eligible public, private, and documentation addresses and drops loopback.
        /// </summary>
        [Fact]
        public void Resolve_WildcardExpandsEligibleAddressesAndDropsLoopback()
        {
            var publicV4 = IPAddress.Parse("203.0.113.10");
            var privateV4 = IPAddress.Parse("10.0.0.5");
            var v6 = IPAddress.Parse("2001:db8::5");
            var resolved = CreateResolver(
                    publicV4,
                    privateV4,
                    v6,
                    IPAddress.Loopback,
                    IPAddress.Parse("169.254.10.10"))
                .Resolve(Options(["*"]));

            Assert.Equal(3, resolved.All.Count);
            Assert.DoesNotContain(IPAddress.Loopback, resolved.All);
        }

        /// <summary>
        /// Asserts that a repeated explicit address plus <c>*</c> resolves to that single address.
        /// </summary>
        [Fact]
        public void Resolve_MultipleExplicitAddressesDeduplicate()
        {
            var v4 = IPAddress.Parse("198.18.0.30");
            var resolved = CreateResolver(v4).Resolve(Options([v4.ToString(), v4.ToString(), "*"]));
            Assert.Single(resolved.All);
        }

        /// <summary>
        /// Asserts that an explicit loopback bind resolves to a set with no DNS-eligible address.
        /// </summary>
        [Fact]
        public void Resolve_ExplicitLoopbackIsNotDnsEligible()
        {
            var resolved = CreateResolver(IPAddress.Loopback).Resolve(Options(["127.0.0.1"]));
            Assert.False(resolved.HasAny);
        }

        /// <summary>
        /// Asserts that an explicit address the assignee does not own fails validation with a <c>BindAddress</c> failure.
        /// </summary>
        [Fact]
        public void Validator_RejectsNonLocalExplicitAddress()
        {
            var options = ValidOptions();
            options.BindAddress = ["198.51.100.10"];
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalAssignee(assignAll: false))
                .Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("BindAddress", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that cleartext <c>BindPort</c> values 0 and 65536 still validate, and that failures do not mention <c>BindPort</c> except as part of <c>BindPortTls</c>.
        /// </summary>
        /// <param name="port">Cleartext port that must not be required.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Validator_DoesNotRequireCleartextBindPort(int port)
        {
            var options = ValidOptions();
            options.BindPort = port;
            var result = new AcmeCloudflareOptionsValidator(new FakeLocalAssignee(assignAll: true))
                .Validate(null, options);
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(
                result.Failures ?? [],
                static f => f.Contains("BindPort", StringComparison.Ordinal)
                            && !f.Contains("BindPortTls", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that planning <c>*</c> yields one dual-mode <see cref="IPAddress.IPv6Any"/> endpoint on the requested port.
        /// </summary>
        [Fact]
        public void Planner_Star_UsesDualStackIpv6Any()
        {
            var planned = ListenEndpointPlanner.Plan(["*"], 1190);
            Assert.Single(planned);
            Assert.Equal(IPAddress.IPv6Any, planned[0].Address);
            Assert.True(planned[0].DualMode);
            Assert.Equal(1190, planned[0].Port);
        }

        /// <summary>
        /// Asserts that an explicit IPv4 loopback plan binds only that address and is not dual-mode.
        /// </summary>
        [Fact]
        public void Planner_ExplicitAddress_BindsOnlyThatAddress()
        {
            var planned = ListenEndpointPlanner.Plan(["127.0.0.1"], 1190);
            Assert.Single(planned);
            Assert.Equal(IPAddress.Loopback, planned[0].Address);
            Assert.False(planned[0].DualMode);
        }

        /// <summary>
        /// Asserts that loopback addresses are not DNS-eligible and that the documented global unicast samples are.
        /// </summary>
        /// <param name="text">Address text passed to <see cref="IpAddressEligibility.IsEligibleForDns"/>.</param>
        /// <param name="expected">Expected eligibility.</param>
        [Theory]
        [InlineData("127.0.0.1", false)]
        [InlineData("10.0.0.1", true)]
        [InlineData("2001:db8::1", true)]
        [InlineData("::1", false)]
        public void Eligibility_MatchesNntpdRules(string text, bool expected)
        {
            Assert.Equal(expected, IpAddressEligibility.IsEligibleForDns(IPAddress.Parse(text)));
        }

        /// <summary>
        /// Builds a resolver whose assignee reports exactly <paramref name="local"/>.
        /// </summary>
        /// <param name="local">Addresses treated as locally assigned.</param>
        /// <returns>The resolver used by the resolve facts.</returns>
        private static BindAddressResolver CreateResolver(params IPAddress[] local) =>
            new(new FakeLocalAssignee(local), NullLogger<BindAddressResolver>.Instance);

        /// <summary>
        /// Builds options with <paramref name="bind"/> and both ports set to 1190.
        /// </summary>
        /// <param name="bind">Bind-address tokens.</param>
        /// <returns>Options for resolution facts.</returns>
        private static AcmeCloudflareOptions Options(string[] bind) =>
            new()
            {
                BindAddress = bind,
                BindPort = 1190,
                BindPortTls = 1190,
            };

        /// <summary>
        /// Builds a complete BackFiller options instance that validation can accept when the assignee allows the bind.
        /// </summary>
        /// <returns>Options with wildcard bind, matching ports, ACME material, and the BackFiller FQDN.</returns>
        private static AcmeCloudflareOptions ValidOptions() =>
            new()
            {
                BindAddress = ["*"],
                BindPort = 1190,
                BindPortTls = 1190,
                AcmeEmail = "ops@example.org",
                AcmeCertificatePassword = TestCertificateFactory.Password,
                CloudFlareApiKey = "unit-test-cloudflare-key-not-secret",
                CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d",
                Fqdn = "backfiller01.usenet.ninja",
                IncludeNewsHostnameInCertificate = false,
            };
    }
}
