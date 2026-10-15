using System.Reflection;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    /// <summary>
    /// Hard TTL: expiry drops indexes, accounting, and the authority's article reference
    /// even while a transfer lease still exists.
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
            using (var early = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, early.Kind);
            }

            Assert.True(authority.RetainedPayloadBytes > 0);
            time.Advance(TimeSpan.FromTicks(1));
            using var late = authority.TryOpenTransfer(second, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, late.Kind);
            Assert.Null(late.Lease);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public async Task Expired_entry_is_not_discoverable_and_cannot_acquire_a_lease_before_the_sweep()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<hidden@example.test>");
            time.Advance(TimeSpan.FromSeconds(10));

            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, open.Kind);
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
        public async Task Expiry_during_an_active_transfer_drops_authority_state_and_completion_releases_the_lease()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<active@example.test>");
            var sibling = Guid.NewGuid();
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(prepared.MessageId, sibling, prepared.Record, prepared.SelectedDateHeaderName).Kind);
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            var lease = open.Lease!;
            var payloadBytes = prepared.Record.ArtSize;

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(payloadBytes, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(lease.IsRevoked);
            Assert.False(lease.HoldsArticle);
            Assert.Throws<InvalidOperationException>(() => _ = lease.Record);

            using var siblingOpen = authority.TryOpenTransfer(sibling, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, siblingOpen.Kind);

            open.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.False(lease.HoldsArticle);
            open.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.SweepExpired());
        }

        [Fact]
        public async Task Lookup_during_an_active_transfer_expires_the_entry_without_waiting_for_the_sweep()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<lookup@example.test>");
            var sibling = Guid.NewGuid();
            authority.RetainCanonical(prepared.MessageId, sibling, prepared.Record, prepared.SelectedDateHeaderName);
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);

            time.Advance(TimeSpan.FromSeconds(10));
            using var siblingOpen = authority.TryOpenTransfer(sibling, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, siblingOpen.Kind);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(open.Lease!.IsRevoked);
            Assert.False(open.Lease.HoldsArticle);
            open.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task An_undisposed_lease_does_not_keep_expired_bytes_in_the_authority()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<pinned@example.test>");
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(open.Lease!.IsRevoked);
            open.Dispose();
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
            using var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            using var second = authority.TryOpenTransfer(later.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, first.Kind);
            Assert.Equal(VatpOpenKind.Rejected, second.Kind);
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
        public async Task Expiry_and_lease_release_subtract_the_expired_entry_once()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(
                time,
                maxBytes: 1024 * 1024,
                ttl: TimeSpan.FromSeconds(30));
            var first = RetentionTestArticles.RetainPrepared(authority, "<acct-a@example.test>", "a\r\n");
            time.Advance(TimeSpan.FromSeconds(10));
            var second = RetentionTestArticles.RetainPrepared(authority, "<acct-b@example.test>", "longer-body-bytes\r\n");
            using var open = authority.TryOpenTransfer(first.RequestId, first.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.Equal(first.Record.ArtSize + second.Record.ArtSize, authority.RetainedPayloadBytes);

            time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(first.Record.ArtSize, authority.SweepExpired());
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            open.Dispose();
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
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            var observed = 0;
            open.Lease!.SetRevocationObserver(() => observed++);
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.Equal(0, authority.SweepExpired());
            Assert.Equal(1, observed);
            open.Lease.Revoke();
            Assert.Equal(1, observed);
            open.Dispose();
            open.Dispose();
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
        public async Task A_failing_lease_observer_does_not_skip_later_leases()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<observers@example.test>");
            var sibling = Guid.NewGuid();
            authority.RetainCanonical(prepared.MessageId, sibling, prepared.Record, prepared.SelectedDateHeaderName);
            using var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            using var second = authority.TryOpenTransfer(sibling, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, first.Kind);
            Assert.Equal(VatpOpenKind.Opened, second.Kind);
            first.Lease!.SetRevocationObserver(static () => throw new InvalidOperationException("observer failed"));
            var secondObserved = 0;
            second.Lease!.SetRevocationObserver(() => secondObserved++);

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.True(first.Lease.IsRevoked);
            Assert.True(second.Lease.IsRevoked);
            Assert.Equal(1, secondObserved);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            first.Dispose();
            second.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task A_fatal_lease_observer_still_revokes_later_leases_and_then_propagates()
        {
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<fatal-observer@example.test>");
            var sibling = Guid.NewGuid();
            authority.RetainCanonical(prepared.MessageId, sibling, prepared.Record, prepared.SelectedDateHeaderName);
            using var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            using var second = authority.TryOpenTransfer(sibling, prepared.Record.ArtId);
            first.Lease!.SetRevocationObserver(static () => throw new OutOfMemoryException("observer exhausted"));
            var secondObserved = 0;
            second.Lease!.SetRevocationObserver(() => secondObserved++);

            time.Advance(TimeSpan.FromSeconds(10));
            var thrown = Assert.Throws<OutOfMemoryException>(() => authority.SweepExpired());
            Assert.Equal("observer exhausted", thrown.Message);
            Assert.True(first.Lease.IsRevoked);
            Assert.True(second.Lease.IsRevoked);
            Assert.Equal(1, secondObserved);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            first.Dispose();
            second.Dispose();
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
