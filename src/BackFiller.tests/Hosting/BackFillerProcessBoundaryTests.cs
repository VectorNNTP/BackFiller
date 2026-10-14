using VectorNNTP.BackFiller.Acme.Protocol;
using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.NntpDb;
using VectorNNTP.BackFiller.Tests.TestDoubles;

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
