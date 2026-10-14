using System.Security.Cryptography;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Tests.Acme
{
    public sealed class AcmeIssuerDnsCleanupTests
    {
        private const string DirectoryUrl = "http://acme.test/directory";

        [Fact]
        public async Task Cancelled_cleanup_does_not_replace_the_original_acme_exception()
        {
            using var cts = new CancellationTokenSource();
            var dns = new RecordingDnsClient();
            var logger = new CollectingLogger<AcmeIssuer>();
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ =>
                {
                    cts.Cancel();
                    throw new AcmeChallengeException("propagation_failed", "injected");
                }),
                logger);

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                scope.Issuer.IssueAsync(["example.com"], cts.Token));

            Assert.Equal("propagation_failed", ex.Category);
            Assert.Equal(0, dns.DeleteCalls);
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 1923);
            AssertResponsesDisposed(scope);
        }

        [Fact]
        public async Task Cleanup_failure_does_not_replace_the_original_acme_exception()
        {
            var dns = new RecordingDnsClient
            {
                DeleteException = new InvalidOperationException("delete fault"),
            };
            var logger = new CollectingLogger<AcmeIssuer>();
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ => throw new AcmeChallengeException("propagation_failed", "injected")),
                logger);

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                scope.Issuer.IssueAsync(["example.com"], CancellationToken.None));

            Assert.Equal("propagation_failed", ex.Category);
            Assert.Equal(1, dns.DeleteCalls);
            Assert.Contains(
                logger.Messages,
                static message => message.Contains("DNS-01 best-effort cleanup failed", StringComparison.Ordinal));
            Assert.DoesNotContain("txt_cleanup_failed", ex.Message, StringComparison.Ordinal);
            AssertResponsesDisposed(scope);
        }

        [Fact]
        public async Task Cancelled_issuance_still_throws_cancellation_after_cleanup()
        {
            using var cts = new CancellationTokenSource();
            var dns = new RecordingDnsClient
            {
                OnCreate = () => cts.Cancel(),
            };
            using var scope = CreateIssuer(
                dns,
                new ScriptedResolver(_ => throw new InvalidOperationException("lookup should not run")),
                NullLogger<AcmeIssuer>.Instance);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                scope.Issuer.IssueAsync(["example.com"], cts.Token));
            Assert.Equal(1, dns.DeleteCalls);
            AssertResponsesDisposed(scope);
        }

        private static IssuerScope CreateIssuer(
            RecordingDnsClient dns,
            IAuthoritativeTxtResolver resolver,
            ILogger<AcmeIssuer> logger)
        {
            var stateDir = Directory.CreateTempSubdirectory("acme-issuer-cleanup-").FullName;
            using var rsa = RSA.Create(2048);
            var store = new AccountStore(stateDir);
            store.Save(new AcmeAccountState(
                "http://acme.test/acct/1",
                DirectoryUrl,
                rsa.ExportPkcs8PrivateKey(),
                "{}"));
            var http = new ScriptedAcmeHttpFactory();

            return new IssuerScope(
                stateDir,
                http,
                new AcmeIssuer(
                Options.Create(new AcmeCloudflareOptions
                {
                    AcmeDirectoryUrl = DirectoryUrl,
                    AcmeCertificatePassword = "pw",
                    AcmeEmail = "ops@example.com",
                    AcmeRenewalThresholdDays = 30,
                }),
                store,
                new Dns01Solver(dns, "zone", resolver, "example.com", propagationTimeout: TimeSpan.FromSeconds(2)),
                http,
                logger));
        }

        private static void AssertResponsesDisposed(IssuerScope scope)
        {
            Assert.NotEmpty(scope.Http.Responses);
            Assert.All(scope.Http.Responses, static response => Assert.True(response.WasDisposed));
        }

        private sealed class IssuerScope(string stateDir, ScriptedAcmeHttpFactory http, AcmeIssuer issuer) : IDisposable
        {
            public AcmeIssuer Issuer { get; } = issuer;

            public ScriptedAcmeHttpFactory Http { get; } = http;

            public void Dispose() => Directory.Delete(stateDir, recursive: true);
        }

        private sealed class ScriptedResolver(Func<CancellationToken, IReadOnlyList<string>> lookup) : IAuthoritativeTxtResolver
        {
            public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken) =>
                Task.FromResult(lookup(cancellationToken));
        }

        private sealed class ScriptedAcmeHttpFactory : IHttpClientFactory
        {
            private readonly ScriptedAcmeHandler _handler = new();

            public IReadOnlyList<ScriptedAcmeHandler.TrackedResponse> Responses => _handler.Responses;

            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        private sealed class ScriptedAcmeHandler : HttpMessageHandler
        {
            private readonly List<TrackedResponse> _responses = [];

            public IReadOnlyList<TrackedResponse> Responses
            {
                get
                {
                    lock (_responses)
                    {
                        return [.. _responses];
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult<HttpResponseMessage>(CreateResponse(request));

            private TrackedResponse CreateResponse(HttpRequestMessage request)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var response = new TrackedResponse(HttpStatusCode.OK);
                lock (_responses)
                {
                    _responses.Add(response);
                }

                response.Headers.TryAddWithoutValidation("Replay-Nonce", "nonce");
                if (request.Method == HttpMethod.Head)
                {
                    response.Content = new ByteArrayContent([]);
                    return response;
                }

                var json = path.EndsWith("/directory", StringComparison.Ordinal)
                    ? """
                      {"newNonce":"http://acme.test/nonce","newAccount":"http://acme.test/new-account","newOrder":"http://acme.test/new-order"}
                      """
                    : path.EndsWith("/new-order", StringComparison.Ordinal)
                        ? """
                          {"status":"pending","authorizations":["http://acme.test/authz/1"],"finalize":"http://acme.test/order/1/finalize","identifiers":[{"type":"dns","value":"example.com"}]}
                          """
                        : path.EndsWith("/authz/1", StringComparison.Ordinal)
                            ? """
                              {"status":"pending","identifier":{"type":"dns","value":"example.com"},"challenges":[{"type":"dns-01","url":"http://acme.test/chall/1","token":"tok"}]}
                              """
                            : "{}";
                if (path.EndsWith("/new-order", StringComparison.Ordinal))
                {
                    response.StatusCode = HttpStatusCode.Created;
                    response.Headers.Location = new Uri("http://acme.test/order/1");
                }

                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return response;
            }

            internal sealed class TrackedResponse : HttpResponseMessage
            {
                public TrackedResponse(HttpStatusCode statusCode)
                    : base(statusCode)
                {
                }

                public bool WasDisposed { get; private set; }

                protected override void Dispose(bool disposing)
                {
                    if (disposing)
                    {
                        WasDisposed = true;
                    }

                    base.Dispose(disposing);
                }
            }
        }

        private sealed class RecordingDnsClient : ICloudflareDnsClient
        {
            public int DeleteCalls { get; private set; }

            public Exception? DeleteException { get; init; }

            public Action? OnCreate { get; init; }

            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("list is not used by this test");

            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("list is not used by this test");

            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken)
            {
                OnCreate?.Invoke();
                return Task.FromResult(new CloudflareDnsRecord
                {
                    Id = "rec-1",
                    Type = request.Type,
                    Name = request.Name,
                    Content = request.Content,
                });
            }

            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("update is not used by this test");

            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken)
            {
                DeleteCalls++;
                if (DeleteException is not null)
                {
                    throw DeleteException;
                }

                return Task.CompletedTask;
            }
        }
    }
}
