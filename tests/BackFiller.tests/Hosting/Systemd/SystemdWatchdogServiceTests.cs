using Microsoft.Extensions.Hosting;
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
            using var service = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
                health: health,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsActive);

            await health.UnhealthyRead.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, notify.WatchdogCount);

            health.IsHealthyForWatchdog = true;
            await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(notify.WatchdogCount >= 1);

            await service.StopAsync(CancellationToken.None);
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
            notify.AfterWatchdogRecorded = () => release.Task.GetAwaiter().GetResult();
            using var watchdog = CreateWatchdog(
                notify,
                runtime: RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromMilliseconds(80)),
                health: observed,
                configure: o => o.Systemd.WatchdogIntervalFraction = 0.5);

            await watchdog.StartAsync(CancellationToken.None);
            await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

            health.MarkUnexpectedTermination();
            Assert.False(health.IsHealthyForWatchdog);
            var count = notify.WatchdogCount;
            observed.ExpectNextRead();
            release.TrySetResult();
            await observed.ReadReturned.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(count, notify.WatchdogCount);

            await watchdog.StopAsync(CancellationToken.None);
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

            await service.StartAsync(CancellationToken.None);
            await notify.WatchdogSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var count = notify.WatchdogCount;
            lifetime.SignalStopping();
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
            Assert.Equal(count, notify.WatchdogCount);
            Assert.False(service.IsActive);
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

            await notify.WatchdogAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(lifetime.StopRequested);
            Assert.True(service.IsActive);

            await service.StopAsync(CancellationToken.None);
            Assert.False(service.IsActive);
        }

        private static SystemdWatchdogService CreateWatchdog(
            FakeSystemdNotifyBridge notify,
            ISystemdRuntime runtime,
            IApplicationHealth health,
            IHostApplicationLifetime? hostLifetime = null,
            Action<BackFillerOptions>? configure = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            configure?.Invoke(options);

            return new SystemdWatchdogService(
                runtime,
                notify,
                health,
                hostLifetime ?? new TestHostApplicationLifetime(),
                Options.Create(options),
                NullLogger<SystemdWatchdogService>.Instance);
        }
    }
}
