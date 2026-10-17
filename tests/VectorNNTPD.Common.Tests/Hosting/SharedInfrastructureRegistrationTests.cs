using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Hosting
{
    /// <summary>
    /// Locks the shared TLS certificate provider, bind-address validation, TLS-only port rules, and the Cloudflare DNS application-service adapter.
    /// </summary>
    public sealed class SharedInfrastructureRegistrationTests
    {
        /// <summary>
        /// Asserts that a new <see cref="TlsCertificateContextProvider"/> is both an ACME certificate publisher and a TLS context provider, and that it is not available before a certificate is published.
        /// </summary>
        [Fact]
        public void Tls_certificate_provider_is_the_shared_acme_publisher()
        {
            var provider = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            Assert.IsAssignableFrom<IAcmeCertificatePublisher>(provider);
            Assert.IsAssignableFrom<ITlsCertificateContextProvider>(provider);
            Assert.False(provider.IsAvailable);
        }

        /// <summary>
        /// Asserts that <c>*</c> produces no bind failures when every address is assigned, and that an unassigned explicit address fails with text naming the address and that it is not assigned.
        /// </summary>
        [Fact]
        public void Bind_address_validation_accepts_wildcard_and_rejects_unassigned()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["*"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);

            options.BindAddress = ["198.51.100.10"];
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: false),
                failures);
            Assert.Contains(failures, static f => f.Contains("198.51.100.10", StringComparison.Ordinal));
            Assert.Contains(failures, static f => f.Contains("not assigned", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that loopback and family-wildcard literals produce no bind failures when the assignee accepts every address.
        /// </summary>
        /// <param name="token">Bind-address token expected to be accepted.</param>
        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("0.0.0.0")]
        [InlineData("::")]
        public void Bind_address_validation_accepts_assigned_literals_and_family_wildcards(string token)
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = [token],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);
        }

        /// <summary>
        /// Asserts that a non-address token fails with the invalid IPv4 or IPv6 message.
        /// </summary>
        [Fact]
        public void Bind_address_validation_rejects_invalid_tokens()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["not-an-address"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Contains(failures, static f => f.Contains("not a valid IPv4 or IPv6 address", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that two assigned literals produce no failures and that an empty bind list normalizes to <c>*</c>.
        /// </summary>
        [Fact]
        public void Bind_address_validation_accepts_multiple_assigned_addresses_and_normalizes_empty_to_wildcard()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["127.0.0.1", "::1"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);

            options.BindAddress = [];
            AcmeCloudflareOptionsValidator.NormalizeBindAddresses(options);
            Assert.Equal(["*"], options.BindAddress);
        }

        /// <summary>
        /// Asserts that <see cref="TlsOnlyAcmeCloudflareOptionsValidator"/> accepts a TCP port in 1 through 65535 as <c>BindPortTls</c>.
        /// </summary>
        /// <param name="port">TLS port expected to be accepted.</param>
        [Theory]
        [InlineData(1)]
        [InlineData(119)]
        [InlineData(563)]
        [InlineData(65535)]
        public void Tls_only_validator_accepts_usable_ports(int port)
        {
            var options = new AcmeCloudflareOptions { BindPortTls = port };
            Assert.True(new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options).Succeeded);
        }

        /// <summary>
        /// Asserts that a TLS port of 0, negative, or above 65535 fails validation with text that mentions TLS-only.
        /// </summary>
        /// <param name="port">TLS port expected to be rejected.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Tls_only_validator_rejects_disabled_or_invalid_ports(int port)
        {
            var options = new AcmeCloudflareOptions { BindPortTls = port };
            var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("TLS-only", StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that bind-address collection, DNS reconciliation, and the TLS certificate provider are members of VectorNNTPD.Common, and that the application-service adapter lives in <c>VectorNNTPD.Common.Cloudflare</c>.
        /// </summary>
        [Fact]
        public void Shared_bind_validator_is_the_same_method_nntpd_and_backfiller_use()
        {
            Assert.NotNull(typeof(AcmeCloudflareOptionsValidator).GetMethod(
                nameof(AcmeCloudflareOptionsValidator.CollectBindAddressFailures),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
            Assert.Equal(
                typeof(AcmeCloudflareOptionsValidator).Assembly,
                typeof(CloudflareDnsReconciliationService).Assembly);
            Assert.Equal(
                typeof(AcmeCloudflareOptionsValidator).Assembly,
                typeof(TlsCertificateContextProvider).Assembly);
            Assert.Equal(
                typeof(CloudflareDnsReconciliationService).Assembly,
                typeof(CloudflareDnsReconciliationApplicationService).Assembly);
            Assert.Equal(
                "VectorNNTPD.Common.Cloudflare",
                typeof(CloudflareDnsReconciliationApplicationService).Namespace);
        }

        /// <summary>
        /// Asserts that the DNS reconciliation application service exposes the inner service name and has no separate execution delegate.
        /// </summary>
        [Fact]
        public void Dns_application_service_adapter_delegates_name_and_execution()
        {
            var options = new AcmeCloudflareOptions
            {
                Fqdn = "backfiller01.usenet.ninja",
                CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
                BindAddress = ["198.18.0.10"],
                CloudFlareApiKey = "unit-test-key",
            };
            var inner = new CloudflareDnsReconciliationService(
                Options.Create(options),
                new BindAddressResolver(
                    new AssigningLocalIpAddressAssignee(assignAll: true),
                    NullLogger<BindAddressResolver>.Instance),
                new NoOpCloudflareDnsReconciler(),
                NullLogger<CloudflareDnsReconciliationService>.Instance);
            var adapter = new CloudflareDnsReconciliationApplicationService(inner);

            Assert.Equal(CloudflareDnsReconciliationService.Name, adapter.Name);
            Assert.Equal("CloudflareDnsReconciliation", adapter.Name);
            Assert.Null(adapter.Execution);
        }

        /// <summary>
        /// Assignee whose local-assignment answer is <paramref name="assignAll"/> and whose unicast list is a fixed pair only when that flag is set.
        /// </summary>
        /// <param name="assignAll"><see langword="true"/> to treat every address as assigned and to return the fixed unicast pair.</param>
        private sealed class AssigningLocalIpAddressAssignee(bool assignAll) : ILocalIpAddressAssignee
        {
            /// <summary>
            /// Returns the constructor flag without inspecting <paramref name="address"/>.
            /// </summary>
            /// <param name="address">Address being tested. The value is not inspected.</param>
            /// <returns>The <c>assignAll</c> value supplied to the constructor.</returns>
            public bool IsLocallyAssigned(System.Net.IPAddress address) => assignAll;

            /// <summary>
            /// Returns <c>198.18.0.10</c> and <c>2001:db8::10</c> when every address is assigned, and an empty list otherwise.
            /// </summary>
            /// <returns>The fixed unicast pair, or an empty list.</returns>
            public IReadOnlyList<System.Net.IPAddress> GetAssignedUnicastAddresses() =>
                assignAll
                    ? [System.Net.IPAddress.Parse("198.18.0.10"), System.Net.IPAddress.Parse("2001:db8::10")]
                    : [];
        }

        /// <summary>
        /// DNS reconciler that completes every operation without contacting Cloudflare or changing records.
        /// </summary>
        private sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
        {
            /// <summary>
            /// Completes without listing or mutating records. Arguments and <paramref name="cancellationToken"/> are not observed.
            /// </summary>
            /// <param name="zoneId">Zone id. Ignored.</param>
            /// <param name="fqdn">FQDN. Ignored.</param>
            /// <param name="desired">Desired addresses. Ignored.</param>
            /// <param name="cancellationToken">Cancellation token. Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task ReconcileAsync(
                string zoneId,
                string fqdn,
                ResolvedBindAddresses desired,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;

            /// <summary>
            /// Reports that the last reconcile did not delete a pre-existing record. This double never reconciles.
            /// </summary>
            public bool DeletedPreExistingDuringLastReconcile => false;

            /// <summary>
            /// Completes without deleting records. Arguments are not observed.
            /// </summary>
            /// <param name="zoneId">Zone id. Ignored.</param>
            /// <param name="fqdn">FQDN. Ignored.</param>
            /// <param name="cancellationToken">Cancellation token. Not observed.</param>
            /// <param name="operationTimeout">Optional timeout. Ignored.</param>
            /// <returns>A completed task.</returns>
            public Task RemoveAllRecordsForFqdnAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken,
                TimeSpan? operationTimeout = null) =>
                Task.CompletedTask;

            /// <summary>
            /// Returns no record ids. Arguments are not observed.
            /// </summary>
            /// <param name="zoneId">Zone id. Ignored.</param>
            /// <param name="fqdn">FQDN. Ignored.</param>
            /// <param name="cancellationToken">Cancellation token. Not observed.</param>
            /// <returns>An empty id list.</returns>
            public Task<IReadOnlyList<string>> ListRecordIdsAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<string>>([]);

            /// <summary>
            /// Completes without deleting the supplied ids. Arguments are not observed.
            /// </summary>
            /// <param name="zoneId">Zone id. Ignored.</param>
            /// <param name="fqdn">FQDN. Ignored.</param>
            /// <param name="recordIds">Record ids. Ignored.</param>
            /// <param name="cancellationToken">Cancellation token. Not observed.</param>
            /// <param name="operationTimeout">Optional timeout. Ignored.</param>
            /// <returns>A completed task.</returns>
            public Task DeleteRecordIdsAsync(
                string zoneId,
                string fqdn,
                IReadOnlyCollection<string> recordIds,
                CancellationToken cancellationToken,
                TimeSpan? operationTimeout = null) =>
                Task.CompletedTask;
        }
    }
}
