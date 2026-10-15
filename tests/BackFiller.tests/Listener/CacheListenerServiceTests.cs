using System.Collections.Concurrent;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using VectorNNTP.BackFiller.Articles;
using VectorNNTP.BackFiller.Articles.Parsing;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Networking.Certificates;

namespace VectorNNTP.BackFiller.Tests.Listener
{
    public sealed class CacheListenerServiceTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task Starts_binds_ipv4_and_repeated_dispose_is_safe()
        {
            await using var context = await ListenerContext.StartAsync();

            Assert.Equal(CacheListenerState.Running, context.Service.State);
            Assert.Contains(context.Service.LocalEndPoints, static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.Loopback));

            await context.Service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, context.Service.State);
        }

        [Fact]
        public async Task Startup_fails_when_the_certificate_is_missing()
        {
            var runtime = CreateRuntime(GetFreePort());
            var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreateUnavailableProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal(CacheListenerState.Stopped, service.State);
        }

        [Fact]
        public async Task Unexpected_connection_task_fault_during_dispose_still_stops_the_listener()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var service = new CacheListenerService(
                CreateRuntime(GetFreePort()),
                TestListenerCertificates.CreateUnavailableProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                logger);
            var connectionsField = typeof(CacheListenerService).GetField(
                "_connections",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var connections = Assert.IsType<ConcurrentDictionary<Task, byte>>(connectionsField?.GetValue(service));
            connections[Task.FromException(new NotSupportedException("connection boom"))] = 0;

            await service.DisposeAsync();

            Assert.Equal(CacheListenerState.Stopped, service.State);
            Assert.Contains(
                logger.Entries,
                entry => entry.EventId.Id == 5414
                    && entry.Message.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
        }

        [Fact]
        public async Task Unexpected_connection_failure_is_logged_and_the_listener_stays_up()
        {
            var logger = new CollectingLogger<CacheListenerService>();
            var runtime = CreateRuntime(GetFreePort());
            var certificates = TestListenerCertificates.CreatePublishedProvider();
            var service = new CacheListenerService(runtime, certificates, new ThrowingRetention(), logger);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await using var client = await ConnectAsync(runtime.BindPortTls);
                await ReadGreetingAsync(client.Stream);
                await client.Stream.WriteAsync("ARTICLE <a@b>\r\n"u8.ToArray());
                await ArticleWorkTestDeliveries.WaitUntilAsync(
                    () => service.State == CacheListenerState.Running && service.ActiveConnections == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(CacheListenerState.Running, service.State);
                Assert.Contains(
                    logger.Entries,
                    entry => entry.EventId.Id == 5411
                        && entry.Message.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
            }
            finally
            {
                await service.DisposeAsync();
                await certificates.DisposeAsync();
            }
        }

        [Fact]
        public async Task Tls_client_receives_the_nntp_greeting()
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
        }

        [Fact]
        public async Task Tls_article_writes_exact_bytes_and_the_next_request_is_430()
        {
            await using var context = await ListenerContext.StartAsync();
            var prepared = RetentionTestArticles.RetainPrepared(context.Authority, "<a@b>", ".\r\n");
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
            await client.Stream.WriteAsync("ARTICLE <a@b>\r\nARTICLE <a@b>\r\n"u8.ToArray());

            var status = "220 0 <a@b> article follows\r\n"u8.ToArray();
            var article = new byte[status.Length + prepared.ArtData.Length + ".\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(article);
            Assert.True(article.AsSpan(0, status.Length).SequenceEqual(status));
            Assert.True(article.AsSpan(status.Length, prepared.ArtData.Length).SequenceEqual(prepared.ArtData));
            Assert.True(article.AsSpan(status.Length + prepared.ArtData.Length).SequenceEqual(".\r\n"u8));
            Assert.Contains("\r\n..\r\n"u8, prepared.ArtData);

            var missing = new byte["430 No Such Article Found\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(missing);
            Assert.True(missing.AsSpan().SequenceEqual("430 No Such Article Found\r\n"u8));
            Assert.Equal(0, context.Authority.RetainedCount);
        }

        [Fact]
        public async Task Renewal_publishes_to_the_running_listener_without_restart()
        {
            await using var context = await ListenerContext.StartAsync();
            Assert.Equal(CacheListenerState.Running, context.Service.State);

            await using (var initial = await ConnectAsync(context))
            {
                Assert.Contains(
                    "backfiller.test",
                    initial.Stream.RemoteCertificate!.Subject,
                    StringComparison.OrdinalIgnoreCase);
            }

            using var renewed = TestListenerCertificates.CreateSelfSigned("backfiller-renewed.test");
            context.Certificates.PublishFromPfx(renewed.Export(X509ContentType.Pfx, "test"), "test");

            await using var client = await ConnectAsync(context);
            var remote = client.Stream.RemoteCertificate;
            Assert.NotNull(remote);
            Assert.Contains("backfiller-renewed.test", remote.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CN=backfiller.test", remote.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CacheListenerState.Running, context.Service.State);
        }

        [Fact]
        public async Task Handshake_failure_does_not_fault_the_listener()
        {
            await using var context = await ListenerContext.StartAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            tcp.GetStream().WriteByte(0x16);
            tcp.Close();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task Max_connections_rejects_additional_clients()
        {
            var runtime = CreateRuntime(GetFreePort(), maxConnections: 1);
            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            await using var held = await ConnectAsync(context);
            using var overflow = new TcpClient();
            await overflow.ConnectAsync(IPAddress.Loopback, context.Port);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => !overflow.Connected || overflow.Available >= 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(1, context.Service.ActiveConnections);
        }

        [Fact]
        public async Task Writer_failure_releases_connection_admission()
        {
            var runtime = CreateRuntime(GetFreePort(), maxConnections: 1);
            runtime = runtime with
            {
                Listener = runtime.Listener with { IoProgressTimeout = TimeSpan.FromMilliseconds(200) },
            };
            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            Assert.Equal(0, context.Service.ActiveConnections);

            await using var client = await ConnectAsync(context);
            using var floodCts = new CancellationTokenSource();
            var flood = Task.Run(async () =>
            {
                var command = "NOPE\r\n"u8.ToArray();
                while (!floodCts.IsCancellationRequested)
                {
                    try
                    {
                        await client.Stream.WriteAsync(command, floodCts.Token);
                    }
                    catch (OperationCanceledException) when (floodCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                }
            });

            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(15));
            await floodCts.CancelAsync();
            try
            {
                await flood;
            }
            catch (IOException ex)
            {
                output.WriteLine(
                    "Ignoring expected IOException while awaiting the flood task during cancellation or shutdown: {0}",
                    ex.Message);
            }

            Assert.Equal(0, context.Service.ActiveConnections);
            await using var second = await ConnectAsync(context);
            await ReadGreetingAsync(second.Stream);
            Assert.Equal(1, context.Service.ActiveConnections);
        }

        [Fact]
        public async Task Shutdown_during_handshake_is_safe()
        {
            await using var context = await ListenerContext.StartAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            await context.Service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, context.Service.State);
        }

        [Fact]
        public async Task Handshake_timeout_releases_the_connection_slot()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["127.0.0.1"];
            options.Listener.TlsHandshakeTimeoutSeconds = 1;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            runtime = runtime with
            {
                Listener = runtime.Listener with { TlsHandshakeTimeout = TimeSpan.FromMilliseconds(80) },
            };

            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(CacheListenerState.Running, context.Service.State);
        }

        [Fact]
        public async Task Io_timeout_closes_an_idle_authenticated_connection()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            runtime = runtime with
            {
                Listener = runtime.Listener with { IoProgressTimeout = TimeSpan.FromMilliseconds(80) },
            };

            await using var context = await ListenerContext.StartAsync(runtime: runtime);
            await using var client = await ConnectAsync(context);
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task Wildcard_bind_listens_on_ipv4_any()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = GetFreePort();
            options.BindAddress = ["*"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());

            await using var service = new CacheListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);
            await service.StartAsync(CancellationToken.None);
            Assert.Contains(
                service.LocalEndPoints,
                static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Any));
            await service.DisposeAsync();
            Assert.Equal(CacheListenerState.Stopped, service.State);
        }

        [Fact]
        public async Task Stream_transport_enforces_io_progress_timeout()
        {
            await using var hanging = new HangingStream();
            var transport = new StreamCacheListenerTransport(hanging, TimeSpan.FromMilliseconds(40), leaveInnerStreamOpen: true);
            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                var buffer = new byte[8];
                _ = await transport.ReadAsync(buffer, CancellationToken.None);
            });
        }

        [Fact]
        public async Task Tls12_client_can_complete_handshake()
        {
            await AssertHandshakeProtocolAsync(SslProtocols.Tls12);
        }

        [Fact]
        public async Task Tls13_client_can_complete_handshake_when_the_platform_supports_it()
        {
            try
            {
                await AssertHandshakeProtocolAsync(SslProtocols.Tls13);
            }
            catch (Exception ex) when (ex is AuthenticationException or PlatformNotSupportedException)
            {
                output.WriteLine($"SKIP: TLS 1.3 is not available for SslStream on this platform/runtime ({ex.GetType().Name}).");
                return;
            }
        }

        [Fact]
        public async Task Legacy_get_is_an_unknown_nntp_command()
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ReadGreetingAsync(client.Stream);
            // A legacy cache request is not an NNTP article command.
            await client.Stream.WriteAsync("GET /legacy\r\n"u8.ToArray());
            var unknown = new byte["500 Unknown command\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(unknown);
            Assert.True(unknown.AsSpan().SequenceEqual("500 Unknown command\r\n"u8));
            await client.Stream.WriteAsync("QUIT\r\n"u8.ToArray());
            var closing = new byte["205 Connection closing\r\n"u8.Length];
            await client.Stream.ReadExactlyAsync(closing);
            Assert.True(closing.AsSpan().SequenceEqual("205 Connection closing\r\n"u8));
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task Explicit_ipv6_loopback_binds_when_the_platform_supports_it()
        {
            if (!Socket.OSSupportsIPv6)
            {
                output.WriteLine("SKIP: Socket.OSSupportsIPv6 is false on this platform.");
                return;
            }

            var port = GetFreePort();
            var runtime = CreateRuntime(port) with
            {
                BindAddressTokens = ["::1"],
            };

            CacheListenerService? service = null;
            try
            {
                service = new CacheListenerService(
                    runtime,
                    TestListenerCertificates.CreatePublishedProvider(),
                    ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                Assert.Contains(
                    service.LocalEndPoints,
                    static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Loopback));
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                    or SocketError.ProtocolNotSupported
                    or SocketError.AddressNotAvailable)
            {
                output.WriteLine($"SKIP: IPv6 loopback bind is not available ({ex.SocketErrorCode}).");
                return;
            }
            finally
            {
                if (service is not null)
                {
                    await service.DisposeAsync();
                }
            }
        }

        [Fact]
        public void Production_listener_code_does_not_block_synchronously()
        {
            var directory = FindListenerSourceDirectory();
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        private static async Task AssertHandshakeProtocolAsync(SslProtocols protocol)
        {
            await using var context = await ListenerContext.StartAsync();
            await using var client = await ConnectAsync(context, protocol);
            Assert.Equal(protocol, client.Stream.SslProtocol);
            await ReadGreetingAsync(client.Stream);
        }

        private static async Task ReadGreetingAsync(SslStream stream)
        {
            var expected = "201 NNTP Service Ready, posting prohibited\r\n"u8.ToArray();
            var greeting = new byte[expected.Length];
            await stream.ReadExactlyAsync(greeting);
            Assert.Equal(expected, greeting);
        }

        private static Task<TlsClient> ConnectAsync(
            ListenerContext context,
            SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13) =>
            ConnectAsync(context.Port, protocols);

        private static async Task<TlsClient> ConnectAsync(
            int port,
            SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            return new TlsClient(tcp, ssl);
        }

        private static BackFillerRuntimeOptions CreateRuntime(int port, int maxConnections = 8)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = port;
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            return runtime with
            {
                Listener = runtime.Listener with { MaxActiveConnections = maxConnections },
            };
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string FindListenerSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "BackFiller", "Listener");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/BackFiller/Listener.");
        }

        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        }

        private sealed class TlsClient(TcpClient tcp, SslStream stream) : IAsyncDisposable
        {
            public SslStream Stream { get; } = stream;

            public async ValueTask DisposeAsync()
            {
                await Stream.DisposeAsync();
                tcp.Dispose();
            }
        }

        private sealed class ThrowingRetention : IArticleRetentionAuthority
        {
            public TimeSpan SweepInterval => TimeSpan.Zero;

            public long RetainedPayloadBytes => 0;

            public int RetainedCount => 0;

            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                ArticleRecord record,
                NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                throw new NotSupportedException("retention boom");

            public ArticleDeliveryClaimResult TryClaimForDelivery(ReadOnlySpan<byte> messageIdOctets) =>
                throw new NotSupportedException("retention boom");

            public bool TryCancelPendingRequest(Guid requestId) =>
                throw new NotSupportedException("retention boom");

            public long SweepExpired() => throw new NotSupportedException("retention boom");

            public void BeginShutdown()
            {
            }
        }

        private sealed class ListenerContext : IAsyncDisposable
        {
            private ListenerContext(
                CacheListenerService service,
                ArticleRetentionAuthority authority,
                int port,
                TlsCertificateContextProvider certificates)
            {
                Service = service;
                Authority = authority;
                Port = port;
                Certificates = certificates;
            }

            public CacheListenerService Service { get; }

            public ArticleRetentionAuthority Authority { get; }

            public TlsCertificateContextProvider Certificates { get; }

            public int Port { get; }

            public static async Task<ListenerContext> StartAsync(BackFillerRuntimeOptions? runtime = null)
            {
                runtime ??= CreateRuntime(GetFreePort());
                var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
                var certificates = TestListenerCertificates.CreatePublishedProvider();
                var service = new CacheListenerService(
                    runtime,
                    certificates,
                    authority,
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                return new ListenerContext(service, authority, runtime.BindPortTls, certificates);
            }

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await Authority.DisposeAsync();
                await Certificates.DisposeAsync();
            }
        }
    }
}
