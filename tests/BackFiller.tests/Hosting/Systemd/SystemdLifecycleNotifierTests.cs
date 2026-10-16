using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd
{
    /// <summary>SystemdLifecycleNotifier must emit READY only after ApplicationStarted and STOPPING on stopping; notify failures are non-fatal.</summary>
    public sealed class SystemdLifecycleNotifierTests
    {
        /// <summary>READY follows ApplicationStarted once; STOPPING follows ApplicationStopping; health marks running then not.</summary>
        [Fact]
        public async Task Readiness_FollowsApplicationStarted_AndIsNotDuplicated()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Contains(notify.Notifications, n => n.Contains("Starting", StringComparison.Ordinal));

            lifetime.SignalStarted();
            Assert.Equal(1, notify.ReadyCount);
            Assert.True(health.IsHealthyForWatchdog);
            Assert.Contains(notify.Notifications, n => n.Contains("Running", StringComparison.Ordinal));

            lifetime.SignalStopping();
            Assert.Equal(1, notify.StoppingCount);
            Assert.False(health.IsHealthyForWatchdog);

            await notifier.StopAsync(CancellationToken.None);
            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(1, notify.StoppingCount);
        }
        /// <summary>Notifier start alone must not emit READY or mark watchdog health running.</summary>

        [Fact]
        public async Task Readiness_IsNotSentBeforeApplicationStarted()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);

            Assert.Equal(0, notify.ReadyCount);
            Assert.False(health.IsHealthyForWatchdog);
        }
        /// <summary>With notify disabled, started and stopping signals produce no notifications.</summary>

        [Fact]
        public async Task Readiness_IsNotSentWhenNotifyDisabled()
        {
            var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();
            await notifier.StopAsync(CancellationToken.None);

            Assert.Empty(notify.Notifications);
            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(0, notify.StoppingCount);
        }
        /// <summary>After stopping is signaled, a late ApplicationStarted must not emit READY.</summary>

        [Fact]
        public async Task Shutdown_PreventsSubsequentReadinessReporting()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStopping();
            Assert.Equal(1, notify.StoppingCount);

            lifetime.SignalStarted();
            Assert.Equal(0, notify.ReadyCount);
            Assert.False(health.IsHealthyForWatchdog);

            await notifier.StopAsync(CancellationToken.None);
            Assert.Equal(1, notify.StoppingCount);
        }
        /// <summary>Second StopAsync does not duplicate READY or STOPPING counts.</summary>

        [Fact]
        public async Task RepeatedStop_IsIdempotent()
        {
            var notify = new FakeSystemdNotifyBridge();
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();

            await notifier.StopAsync(CancellationToken.None);

            Assert.Equal(1, notify.ReadyCount);
            Assert.Equal(1, notify.StoppingCount);
        }
        /// <summary>Notify exceptions are swallowed so notifier start/stop completes without duplicate READY/STOPPING.</summary>

        [Fact]
        public async Task NotificationFailure_DoesNotCrashNotifier()
        {
            var notify = new FakeSystemdNotifyBridge
            {
                ThrowOnNotify = new InvalidOperationException("notify-failed"),
            };
            var lifetime = new TestHostApplicationLifetime();
            var health = new BackFillerApplicationHealth();
            using var notifier = CreateNotifier(lifetime, health, notify);

            await notifier.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            lifetime.SignalStopping();
            await notifier.StopAsync(CancellationToken.None);

            Assert.Equal(0, notify.ReadyCount);
            Assert.Equal(0, notify.StoppingCount);
        }
        /// <summary>BackFillerApplicationHealth reflects started, stopping, and unexpected termination for watchdog gating.</summary>

        [Fact]
        public void ApplicationHealth_TracksStartedStoppingAndUnexpectedTermination()
        {
            var health = new BackFillerApplicationHealth();
            Assert.False(health.IsHealthyForWatchdog);

            health.MarkStarted();
            Assert.True(health.IsHealthyForWatchdog);

            health.MarkUnexpectedTermination();
            Assert.False(health.IsHealthyForWatchdog);

            var health2 = new BackFillerApplicationHealth();
            health2.MarkStarted();
            health2.MarkStopping();
            Assert.False(health2.IsHealthyForWatchdog);
        }

        /// <summary>Constructs a lifecycle notifier with Linux-under-systemd runtime fakes.</summary>
        private static SystemdLifecycleNotifier CreateNotifier(
            IHostApplicationLifetime lifetime,
            BackFillerApplicationHealth health,
            FakeSystemdNotifyBridge notify,
            Action<BackFillerOptions>? configure = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            configure?.Invoke(options);

            return new SystemdLifecycleNotifier(
                lifetime,
                health,
                notify,
                RecordingSystemdRuntimeFactory.LinuxUnderSystemd(),
                Options.Create(options),
                NullLogger<SystemdLifecycleNotifier>.Instance);
        }
    }
}
