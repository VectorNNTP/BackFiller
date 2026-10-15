using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Hosting.Systemd;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd
{
    public sealed class SystemdRuntimeDetectionTests
    {
        [Fact]
        public void NonLinux_DisablesWatchdogEvenIfEnvironmentLooksConfigured()
        {
            using var scope = new EnvironmentVariableScope(
                (SystemdRuntime.WatchdogUsecVariable, "5000000"),
                (SystemdRuntime.WatchdogPidVariable, Environment.ProcessId.ToString()));

            var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);

            if (runtime.IsLinux)
            {
                // On a real Linux CI agent this assertion is skipped; detection is covered by fakes.
                return;
            }

            Assert.False(runtime.IsLinux);
            Assert.False(runtime.IsSystemdService);
            Assert.False(runtime.IsWatchdogConfigured);
            Assert.Null(runtime.WatchdogTimeout);
        }

        [Fact]
        public void MissingWatchdogConfiguration_IsSafe()
        {
            using var scope = new EnvironmentVariableScope(
                (SystemdRuntime.WatchdogUsecVariable, null),
                (SystemdRuntime.WatchdogPidVariable, null));

            var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);
            Assert.False(runtime.IsWatchdogConfigured);
            Assert.Null(runtime.WatchdogTimeout);

            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                null,
                null,
                Environment.ProcessId,
                NullLogger.Instance,
                out var reason);
            Assert.Null(parsed);
            Assert.Equal("WATCHDOG_USEC not set", reason);
        }

        [Theory]
        [InlineData("not-a-number")]
        [InlineData("0")]
        [InlineData("-1")]
        public void InvalidWatchdogUsec_IsIgnored_OnLinuxPath(string usec)
        {
            using var scope = new EnvironmentVariableScope(
                (SystemdRuntime.WatchdogUsecVariable, usec),
                (SystemdRuntime.WatchdogPidVariable, null));

            var runtime = new SystemdRuntime([], NullLogger<SystemdRuntime>.Instance);
            Assert.False(runtime.IsWatchdogConfigured);
            Assert.Null(runtime.WatchdogTimeout);

            var logs = new CollectingLogger<SystemdRuntime>();
            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                usec,
                null,
                Environment.ProcessId,
                logs,
                out var reason);
            Assert.Null(parsed);
            Assert.Equal("WATCHDOG_USEC missing or invalid", reason);
            Assert.Contains(
                logs.Messages,
                message => message.Contains("WATCHDOG_USEC is not a positive integer", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidWatchdogUsec_IsAccepted_ByTheLinuxParser()
        {
            var parsed = SystemdRuntime.TryParseWatchdogTimeout(
                "5000000",
                null,
                Environment.ProcessId,
                NullLogger.Instance,
                out var reason);
            Assert.Equal(TimeSpan.FromSeconds(5), parsed);
            Assert.Equal("WATCHDOG_USEC accepted", reason);
        }

        [Fact]
        public void FakeDetection_DistinguishesSystemdFromConsoleAndNonLinux()
        {
            var underSystemd = RecordingSystemdRuntimeFactory.LinuxUnderSystemd(TimeSpan.FromSeconds(5));
            var console = RecordingSystemdRuntimeFactory.LinuxConsole();
            var windows = RecordingSystemdRuntimeFactory.NonLinux();

            Assert.True(underSystemd.IsLinux && underSystemd.IsSystemdService && underSystemd.IsWatchdogConfigured);
            Assert.True(console.IsLinux && !console.IsSystemdService && !console.IsWatchdogConfigured);
            Assert.True(!windows.IsLinux && !windows.IsSystemdService && !windows.IsWatchdogConfigured);
        }
    }

    internal sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previous;

        public EnvironmentVariableScope(params (string Name, string? Value)[] values)
        {
            _previous = values
                .Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))
                .ToArray();

            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
