using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd
{
    public sealed class SystemdWatchdogServiceTests
    {
        [Fact]
        public async Task Watchdog_DisabledWhenNotConfigured()
        {
            var notify = new FakeSystemdNotifyBridge();
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(watchdog: null),
                health: new FakeApplicationHealth { IsHealthyForWatchdog = true });

            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsActive);
            Assert.Equal(0, notify.WatchdogCount);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Watchdog_DisabledWhenOptionFalse()
        {
            var notify = new FakeSystemdNotifyBridge();
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromSeconds(1)),
                health: new FakeApplicationHealth { IsHealthyForWatchdog = true },
                configure: o => o.Systemd.EnableWatchdog = false);

            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsActive);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Watchdog_DisabledOnNonLinux()
        {
            var notify = new FakeSystemdNotifyBridge();
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.NonLinux(),
                health: new FakeApplicationHealth { IsHealthyForWatchdog = true });

            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsActive);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Watchdog_DisabledWhenNotifyDisabled()
        {
            var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromSeconds(1)),
                health: new FakeApplicationHealth { IsHealthyForWatchdog = true });

            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsActive);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Watchdog_SendsHeartbeatsWhileHealthy_AndStopsOnShutdown()
        {
            var notify = new FakeSystemdNotifyBridge();
            var health = new FakeApplicationHealth { IsHealthyForWatchdog = true };
            var lifetime = new TestHostApplicationLifetime();

            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(100)),
                health: health,
                hostLifetime: lifetime,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsActive);
            Assert.True(service.HeartbeatInterval > TimeSpan.Zero);
            Assert.True(service.HeartbeatInterval < TimeSpan.FromMilliseconds(100));

            await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(notify.WatchdogCount >= 1);

            await service.StopAsync(CancellationToken.None);
            var countAfterStop = notify.WatchdogCount;
            Assert.Equal(countAfterStop, notify.WatchdogCount);
            Assert.False(service.IsActive);
        }

        [Fact]
        public async Task Watchdog_DoesNotReportHealthyDuringUnhealthyPeriods()
        {
            var notify = new FakeSystemdNotifyBridge();
            var health = new FakeApplicationHealth { IsHealthyForWatchdog = false };
            var skipped = new WatchdogEventLogger("Skipping systemd watchdog keep-alive");
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
                health: health,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5,
                logger: skipped);
            try
            {
                await service.StartAsync(CancellationToken.None);
                Assert.True(service.IsActive);

                await skipped.Seen.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, notify.WatchdogCount);

                health.IsHealthyForWatchdog = true;
                await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(notify.WatchdogCount >= 1);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Watchdog_StopsHeartbeatsAfterUnexpectedTermination()
        {
            var health = new BackFillerApplicationHealth();
            health.MarkStarted();
            Assert.True(health.IsHealthyForWatchdog);
            var observed = new ObservingApplicationHealth(health);

            var notify = new FakeSystemdNotifyBridge();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var skipped = new WatchdogEventLogger("Skipping systemd watchdog keep-alive");
            notify.AfterWatchdogRecorded = () => release.Task.GetAwaiter().GetResult();
            using var watchdog = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
                health: observed,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5,
                logger: skipped);
            try
            {
                await watchdog.StartAsync(CancellationToken.None);
                await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

                health.MarkUnexpectedTermination();
                Assert.False(health.IsHealthyForWatchdog);
                var count = notify.WatchdogCount;
                skipped.Arm();
                release.TrySetResult();
                await skipped.Seen.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(count, notify.WatchdogCount);
            }
            finally
            {
                release.TrySetResult();
                await watchdog.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Watchdog_CancelsOnApplicationStopping()
        {
            var notify = new FakeSystemdNotifyBridge();
            var health = new FakeApplicationHealth { IsHealthyForWatchdog = true };
            var lifetime = new TestHostApplicationLifetime();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            notify.AfterWatchdogRecorded = () => release.Task.GetAwaiter().GetResult();

            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
                health: health,
                hostLifetime: lifetime,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);
            try
            {
                await service.StartAsync(CancellationToken.None);
                await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var count = notify.WatchdogCount;
                lifetime.SignalStopping();
                release.TrySetResult();
                await service.StopAsync(CancellationToken.None);
                Assert.Equal(count, notify.WatchdogCount);
                Assert.False(service.IsActive);
            }
            finally
            {
                release.TrySetResult();
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Theory]
        [InlineData(typeof(InvalidOperationException))]
        [InlineData(typeof(NotSupportedException))]
        public async Task Watchdog_NotificationFailure_DoesNotStopHost(Type exceptionType)
        {
            var notify = new FakeSystemdNotifyBridge
            {
                ThrowOnNotify = (Exception)Activator.CreateInstance(exceptionType, "notify-failed")!,
            };
            var health = new FakeApplicationHealth { IsHealthyForWatchdog = true };
            var lifetime = new TestHostApplicationLifetime();

            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(60)),
                health: health,
                hostLifetime: lifetime,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsActive);

            await notify.SecondWatchdogAttempt.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(lifetime.StopRequested);
            Assert.True(service.IsActive);
            Assert.True(notify.WatchdogAttemptCount >= 2);

            await service.StopAsync(CancellationToken.None);
            Assert.False(service.IsActive);
        }

        private static SystemdWatchdogService CreateWatchdog(
            FakeSystemdNotifyBridge notify,
            ISystemdRuntime runtime,
            IApplicationHealth health,
            IHostApplicationLifetime? hostLifetime = null,
            Action<BackFillerOptions>? configure = null,
            ILogger<SystemdWatchdogService>? logger = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            configure?.Invoke(options);

            return new SystemdWatchdogService(
                runtime,
                notify,
                health,
                hostLifetime ?? new TestHostApplicationLifetime(),
                Options.Create(options),
                logger ?? NullLogger<SystemdWatchdogService>.Instance);
        }

        private sealed class WatchdogEventLogger : ILogger<SystemdWatchdogService>
        {
            private readonly string _fragment;
            private TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public WatchdogEventLogger(string fragment) => _fragment = fragment;

            public Task Seen => Volatile.Read(ref _seen).Task;

            public void Arm() =>
                Volatile.Write(ref _seen, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                if (message.Contains(_fragment, StringComparison.Ordinal))
                {
                    Volatile.Read(ref _seen).TrySetResult();
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
