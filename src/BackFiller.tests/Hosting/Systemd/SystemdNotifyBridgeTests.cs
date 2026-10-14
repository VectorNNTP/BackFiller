using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd
{
    public sealed class SystemdNotifyBridgeTests
    {
        [Fact]
        public void SocketException_is_logged_and_does_not_escape()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new System.Net.Sockets.SocketException(10054))],
                logger);

            bridge.NotifyWatchdog();

            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 5105);
            Assert.Contains(logger.Messages, static message => message.Contains("WATCHDOG=1", StringComparison.Ordinal));
        }

        [Fact]
        public void Unexpected_notifier_exception_is_logged_and_does_not_escape()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new NotSupportedException("notify socket"))],
                logger);

            bridge.NotifyReady();

            Assert.Contains(
                logger.Entries,
                entry => entry.EventId.Id == 5105 && entry.Message.Contains("notify socket", StringComparison.Ordinal));
        }

        [Fact]
        public void Notification_log_failure_does_not_escape()
        {
            var bridge = new SystemdNotifyBridge(
                [new ThrowingNotifier(new NotSupportedException("notify socket"))],
                new ThrowingLogger());

            var diagnostic = TestSelfLog.Capture(bridge.NotifyStopping);

            Assert.Contains("notification failure log failed", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public void Disabled_notifier_does_not_send()
        {
            var logger = new CollectingLogger<SystemdNotifyBridge>();
            var notifier = new ThrowingNotifier(new InvalidOperationException("should not send"))
            {
                Enabled = false,
            };
            var bridge = new SystemdNotifyBridge([notifier], logger);

            bridge.NotifyStatus("ready");

            Assert.Empty(logger.Entries);
            Assert.Equal(0, notifier.Calls);
        }

        private sealed class ThrowingNotifier(Exception exception) : ISystemdNotifier
        {
            public bool Enabled { get; init; } = true;

            public int Calls { get; private set; }

            public bool IsEnabled => Enabled;

            public void Notify(ServiceState state)
            {
                Calls++;
                throw exception;
            }
        }

        private sealed class ThrowingLogger : ILogger<SystemdNotifyBridge>
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("log failed");

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
