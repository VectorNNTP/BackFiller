using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    public sealed class ArticleRetentionSweepServiceTests
    {
        [Fact]
        public async Task Sweep_runs_immediately_then_stops_on_cancellation_and_closes_admission()
        {
            var authority = new RecordingRetentionAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            using var cts = new CancellationTokenSource();
            await sweep.StartAsync(cts.Token);
            await authority.Swept.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await sweep.StopAsync(CancellationToken.None);
            Assert.True(authority.ShutdownStarted);
            Assert.True(authority.SweepCount >= 1);
        }

        [Fact]
        public async Task Sweep_continues_after_a_failed_pass()
        {
            var authority = new ThrowingThenSucceedingAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            using var cts = new CancellationTokenSource();
            await sweep.StartAsync(cts.Token);
            await authority.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await sweep.StopAsync(CancellationToken.None);
            Assert.True(authority.Failures >= 1);
            Assert.True(authority.SweepCount >= 2);
            Assert.True(authority.ShutdownStarted);
        }

        [Fact]
        public async Task Process_fatal_sweep_failure_leaves_the_hosted_loop()
        {
            var authority = new FatalSweepAuthority();
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);

            await sweep.StartAsync(CancellationToken.None);
            var execute = sweep.ExecuteTask;
            Assert.NotNull(execute);
            var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() => execute!);

            Assert.Equal("sweep exhausted", thrown.Message);
            Assert.Equal(1, authority.SweepCount);
        }

        [Fact]
        public async Task Sweep_interval_is_the_configured_retention_interval()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 64, sweep: TimeSpan.FromSeconds(7));
            Assert.Equal(TimeSpan.FromSeconds(7), authority.SweepInterval);
            var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
            Assert.Equal(TimeSpan.FromSeconds(7), ((IArticleRetentionAuthority)authority).SweepInterval);
            await sweep.StopAsync(CancellationToken.None);
        }

        private sealed class RecordingRetentionAuthority : IArticleRetentionAuthority
        {
            public TaskCompletionSource Swept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SweepCount { get; private set; }

            public bool ShutdownStarted { get; private set; }

            public TimeSpan SweepInterval => TimeSpan.FromHours(1);

            public long RetainedPayloadBytes => 0;

            public int RetainedCount => 0;

            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleRecord record,
                VectorNNTP.BackFiller.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            public VatpOpenResult TryOpenTransfer(
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleId expectedArticleId,
                Func<int, bool>? tryReserveOutboundBytes = null) =>
                VatpOpenResult.Rejected();

            public bool TryCancelPendingRequest(Guid requestId) => false;

            public long SweepExpired()
            {
                SweepCount++;
                Swept.TrySetResult();
                return 0;
            }

            public void BeginShutdown() => ShutdownStarted = true;
        }

        private sealed class ThrowingThenSucceedingAuthority : IArticleRetentionAuthority
        {
            public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SweepCount { get; private set; }

            public int Failures { get; private set; }

            public bool ShutdownStarted { get; private set; }

            public TimeSpan SweepInterval => TimeSpan.Zero;

            public long RetainedPayloadBytes => 0;

            public int RetainedCount => 0;

            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleRecord record,
                VectorNNTP.BackFiller.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            public VatpOpenResult TryOpenTransfer(
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleId expectedArticleId,
                Func<int, bool>? tryReserveOutboundBytes = null) =>
                VatpOpenResult.Rejected();

            public bool TryCancelPendingRequest(Guid requestId) => false;

            public long SweepExpired()
            {
                SweepCount++;
                if (SweepCount == 1)
                {
                    Failures++;
                    throw new InvalidOperationException("sweep failed");
                }

                Recovered.TrySetResult();
                return 0;
            }

            public void BeginShutdown() => ShutdownStarted = true;
        }

        private sealed class FatalSweepAuthority : IArticleRetentionAuthority
        {
            public int SweepCount { get; private set; }

            public TimeSpan SweepInterval => TimeSpan.FromHours(1);

            public long RetainedPayloadBytes => 0;

            public int RetainedCount => 0;

            public ArticleRetentionResult RetainCanonical(
                string messageId,
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleRecord record,
                VectorNNTP.BackFiller.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName,
                string? endpointFqdn = null) =>
                new(ArticleRetentionKind.ShuttingDown, null, null, null, 0, 0);

            public VatpOpenResult TryOpenTransfer(
                Guid requestId,
                VectorNNTP.BackFiller.Articles.ArticleId expectedArticleId,
                Func<int, bool>? tryReserveOutboundBytes = null) =>
                VatpOpenResult.Rejected();

            public bool TryCancelPendingRequest(Guid requestId) => false;

            public long SweepExpired()
            {
                SweepCount++;
                throw new OutOfMemoryException("sweep exhausted");
            }

            public void BeginShutdown()
            {
            }
        }
    }
}
