using VectorNNTP.BackFiller.Acme.Protocol;
using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.NntpDb;

namespace VectorNNTP.BackFiller.Tests.Hosting
{
    public sealed class BackFillerProcessBoundaryTests
    {
        [Theory]
        [MemberData(nameof(OperationalFailures))]
        public void Operational_and_unexpected_host_failures_return_the_failure_exit_code(Exception failure)
        {
            Exception? observed = null;

            var code = BackFillerProcessBoundary.ObserveHostFailure(failure, exception => observed = exception);

            Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            Assert.Same(failure, observed);
        }

        [Fact]
        public void Fatal_log_failure_still_returns_the_failure_exit_code()
        {
            var diagnostic = TestSelfLog.Capture(() =>
            {
                var code = BackFillerProcessBoundary.ObserveHostFailure(
                    new NntpDbAuthenticationException("access denied"),
                    static _ => throw new InvalidOperationException("log sink failed"));

                Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            });

            Assert.Contains("host failure log failed", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Logger_shutdown_failure_after_a_clean_run_returns_the_failure_exit_code()
        {
            var restored = 0;

            var code = await BackFillerProcessBoundary.FinishAsync(
                exitCode: 0,
                () => throw new NotSupportedException("close boom"),
                () => restored++);

            Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            Assert.Equal(1, restored);
        }

        [Fact]
        public async Task Logger_shutdown_failure_keeps_an_existing_host_failure_code()
        {
            var restored = 0;

            var diagnostic = await TestSelfLog.CaptureAsync(async () =>
            {
                var code = await BackFillerProcessBoundary.FinishAsync(
                    BackFillerProcessBoundary.FailureExitCode,
                    () => throw new NotSupportedException("close boom"),
                    () => restored++);
                Assert.Equal(BackFillerProcessBoundary.FailureExitCode, code);
            });

            Assert.Equal(1, restored);
            Assert.Contains("logger shutdown failed after a host failure", diagnostic, StringComparison.Ordinal);
            Assert.Contains("close boom", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Console_restore_failure_does_not_replace_the_exit_code()
        {
            var diagnostic = await TestSelfLog.CaptureAsync(async () =>
            {
                var code = await BackFillerProcessBoundary.FinishAsync(
                    exitCode: 0,
                    () => Task.CompletedTask,
                    () => throw new NotSupportedException("restore boom"));
                Assert.Equal(0, code);
            });

            Assert.Contains("console restore failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("restore boom", diagnostic, StringComparison.Ordinal);
        }

        public static TheoryData<Exception> OperationalFailures() =>
        [
            new NntpDbAuthenticationException("access denied"),
            new NntpDbUnavailableException("database unavailable"),
            new AcmeCaException("acme directory failed"),
            new CloudflareDnsException("dns update failed"),
            new InvalidOperationException("host exploded"),
            new OperationCanceledException("startup canceled"),
        ];
    }
}
