using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    public sealed class ArticleDeliveryClaimTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Commit_removes_the_entry_and_subtracts_bytes_once()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var before = authority.RetainedPayloadBytes;

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            var claim = claimed.Claim;
            Assert.NotNull(claim);
            Assert.True(claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            Assert.Equal("<a@b>", claim.MessageId);
            Assert.Equal(before, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            claim.Commit();
            claim.Dispose();

            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
        }

        [Fact]
        public void Rollback_leaves_an_unexpired_entry_claimable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var before = authority.RetainedPayloadBytes;

            var first = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, first.Kind);
            first.Claim!.Dispose();
            first.Claim.Dispose();

            Assert.Equal(before, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            var second = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, second.Kind);
            Assert.True(second.Claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            second.Claim.Dispose();
        }

        [Fact]
        public void Commit_clears_every_request_id_on_the_entry()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<a@b>", "one\r\n");
            var second = RetentionTestArticles.Create("<a@b>", "two-bytes\r\n");
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                "<a@b>", second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            claimed.Claim!.Commit();

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.False(authority.TryCancelPendingRequest(first.RequestId));
            Assert.False(authority.TryCancelPendingRequest(second.RequestId));
        }

        [Fact]
        public void A_second_claim_is_rejected_until_the_first_is_released()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var first = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, first.Kind);

            var second = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.InDelivery, second.Kind);
            Assert.Null(second.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));

            first.Claim!.Dispose();
            var third = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, third.Kind);
            Assert.True(third.Claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            third.Claim.Dispose();
        }

        [Fact]
        public void Ttl_during_a_claim_keeps_the_captured_bytes_and_does_not_resurrect_the_entry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            var captured = claimed.Claim!.ArticleBytes.ToArray();

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(captured));
            Assert.Equal(0, authority.RetainedPayloadBytes);

            claimed.Claim.Commit();
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
        }

        [Fact]
        public void Rollback_after_ttl_does_not_restore_the_entry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);

            claim!.Dispose();

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
        }

        [Fact]
        public void An_active_claim_pins_fifo_reclaim_after_the_last_request_id_is_cancelled()
        {
            var time = new ManualTimeProvider(Start);
            var first = RetentionTestArticles.Create("<a@b>", "same-body\r\n");
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: first.Record.ArtSize);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));

            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            Assert.NotNull(claim);
            var second = RetentionTestArticles.Create("<c@d>", "same-body\r\n");
            var blocked = authority.RetainCanonical(
                "<c@d>", second.RequestId, second.Record, second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, blocked.Kind);
            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            claim!.Dispose();
            var admitted = authority.RetainCanonical(
                "<c@d>", second.RequestId, second.Record, second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, authority.TryClaimForDelivery(Id("<c@d>")).Kind);
        }

        [Fact]
        public async Task Dispose_during_a_claim_does_not_resurrect_or_double_release()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            var captured = claim!.ArticleBytes.ToArray();

            await authority.DisposeAsync();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(captured));

            claim.Commit();
            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            Assert.Throws<ObjectDisposedException>(() => authority.TryClaimForDelivery(Id("<a@b>")));
        }

        [Fact]
        public void Begin_shutdown_still_allows_a_claim()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            authority.BeginShutdown();
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            claimed.Claim!.Dispose();
        }

        [Fact]
        public void An_article_that_does_not_end_in_crlf_is_not_claimed()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "unterminated");
            Assert.False(prepared.Record.ArtData.Span.EndsWith("\r\n"u8));
            var before = prepared.Record.ArtData.ToArray();

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, claimed.Kind);
            Assert.Null(claimed.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
        }

        [Fact]
        public void Commit_at_exact_expiry_without_a_sweep_removes_the_entry_once()
        {
            SettleAtExactExpiry(commit: true);
        }

        [Fact]
        public void Rollback_at_exact_expiry_without_a_sweep_removes_the_entry_once()
        {
            SettleAtExactExpiry(commit: false);
        }

        private static void SettleAtExactExpiry(bool commit)
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var sibling = Guid.NewGuid();
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                "<a@b>", sibling, prepared.Record, prepared.SelectedDateHeaderName).Kind);
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            var claim = claimed.Claim;
            Assert.NotNull(claim);
            var captured = claim!.ArticleBytes.ToArray();

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
            if (commit)
            {
                claim.Commit();
            }

            claim.Dispose();
            claim.Dispose();
            if (commit)
            {
                claim.Commit();
            }

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.False(authority.TryCancelPendingRequest(sibling));
            Assert.Equal(0, authority.SweepExpired());
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(captured));
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
        }

        private static byte[] Id(string messageId) => Encoding.ASCII.GetBytes(messageId);
    }
}
