using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Core;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Acme
{
    /// <summary>
    /// Acme certificate application service lifecycle with test doubles.
    /// </summary>

    public sealed class AcmeCertificateApplicationServiceTests
    {
        /// <summary>
        /// Contract: Adapter implements application service and forwards identity.
        /// </summary>

        [Fact]
        public void Adapter_implements_application_service_and_forwards_identity()
        {
            using var harness = CreateTlsDisabledHarness();
            Assert.IsAssignableFrom<IApplicationService>(harness.Adapter);
            Assert.Equal("AcmeCertificate", harness.Adapter.Name);
            Assert.Null(harness.Adapter.Execution);
        }

        /// <summary>
        /// Contract: StartAsync without readiness fails fast and does not journal.
        /// </summary>

        [Fact]
        public async Task StartAsync_without_readiness_fails_fast_and_does_not_journal()
        {
            using var harness = CreateTlsDisabledHarness();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Adapter.StartAsync(CancellationToken.None));
            Assert.Contains("usable certificate", ex.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Journal.Stages);
            Assert.False(harness.Readiness.IsReady);
            Assert.Null(harness.Adapter.Execution);
        }

        /// <summary>
        /// Contract: StartAsync with existing certificate journals ready and keeps renewal execution.
        /// </summary>

        [Fact]
        public async Task StartAsync_with_existing_certificate_journals_ready_and_keeps_renewal_execution()
        {
            using var harness = CreateReusableCertificateHarness();

            await harness.Adapter.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(harness.Readiness.IsReady);
                Assert.Equal([BackFillerStartupStages.AcmeCertificateReady], harness.Journal.Stages);
                Assert.Same(harness.Inner.Execution, harness.Adapter.Execution);
                Assert.NotNull(harness.Adapter.Execution);
                Assert.False(harness.Adapter.Execution.IsCompleted);
            }
            finally
            {
                await harness.Adapter.StopAsync(CancellationToken.None);
                await harness.Adapter.DisposeAsync();
            }

            Assert.True(harness.Adapter.Execution?.IsCompleted);
        }

        /// <summary>
        /// Contract: StopAsync and DisposeAsync delegate to the common service.
        /// </summary>

        [Fact]
        public async Task StopAsync_and_DisposeAsync_delegate_to_the_common_service()
        {
            using var harness = CreateReusableCertificateHarness();
            await harness.Adapter.StartAsync(CancellationToken.None);
            var execution = harness.Adapter.Execution;
            Assert.NotNull(execution);

            await harness.Adapter.StopAsync(CancellationToken.None);
            Assert.True(execution.IsCompleted);

            await harness.Adapter.DisposeAsync();
        }

        /// <summary>
        /// Contract: Certificate manager evaluate throws after dispose.
        /// </summary>

        [Fact]
        public void Certificate_manager_evaluate_throws_after_dispose()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var manager = new CertificateManager(
                options.Fqdn,
                AppContext.BaseDirectory,
                options.AcmeDirectoryUrl,
                new CertificateStore(
                    AppContext.BaseDirectory,
                    options.Fqdn,
                    options.AcmeCertificatePassword,
                    domains,
                    TimeSpan.FromDays(options.AcmeRenewalThresholdDays)),
                new ThrowingIssuer(),
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);

            manager.Dispose();

            Assert.Throws<ObjectDisposedException>(() => manager.EvaluateExisting());
        }

        /// <summary>
        /// Builds an adapter harness with TLS bind port zero, a no-op publisher, and a factory that must not run issuance.
        /// </summary>

        private static AdapterHarness CreateTlsDisabledHarness()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            options.BindPortTls = 0;
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                new UnusedAcmeFactory(options),
                new NoOpPublisher(),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            return new AdapterHarness(
                new AcmeCertificateApplicationService(inner, readiness, journal),
                inner,
                readiness,
                journal);
        }

        /// <summary>
        /// Builds an adapter harness with a long-lived self-signed certificate on disk, an injected manager, and a publisher that marks readiness.
        /// </summary>

        private static AdapterHarness CreateReusableCertificateHarness()
        {
            var dir = new TempAcmeDir();
            var options = BackFillerTestOptions.CreateValidAcme();
            options.AcmeStateDir = dir.Path;
            options.Fqdn = "backfiller01.usenet.ninja";
            options.IncludeNewsHostnameInCertificate = false;
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays));
            store.Save(CreateMaterial(domains, options.AcmeCertificatePassword, DateTimeOffset.UtcNow.AddDays(90)));

            var manager = new CertificateManager(
                options.Fqdn,
                dir.Path,
                options.AcmeDirectoryUrl,
                store,
                new ThrowingIssuer(),
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);
            var factory = new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options));
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                factory,
                new MarkingPublisher(readiness),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            var adapter = new AcmeCertificateApplicationService(inner, readiness, journal);
            return new AdapterHarness(adapter, inner, readiness, journal, dir);
        }

        /// <summary>
        /// Creates exportable PFX material with ordered lowercase SAN names and the given validity window.
        /// </summary>
        /// <param name="dnsNames">DNS identifiers; the first name becomes the certificate subject CN.</param>
        /// <param name="password">PFX encryption password.</param>
        /// <param name="notAfter">Certificate not-after instant (UTC).</param>
        private static CertificateMaterial CreateMaterial(
            IReadOnlyList<string> dnsNames,
            string password,
            DateTimeOffset notAfter)
        {
            var before = DateTimeOffset.UtcNow.AddDays(-1);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + dnsNames[0],
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                sanBuilder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
            var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], password);
            return new CertificateMaterial(
                pfx,
                dnsNames.Select(static name => name.ToLowerInvariant()).OrderBy(static name => name).ToArray(),
                before,
                notAfter);
        }

        /// <summary>Builds AcmeCertificateApplicationService with injectable issuer/publisher fakes for adapter tests.</summary>
        private sealed class AdapterHarness : IDisposable
        {
            /// <summary>Optional temp ACME directory disposed with the harness.</summary>
            private readonly TempAcmeDir? _dir;

            /// <summary>Retains adapter test services and optional filesystem state.</summary>
            /// <param name="adapter">Application service under test.</param>
            /// <param name="inner">Inner certificate service.</param>
            /// <param name="readiness">Readiness gate observed by tests.</param>
            /// <param name="journal">Startup journal for stage assertions.</param>
            /// <param name="dir">Optional temp directory cleaned up on dispose.</param>
            public AdapterHarness(
                AcmeCertificateApplicationService adapter,
                AcmeCertificateService inner,
                IAcmeCertificateReadiness readiness,
                IBackFillerStartupJournal journal,
                TempAcmeDir? dir = null)
            {
                Adapter = adapter;
                Inner = inner;
                Readiness = readiness;
                Journal = journal;
                _dir = dir;
            }

            /// <summary>
            /// Application-service adapter under test.
            /// </summary>

            public AcmeCertificateApplicationService Adapter { get; }

            /// <summary>
            /// Inner certificate service wired into the adapter.
            /// </summary>

            public AcmeCertificateService Inner { get; }

            /// <summary>
            /// Shared readiness gate observed by lifecycle assertions.
            /// </summary>

            public IAcmeCertificateReadiness Readiness { get; }

            /// <summary>
            /// Startup journal recording stages emitted during start.
            /// </summary>

            public IBackFillerStartupJournal Journal { get; }

            /// <summary>
            /// Disposes the optional temp ACME directory when present.
            /// </summary>

            public void Dispose() => _dir?.Dispose();
        }

        /// <summary>Creates and deletes a temporary ACME state directory for filesystem-sensitive tests.</summary>
        private sealed class TempAcmeDir : IDisposable
        {
            /// <summary>
            /// Unique directory path under the process temp folder.
            /// </summary>

            public string Path { get; } = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "vectornntp-bf-acme-" + Guid.NewGuid().ToString("N"));

            /// <summary>Creates the directory at <see cref="Path"/>.</summary>
            public TempAcmeDir() => Directory.CreateDirectory(Path);

            /// <summary>
            /// Recursively deletes the temp directory; IO failures are ignored.
            /// </summary>

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort test cleanup.
                }
            }
        }

        /// <summary>AcmeComponentFactory placeholder that must not be invoked in the exercised code paths.</summary>
        private sealed class UnusedAcmeFactory : AcmeComponentFactory
        {
            /// <summary>Base factory that must not be invoked on exercised paths.</summary>
            /// <param name="options">Bound ACME options for the unused base.</param>
            public UnusedAcmeFactory(AcmeCloudflareOptions options)
                : base(
                    Options.Create(options),
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
            }
        }

        /// <summary>AcmeComponentFactory that returns test-controlled issuer and publisher instances.</summary>
        private sealed class InjectedManagerFactory : AcmeComponentFactory
        {
            /// <summary>Manager returned from <see cref="GetOrCreateManager"/>.</summary>
            private readonly CertificateManager _manager;

            /// <summary>Factory that always supplies the injected certificate manager.</summary>
            /// <param name="manager">Prebuilt manager for issuer tests.</param>
            /// <param name="options">ACME options passed to the base factory.</param>
            public InjectedManagerFactory(CertificateManager manager, IOptions<AcmeCloudflareOptions> options)
                : base(
                    options,
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
                _manager = manager;
            }

            /// <summary>Returns the injected manager without creating production components.</summary>
            internal override CertificateManager? GetOrCreateManager() => _manager;
        }

        /// <summary>Cloudflare client double that throws if any API method is called unexpectedly.</summary>
        private sealed class UnusedCloudflareClient : ICloudflareDnsClient
        {
            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="type">DNS record type (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="request">Record payload (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Existing record identifier (unused).</param>
            /// <param name="request">Updated record payload (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Record to delete (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");
        }

        /// <summary>HttpClientFactory that throws if CreateClient is invoked.</summary>
        private sealed class UnusedHttpClientFactory : IHttpClientFactory
        {
            /// <summary>
            /// Satisfies the base factory constructor; returns a disposable client that must not perform ACME HTTP in these tests.
            /// </summary>
            /// <param name="name">Named client key (ignored).</param>
            public HttpClient CreateClient(string name) => new();
        }

        /// <summary>ICertificateIssuer that always throws to simulate issuance failure.</summary>
        private sealed class ThrowingIssuer : ICertificateIssuer
        {
            /// <summary>
            /// Always fails to ensure issuance does not run when a reusable certificate is already present.
            /// </summary>
            /// <param name="domains">Requested DNS names (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CertificateMaterial> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Issuance should not run when a usable certificate exists.");
        }

        /// <summary>Publisher double that completes without writing certificate material.</summary>
        private sealed class NoOpPublisher : IAcmeCertificatePublisher
        {
            /// <summary>
            /// Ignores PFX bytes; does not write certificate files or change readiness.
            /// </summary>
            /// <param name="pfxBytes">Exported certificate bytes (ignored).</param>
            /// <param name="password">PFX password (ignored).</param>
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
            }
        }

        /// <summary>Publisher double that records whether Publish was invoked.</summary>
        private sealed class MarkingPublisher : IAcmeCertificatePublisher
        {
            /// <summary>Readiness marked when publish is invoked.</summary>
            private readonly IAcmeCertificateReadiness _readiness;

            /// <summary>Records readiness on publish without writing certificate files.</summary>
            /// <param name="readiness">Gate updated in <see cref="PublishFromPfx"/>.</param>
            public MarkingPublisher(IAcmeCertificateReadiness readiness)
            {
                _readiness = readiness;
            }

            /// <summary>
            /// Marks the injected readiness gate without persisting PFX material to disk.
            /// </summary>
            /// <param name="pfxBytes">Exported certificate bytes (ignored).</param>
            /// <param name="password">PFX password (ignored).</param>
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
                _readiness.MarkReady();
            }
        }
    }
}
