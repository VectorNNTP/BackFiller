using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Networking;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Cloudflare
{
    public sealed class CloudflareDnsReconcilerLifecycleTests
    {
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

        [Fact]
        public void Dispose_is_idempotent()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            var reconciler = new CloudflareDnsReconciler(
                new NoOpDnsClient(),
                Options.Create<AcmeCloudflareOptions>(options),
                NullLogger<CloudflareDnsReconciler>.Instance);

            reconciler.Dispose();
            reconciler.Dispose();
        }

        private sealed class NoOpDnsClient : ICloudflareDnsClient
        {
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);

            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);

            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");

            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");

            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Not used in lifecycle tests.");
        }
    }
}
