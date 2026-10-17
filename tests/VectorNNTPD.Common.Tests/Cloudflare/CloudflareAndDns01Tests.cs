using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Tests.TestDoubles;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Cloudflare
{
    /// <summary>
    /// Locks Cloudflare DNS reconciliation for managed A and AAAA records and DNS-01 TXT placement, failure, and cancellation cleanup.
    /// </summary>
    public sealed class CloudflareAndDns01Tests
    {
        /// <summary>Zone id passed to the reconciler and DNS-01 solver.</summary>
        private const string ZoneId = "zone-test";
        /// <summary>FQDN whose records these facts create, replace, and delete.</summary>
        private const string Fqdn = "backfiller01.usenet.ninja";

        /// <summary>
        /// Asserts that reconciliation creates A and AAAA records for the desired addresses using the managed TTL and DNS-only proxied flag.
        /// </summary>
        /// <returns>A task that completes when both record assertions finish.</returns>
        [Fact]
        public async Task Reconcile_CreatesAAndAaaa_WithManagedTtlAndDnsOnly()
        {
            var client = new InMemoryCloudflareDnsClient();
            var reconciler = CreateReconciler(client);

            await reconciler.ReconcileAsync(
                ZoneId,
                Fqdn,
                Desired("198.18.0.10", "2001:db8::10"),
                CancellationToken.None);

            var snapshot = client.Snapshot();
            Assert.Contains(
                snapshot,
                static r => r.Type == "A"
                            && r.Content == "198.18.0.10"
                            && r.Ttl == CloudflareManagedDnsPolicy.ManagedTtl
                            && r.Proxied == CloudflareManagedDnsPolicy.ManagedProxied);
            Assert.Contains(
                snapshot,
                static r => r.Type == "AAAA"
                            && r.Content == "2001:db8::10"
                            && r.Ttl == CloudflareManagedDnsPolicy.ManagedTtl
                            && r.Proxied == CloudflareManagedDnsPolicy.ManagedProxied);
        }

        /// <summary>
        /// Asserts that reconciliation deletes a stale address on the managed name, keeps a record for a different name, and creates the desired A record.
        /// </summary>
        /// <returns>A task that completes when the snapshot assertions finish.</returns>
        [Fact]
        public async Task Reconcile_RemovesStaleAndLeavesUnrelatedNames()
        {
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(
                Record("stale-a", "A", "198.18.0.99"),
                Record("other", "A", "198.18.0.50", name: "other.usenet.ninja"));
            var reconciler = CreateReconciler(client);

            await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

            var snapshot = client.Snapshot();
            Assert.DoesNotContain(snapshot, static r => r.Content == "198.18.0.99");
            Assert.Contains(snapshot, static r => r.Name == "other.usenet.ninja");
            Assert.Contains(snapshot, static r => r.Type == "A" && r.Content == "198.18.0.10");
        }

        /// <summary>
        /// Asserts that reconciliation of an already matching A record performs no create, update, or delete.
        /// </summary>
        /// <returns>A task that completes when the call-count assertions finish.</returns>
        [Fact]
        public async Task Reconcile_UnchangedSet_MakesNoMutations()
        {
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(Record("a1", "A", "198.18.0.10"));
            var reconciler = CreateReconciler(client);

            await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

            Assert.Equal(0, client.CreateCallCount);
            Assert.Equal(0, client.UpdateCallCount);
            Assert.Equal(0, client.DeleteCallCount);
        }

        /// <summary>
        /// Asserts that removing every record for the managed FQDN leaves a TXT record on a different name in place.
        /// </summary>
        /// <returns>A task that completes when the cleanup assertions finish.</returns>
        [Fact]
        public async Task Cleanup_RemovesExactFqdnRecords()
        {
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(
                Record("a1", "A", "198.18.0.10"),
                Record("txt", "TXT", "keep-other", name: "other.usenet.ninja"));
            var reconciler = CreateReconciler(client);

            await reconciler.RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

            var snapshot = client.Snapshot();
            Assert.DoesNotContain(snapshot, static r => r.Name == Fqdn);
            Assert.Contains(snapshot, static r => r.Name == "other.usenet.ninja");
        }

        /// <summary>
        /// Asserts that placing a DNS-01 challenge creates a TXT record, propagation succeeds against a pre-seeded resolver, and cleanup removes that TXT record.
        /// </summary>
        /// <returns>A task that completes when placement, propagation, and cleanup finish.</returns>
        [Fact]
        public async Task Dns01_PlaceAndCleanup_RemovesTxt()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var resolver = new ImmediateTxtResolver();
            var solver = new Dns01Solver(
                client,
                ZoneId,
                resolver,
                Fqdn,
                dir.Path,
                propagationTimeout: TimeSpan.FromSeconds(2),
                propagationInterval: TimeSpan.FromMilliseconds(10));

            var spec = new Dns01ChallengeSpec(Fqdn, "validation-token");
            resolver.Set(spec.RecordName, spec.Validation);
            await solver.PlaceAsync([spec], CancellationToken.None);
            Assert.Contains(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
            await solver.WaitPropagatedAsync([spec], CancellationToken.None);
            await solver.CleanupAsync(CancellationToken.None);
            Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
        }

        /// <summary>
        /// Asserts that a DNS client create failure surfaces as <see cref="AcmeChallengeException"/> and leaves no TXT record behind.
        /// </summary>
        /// <returns>A task that completes when the exception and snapshot assertions finish.</returns>
        [Fact]
        public async Task Dns01_CreateFailure_CleansUp()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient
            {
                CreateException = new CloudflareDnsException("create failed"),
            };
            var solver = new Dns01Solver(client, ZoneId, new ImmediateTxtResolver(), Fqdn, dir.Path);

            await Assert.ThrowsAsync<AcmeChallengeException>(
                () => solver.PlaceAsync([new Dns01ChallengeSpec(Fqdn, "tok")], CancellationToken.None));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
        }

        /// <summary>
        /// Asserts that cancelling placement while the client mutation waits throws <see cref="OperationCanceledException"/> and leaves no TXT record behind.
        /// </summary>
        /// <returns>A task that completes when the cancellation and snapshot assertions finish.</returns>
        [Fact]
        public async Task Dns01_Cancellation_CleansUp()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient
            {
                OnMutate = static async ct => await Task.Delay(Timeout.Infinite, ct),
            };
            var solver = new Dns01Solver(client, ZoneId, new ImmediateTxtResolver(), Fqdn, dir.Path);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => solver.PlaceAsync([new Dns01ChallengeSpec(Fqdn, "tok")], cts.Token));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
        }

        /// <summary>
        /// Builds a reconciler with a five-second Cloudflare timeout and a <c>DelayAsync</c> hook that completes immediately.
        /// </summary>
        /// <param name="client">DNS client the reconciler mutates.</param>
        /// <returns>The reconciler used by the reconciliation facts.</returns>
        private static CloudflareDnsReconciler CreateReconciler(ICloudflareDnsClient client)
        {
            var options = Options.Create(new AcmeCloudflareOptions
            {
                CloudFlareOperationTimeout = TimeSpan.FromSeconds(5),
            });
            var reconciler = new CloudflareDnsReconciler(
                client,
                options,
                NullLogger<CloudflareDnsReconciler>.Instance);
            reconciler.DelayAsync = static (_, _) => Task.CompletedTask;
            return reconciler;
        }

        /// <summary>
        /// Parses <paramref name="addresses"/> into the desired bind set passed to reconciliation.
        /// </summary>
        /// <param name="addresses">IPv4 or IPv6 address text.</param>
        /// <returns>The parsed desired address set.</returns>
        private static ResolvedBindAddresses Desired(params string[] addresses) =>
            new(addresses.Select(IPAddress.Parse));

        /// <summary>
        /// Builds a managed DNS record. <paramref name="name"/> defaults to <see cref="Fqdn"/>, and TTL and proxied follow the managed DNS policy.
        /// </summary>
        /// <param name="id">Record id stored by the in-memory client.</param>
        /// <param name="type">DNS record type, such as <c>A</c> or <c>TXT</c>.</param>
        /// <param name="content">Record content.</param>
        /// <param name="name">Record name, or <see langword="null"/> to use <see cref="Fqdn"/>.</param>
        /// <returns>The seeded record.</returns>
        private static CloudflareDnsRecord Record(string id, string type, string content, string? name = null) =>
            new()
            {
                Id = id,
                Type = type,
                Name = name ?? Fqdn,
                Content = content,
                Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
            };
    }
}
