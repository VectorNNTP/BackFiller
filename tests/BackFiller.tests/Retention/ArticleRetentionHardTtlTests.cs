using System.Reflection;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    /// <summary>
    /// Hard TTL: expiry drops indexes and accounting even while a delivery claim still holds captured bytes.
    /// </summary>
    public sealed class ArticleRetentionHardTtlTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task Entry_expires_at_the_exact_deadline_without_a_sweep()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<exact@example.test>");
            var second = Guid.NewGuid();
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(prepared.MessageId, second, prepared.Record, prepared.SelectedDateHeaderName).Kind);

            time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
            using (var early = ArticleRetentionAuthorityTests.Claim(authority, "<exact@example.test>", prepared.ArtData))
            {
                Assert.True(authority.RetainedPayloadBytes > 0);
            }

            time.Advance(TimeSpan.FromTicks(1));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<exact@example.test>");
            Assert.False(authority.TryCancelPendingRequest(second));
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public async Task Expired_entry_is_not_discoverable_before_the_sweep()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            _ = RetentionTestArticles.RetainPrepared(authority, "<hidden@example.test>");
            time.Advance(TimeSpan.FromSeconds(10));

            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<hidden@example.test>");
            Assert.Equal(0, authority.RetainedPayloadBytes);

            var replacement = RetentionTestArticles.Create("<hidden@example.test>", "fresh\r\n");
            var admitted = authority.RetainCanonical(
                replacement.MessageId,
                replacement.RequestId,
                replacement.Record,
                replacement.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(replacement.Record.ArtSize, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task Expiry_during_an_active_claim_drops_authority_state_and_keeps_captured_bytes()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<active@example.test>");
            var sibling = Guid.NewGuid();
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(prepared.MessageId, sibling, prepared.Record, prepared.SelectedDateHeaderName).Kind);
            var claim = ArticleRetentionAuthorityTests.Claim(authority, "<active@example.test>", prepared.ArtData);
            var payloadBytes = prepared.Record.ArtSize;

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(payloadBytes, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            Assert.False(authority.TryCancelPendingRequest(sibling));

            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public async Task A_second_claim_at_expiry_removes_the_entry_without_a_sweep()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<lookup@example.test>");
            var claim = ArticleRetentionAuthorityTests.Claim(authority, "<lookup@example.test>", prepared.ArtData);

            time.Advance(TimeSpan.FromSeconds(10));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<lookup@example.test>");
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task An_unsettled_claim_does_not_keep_expired_bytes_in_the_authority()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<pinned@example.test>");
            var claim = ArticleRetentionAuthorityTests.Claim(authority, "<pinned@example.test>", prepared.ArtData);
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            claim.Dispose();
        }

        [Fact]
        public async Task Duplicate_message_id_and_a_later_request_id_do_not_extend_ttl()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<dup@example.test>", "first-body\r\n");
            time.Advance(TimeSpan.FromSeconds(9));
            var later = RetentionTestArticles.Create("<dup@example.test>", "second-body\r\n");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(later.MessageId, later.RequestId, later.Record, later.SelectedDateHeaderName).Kind);

            time.Advance(TimeSpan.FromSeconds(1));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<dup@example.test>");
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.False(authority.TryCancelPendingRequest(later.RequestId));
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Fifo_reclaim_replaces_retained_bytes_exactly()
        {
            var time = new ManualTimeProvider(Start);
            var sample = RetentionTestArticles.Create("<fifo-size@example.test>", "a\r\n");
            await using var authority = ArticleRetentionAuthorityTests.Create(
                time,
                maxBytes: sample.Record.ArtSize,
                ttl: TimeSpan.FromSeconds(30));
            var first = RetentionTestArticles.RetainPrepared(authority, "<fifo-a@example.test>", "a\r\n");
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));
            var second = RetentionTestArticles.Create("<fifo-b@example.test>", "a\r\n");
            var admitted = authority.RetainCanonical(
                second.MessageId,
                second.RequestId,
                second.Record,
                second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
        }

        [Fact]
        public async Task Expiry_and_claim_release_subtract_the_expired_entry_once()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(
                time,
                maxBytes: 1024 * 1024,
                ttl: TimeSpan.FromSeconds(30));
            var first = RetentionTestArticles.RetainPrepared(authority, "<acct-a@example.test>", "a\r\n");
            time.Advance(TimeSpan.FromSeconds(10));
            var second = RetentionTestArticles.RetainPrepared(authority, "<acct-b@example.test>", "longer-body-bytes\r\n");
            var claim = ArticleRetentionAuthorityTests.Claim(authority, "<acct-a@example.test>", first.ArtData);
            Assert.Equal(first.Record.ArtSize + second.Record.ArtSize, authority.RetainedPayloadBytes);

            time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(first.Record.ArtSize, authority.SweepExpired());
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            claim.Dispose();
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(second.Record.ArtSize, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public async Task Repeated_expiry_and_disposal_are_idempotent()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<idem@example.test>");
            var claim = ArticleRetentionAuthorityTests.Claim(authority, "<idem@example.test>", prepared.ArtData);
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.Equal(0, authority.SweepExpired());
            claim.Dispose();
            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);

            authority.BeginShutdown();
            var shut = authority.RetainCanonical(
                prepared.MessageId,
                Guid.NewGuid(),
                prepared.Record,
                prepared.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.ShuttingDown, shut.Kind);
            await authority.DisposeAsync();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            await authority.DisposeAsync();
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public void Handler_does_not_keep_a_production_article_reference()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var handler = typeof(ProviderArticleWorkHandler);
            Assert.Null(handler.GetProperty("LastPayload", flags));
            Assert.Null(handler.GetProperty("LastRecord", flags));
            Assert.NotNull(handler.GetProperty("LastKind", flags));
        }
    }
}
