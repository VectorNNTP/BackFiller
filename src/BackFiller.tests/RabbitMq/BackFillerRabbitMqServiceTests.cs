using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

using VectorNNTP.BackFiller.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.RabbitMq
{
    public sealed class RabbitMqServiceTests
    {
        [Fact]
        public void ConnectionHandle_does_not_own_the_connection()
        {
            Assert.DoesNotContain(typeof(IDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
            Assert.DoesNotContain(typeof(IAsyncDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
        }

        [Fact]
        public async Task StartAsync_connects_once_and_is_ready()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
            var service = CreateService(factory);
            service.ConnectionReplaced += (_, args) => replaced.Add(args);

            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.NotNull(service.Execution);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.True(handle.IsCurrent);
            Assert.Equal(1, handle.Generation);
            Assert.Same(factory.LastConnection, handle.Connection);
            var generation = Assert.Single(replaced);
            Assert.Equal(1, generation.ConnectionGeneration);
            Assert.False(generation.IsReplacement);
            Assert.Equal("VectorNNTP.BackFiller:backfiller01.usenet.ninja", factory.LastConnection!.ClientProvidedName);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task StartAsync_fails_when_connect_throws()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var service = CreateService(factory);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal("broker down", ex.Message);
            Assert.Equal(0, service.ConnectionCount);
            Assert.Equal(0, service.ConnectionGeneration);
            Assert.False(service.IsReady);
            Assert.Null(service.Execution);
            Assert.False(service.TryGetCurrent(out _));
        }

        [Fact]
        public async Task StartAsync_fails_when_connection_is_not_usable()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory { ReturnUnusableConnection = true };
            var service = CreateService(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal(0, service.ConnectionCount);
            Assert.False(service.IsReady);
            Assert.Equal(1, factory.LastConnection!.DisposeCount);
        }

        [Fact]
        public async Task StartAsync_cancelled_does_not_leave_a_connection()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var service = CreateService(factory);
            using var cts = new CancellationTokenSource();

            var start = service.StartAsync(cts.Token);
            cts.Cancel();
            factory.BlockConnect.TrySetCanceled(cts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Equal(0, service.ConnectionCount);
            Assert.False(service.IsReady);
            Assert.Null(service.Execution);
        }

        [Fact]
        public async Task StartAsync_can_retry_after_failed_connect()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var service = CreateService(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            factory.ConnectException = null;

            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsReady);
            Assert.Equal(1, service.ConnectionGeneration);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task StartAsync_is_idempotent()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, factory.ConnectCount);
            await service.DisposeAsync();
        }

        [Fact]
        public async Task Shutdown_after_successful_startup_disposes_the_current_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var connection = factory.LastConnection!;

            await service.StopAsync(CancellationToken.None);

            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            Assert.Equal(1, connection.DisposeCount);
        }

        [Fact]
        public async Task ConnectionLost_replaces_connection_and_increments_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
            var service = CreateService(factory);
            service.ConnectionReplaced += (_, args) => replaced.Add(args);

            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var count = await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, count);
            Assert.Equal(2, service.ConnectionCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(2, replaced.Count);
            Assert.True(replaced[1].IsReplacement);
            Assert.Equal(2, replaced[1].ConnectionGeneration);
            Assert.True(service.TryGetCurrent(out var current));
            Assert.Same(factory.LastConnection, current.Connection);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task Repeated_connection_loss_recovers_indefinitely()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);

            for (var expected = 2; expected <= 4; expected++)
            {
                var previous = factory.LastConnection!;
                var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var published = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                factory.Connected = connected;
                void OnReplaced(object? sender, RabbitMqConnectionReplacedEventArgs args)
                {
                    if (args.ConnectionGeneration == expected)
                    {
                        published.TrySetResult(args.ConnectionGeneration);
                    }
                }

                service.ConnectionReplaced += OnReplaced;
                try
                {
                    previous.SimulateLost();
                    using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await connected.Task.WaitAsync(safety.Token);
                    await published.Task.WaitAsync(safety.Token);
                    Assert.Equal(expected, service.ConnectionGeneration);
                    Assert.True(service.IsReady);
                    Assert.Equal(1, previous.DisposeCount);
                }
                finally
                {
                    service.ConnectionReplaced -= OnReplaced;
                }
            }

            await service.DisposeAsync();
        }

        [Fact]
        public async Task Stale_generation_cannot_replace_newer_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);
            var current = factory.LastConnection!;

            var extra = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = extra;
            first.SimulateLost();

            var extraConnect = extra.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<TimeoutException>(() => extraConnect);

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.Same(current, handle.Connection);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(0, current.DisposeCount);

            await service.DisposeAsync();
            Assert.Equal(1, current.DisposeCount);
        }

        [Fact]
        public async Task Stale_generation_cannot_dispose_newer_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);
            var current = factory.LastConnection!;

            await first.DisposeAsync();

            Assert.Equal(2, first.DisposeCount);
            Assert.Equal(0, current.DisposeCount);
            Assert.True(service.IsReady);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.Same(current, handle.Connection);

            await service.DisposeAsync();
            Assert.Equal(1, current.DisposeCount);
        }

        [Fact]
        public async Task Current_generation_can_be_shut_down_cleanly()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var handle));
            var connection = factory.LastConnection!;

            await service.DisposeAsync();

            Assert.False(handle.IsCurrent);
            Assert.False(service.IsReady);
            Assert.Equal(1, connection.DisposeCount);
            Assert.False(service.Execution!.IsFaulted);
        }

        [Fact]
        public async Task Cancellation_during_recovery_does_not_install_a_replacement()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.ConnectStarted = connectStarted;
            factory.BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connectStarted.Task.WaitAsync(safety.Token);

            var stop = service.StopAsync(CancellationToken.None);
            factory.BlockConnect.TrySetCanceled();
            await stop;

            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            foreach (var connection in factory.Connections)
            {
                Assert.Equal(1, connection.DisposeCount);
            }
        }

        [Fact]
        public async Task Shutdown_before_recovery_install_does_not_fault_the_watcher()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.DisposeStarted = disposeEntered;
            first.BlockDispose = releaseDispose;

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await disposeEntered.Task.WaitAsync(safety.Token);
            Assert.False(service.IsStopping);
            Assert.Equal(0, first.DisposeCount);
            Assert.Equal(1, factory.AttemptCount);

            var dispose = Task.Run(() => service.DisposeAsync().AsTask());
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => service.IsStopping, TimeSpan.FromSeconds(2));
            releaseDispose.TrySetResult();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(service.Execution!.IsFaulted);
            Assert.True(service.Execution.IsCompletedSuccessfully);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, factory.AttemptCount);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.Equal(1, first.DisposeCount);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            AssertInstallCanceled(logger);
        }

        [Fact]
        public async Task Shutdown_after_recovery_connect_does_not_publish_or_fault()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new HoldSecondConnectFactory(entered, release);
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.Inner.LastConnection!;

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await entered.Task.WaitAsync(safety.Token);
            Assert.False(service.IsStopping);
            Assert.Equal(1, first.DisposeCount);

            var dispose = Task.Run(() => service.DisposeAsync().AsTask());
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => service.IsStopping, TimeSpan.FromSeconds(2));
            release.TrySetResult();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(service.Execution!.IsFaulted);
            Assert.True(service.Execution.IsCompletedSuccessfully);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.Equal(2, factory.Inner.ConnectCount);
            var second = factory.Inner.Connections[1];
            Assert.NotSame(first, second);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            AssertInstallCanceled(logger);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2809);
        }

        [Fact]
        public async Task Unexpected_recovery_failure_faults_the_watcher_and_disposal()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            factory.ConnectException = new InvalidOperationException("broker exploded");

            first.SimulateLost();

            var fault = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.Execution!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("broker exploded", fault.Message);
            Assert.True(service.Execution!.IsFaulted);

            var disposed = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisposeAsync().AsTask());
            Assert.Equal("broker exploded", disposed.Message);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(2, factory.AttemptCount);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, first.DisposeCount);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2816);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 9100);
        }

        [Fact]
        public async Task CreateChannel_uses_the_current_generation_and_does_not_dispose_the_connection()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var handle));

            var channel = await handle.Connection.CreateManualAckChannelAsync(handle.Generation, CancellationToken.None);
            Assert.Equal(1, channel.Generation);
            Assert.True(channel.IsOpen);
            Assert.Single(factory.LastConnection!.Channels);

            await channel.DisposeAsync();
            Assert.Equal(1, factory.LastConnection.Channels[0].DisposeCount);
            Assert.Equal(0, factory.LastConnection.DisposeCount);
            Assert.True(service.IsReady);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task CreateChannel_on_a_stale_handle_fails()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var stale));
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.Connection.CreateManualAckChannelAsync(stale.Generation, CancellationToken.None));
            Assert.Empty(factory.LastConnection!.Channels);

            await service.DisposeAsync();
        }

        [Fact]
        public async Task Logs_do_not_include_configured_password()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

            Assert.All(
                logger.Messages,
                static message => Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, message, StringComparison.Ordinal));
        }

        [Fact]
        public void Production_rabbitmq_code_does_not_block_synchronously()
        {
            var directory = FindRabbitMqSourceDirectory();
            Assert.True(Directory.Exists(directory), directory);
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void BackFiller_assembly_does_not_contain_BackFillerRabbitMqService()
        {
            var assembly = typeof(VectorNNTP.BackFiller.Hosting.BackFillerServiceCollectionExtensions).Assembly;
            Assert.Null(assembly.GetType("VectorNNTP.BackFiller.RabbitMq.BackFillerRabbitMqService"));
            Assert.DoesNotContain(
                assembly.GetTypes(),
                static type => type.Name == "BackFillerRabbitMqService");
        }

        [Fact]
        public void Integrated_standalone_assembly_keeps_common_cache_contracts_without_legacy_backfiller_storage()
        {
            var assembly = typeof(RabbitMqService).Assembly;
            Assert.NotNull(assembly.GetType("VectorNNTP.BackFiller.ArticleWork.ArticleWorkConsumerService"));
            Assert.NotNull(assembly.GetType("VectorNNTP.BackFiller.Messaging.Cache.CacheFleetTopology"));
            Assert.DoesNotContain(
                assembly.GetManifestResourceNames(),
                static name => name.Contains("backfiller.storage", StringComparison.OrdinalIgnoreCase));

            var topologyNames = assembly.GetTypes()
                .Where(static type => type.Namespace?.StartsWith(
                    "Coverlet.Core.Instrumentation",
                    StringComparison.Ordinal) != true)
                .SelectMany(static type => type.GetFields(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance))
                .Where(static field => field.FieldType == typeof(string) && field.IsLiteral)
                .Select(static field => field.GetRawConstantValue() as string)
                .Where(static value => !string.IsNullOrEmpty(value))
                .ToArray();
            Assert.Contains(topologyNames, static value => value == "cache.requests");
            Assert.DoesNotContain(topologyNames, static value => value == "backfiller.storage");
        }

        internal static RabbitMqService CreateService(
            IRabbitMqConnectionFactory factory,
            ILogger<RabbitMqService>? logger = null)
        {
            var options = BackFillerTestOptions.CreateValidRabbitMq();
            options.PoolReconnectBaseDelayMs = 1;
            options.PoolReconnectMaxDelayMs = 1;
            return new RabbitMqService(
                factory,
                Microsoft.Extensions.Options.Options.Create(options),
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.BackFiller:backfiller01.usenet.ninja"),
                logger ?? NullLogger<RabbitMqService>.Instance);
        }

        internal static BackFillerRuntimeOptions CreateFastRuntime()
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
            rabbit.PoolReconnectBaseDelayMs = 1;
            rabbit.PoolReconnectMaxDelayMs = 1;
            return BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(),
                rabbit);
        }

        private static string FindRabbitMqSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "BackFiller", "RabbitMq");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/BackFiller/RabbitMq.");
        }

        private static void AssertInstallCanceled(CollectingLogger<RabbitMqService> logger)
        {
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 9100
                    && entry.Message.Contains("RabbitMQ connection install", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2810);
        }

        /// <summary>
        /// Recovery connect waits until the test releases it and does not observe shutdown cancellation,
        /// so publication can lose the race after the broker connect returns.
        /// </summary>
        private sealed class HoldSecondConnectFactory : IRabbitMqConnectionFactory
        {
            private readonly FakeBackFillerRabbitMqConnectionFactory _inner = new();
            private readonly TaskCompletionSource _secondConnectEntered;
            private readonly TaskCompletionSource _releaseSecondConnect;
            private int _attempts;

            public HoldSecondConnectFactory(
                TaskCompletionSource secondConnectEntered,
                TaskCompletionSource releaseSecondConnect)
            {
                _secondConnectEntered = secondConnectEntered;
                _releaseSecondConnect = releaseSecondConnect;
            }

            public FakeBackFillerRabbitMqConnectionFactory Inner => _inner;

            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    return _inner.ConnectAsync(options, connectionName, cancellationToken);
                }

                return ConnectSecondAsync(options, connectionName);
            }

            private async Task<IRabbitMqConnection> ConnectSecondAsync(
                RabbitMqOptions options,
                string connectionName)
            {
                _secondConnectEntered.TrySetResult();
                await _releaseSecondConnect.Task.ConfigureAwait(false);
                return await _inner.ConnectAsync(options, connectionName, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
