using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Cloudflare;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Networking;
using VectorNNTPD.BackFillerService.Tests.Fixtures;

namespace VectorNNTPD.BackFillerService.Tests.Cloudflare
{
    /// <summary>
    /// Cloudflare DNS reconciler start/stop and cancellation with recording double.
    /// </summary>

    public sealed class CloudflareDnsReconcilerLifecycleTests
    {
        /// <summary>
        /// Contract: Dispose then operations throw object disposed exception.
        /// </summary>

        [Fact]
        public async Task Dispose_then_operations_throw_object_disposed_exception()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            var reconciler = new CloudflareDnsReconciler(
                new NoOpDnsClient(),
                Options.Create<AcmeCloudflareOptions>(options),
                NullLogger<CloudflareDnsReconciler>.Instance);

            reconciler.Dispose();

            var desired = new ResolvedBindAddresses([IPAddress.Parse("198.51.100.10")]);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                reconciler.ReconcileAsync("zone", "news.example.test", desired, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                reconciler.RemoveAllRecordsForFqdnAsync("zone", "news.example.test", CancellationToken.None));
        }

        /// <summary>
        /// Contract: Dispose is idempotent.
        /// </summary>

        [Fact]
        public void Dispose_is_idempotent()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            using var reconciler = new CloudflareDnsReconciler(
                new NoOpDnsClient(),
                Options.Create<AcmeCloudflareOptions>(options),
                NullLogger<CloudflareDnsReconciler>.Instance);

            reconciler.Dispose();
            reconciler.Dispose();
        }

        /// <summary>Cloudflare DNS client that completes all API calls without network I/O for lifecycle-only tests.</summary>
        private sealed class NoOpDnsClient : ICloudflareDnsClient
        {

            /// <summary>Returns an empty list. Lifecycle tests never inspect listed records.</summary>
            /// <param name="zoneId">Ignored.</param>
            /// <param name="fqdn">Ignored.</param>
            /// <param name="type">Ignored.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);


            /// <summary>Returns an empty list. Lifecycle tests never inspect listed records.</summary>
            /// <param name="zoneId">Ignored.</param>
            /// <param name="fqdn">Ignored.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);


            /// <summary>Throws. Dispose tests must not create records.</summary>
            /// <param name="zoneId">Ignored.</param>
            /// <param name="request">Ignored.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");


            /// <summary>Throws. Dispose tests must not update records.</summary>
            /// <param name="zoneId">Ignored.</param>
            /// <param name="recordId">Ignored.</param>
            /// <param name="request">Ignored.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");


            /// <summary>Throws. Dispose tests must not delete records.</summary>
            /// <param name="zoneId">Ignored.</param>
            /// <param name="recordId">Ignored.</param>
            /// <param name="cancellationToken">Ignored.</param>
            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");
        }
    }
}
