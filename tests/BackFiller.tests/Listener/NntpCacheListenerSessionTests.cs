using System.Runtime.InteropServices;
using VectorNNTP.BackFiller.Articles;
using VectorNNTP.BackFiller.Articles.Parsing;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;

namespace VectorNNTP.BackFiller.Tests.Listener
{
    public sealed class NntpCacheListenerSessionTests
    {
        private static ReadOnlySpan<byte> Greeting => "201 NNTP Service Ready, posting prohibited\r\n"u8;

        private static ReadOnlySpan<byte> Terminator => ".\r\n"u8;

        [Fact]
        public async Task Article_writes_the_status_line_exact_bytes_and_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<Ab@Cd>");
            var written = await RunAsync(authority, "ARTICLE <Ab@Cd>\r\n");

            var follows = Encoding.ASCII.GetBytes("220 0 <Ab@Cd> article follows\r\n");
            Assert.True(written.AsSpan(0, Greeting.Length).SequenceEqual(Greeting));
            var bodyStart = Greeting.Length + follows.Length;
            Assert.True(written.AsSpan(Greeting.Length, follows.Length).SequenceEqual(follows));
            Assert.True(written.AsSpan(bodyStart, prepared.ArtData.Length).SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(bodyStart + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task Existing_dot_stuffing_is_preserved_and_exactly_one_terminator_is_appended()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", ".\r\n");
            Assert.Contains(Encoding.ASCII.GetBytes("\r\n..\r\n"), prepared.ArtData.AsSpan());

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            var follows = Status("<a@b>");
            var article = written.AsSpan(Greeting.Length + follows.Length, prepared.ArtData.Length);
            Assert.True(article.SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(0, authority.RetainedCount);
        }

        [Theory]
        [InlineData("cr-only\r")]
        [InlineData("lf-only\n")]
        public async Task Article_ending_in_cr_or_lf_only_is_refused_without_mutation(string body)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.ArtData.ToArray();
            Assert.False(before.AsSpan().EndsWith("\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nQUIT\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(Encoding.ASCII.GetBytes(body), written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        [Theory]
        [InlineData("plain\nmid.dot\r\n")]
        [InlineData("plain\rmid.dot\r\n")]
        public async Task Interior_bare_cr_or_lf_returns_403_before_any_article_byte(string body)
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.ArtData.ToArray();
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        [Fact]
        public async Task Nul_in_a_retained_article_is_403_before_any_article_byte()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "body\r\n");
            var body = prepared.Record.ArtData.Span.IndexOf("body\r\n"u8);
            Assert.True(body > 0);
            WriteRetainedByte(prepared.Record, body, 0);
            var before = prepared.Record.ArtData.ToArray();
            Assert.Contains(before, static value => value == 0);
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<a@b>")).Kind);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        [Fact]
        public async Task Dot_leading_line_without_a_separator_is_403_before_any_article_byte()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", ".only\r\n");
            var separator = prepared.Record.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            WriteRetainedByte(prepared.Record, separator, (byte)'Z');
            WriteRetainedByte(prepared.Record, separator + 1, (byte)'Z');
            var before = prepared.Record.ArtData.ToArray();
            Assert.True(before.AsSpan().IndexOf("\r\n\r\n"u8) < 0);
            Assert.Contains("..only\r\n"u8, before);

            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<a@b>")).Kind);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");

            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain("220 "u8, written);
            Assert.DoesNotContain(before, written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        [Fact]
        public async Task Dot_leading_header_is_stuffed_on_the_wire_and_the_body_is_decoded_once()
        {
            var logical = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: <dot-header@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: dots\r\n"
                + "Path: peer.example\r\n"
                + ".X-Dot: kept\r\n"
                + "\r\n"
                + ".only\r\n"
                + "..two\r\n"
                + "...three\r\n"
                + "mid.dot\r\n");
            var parser = new NntpArticleParser("backfiller.test");
            var created = ArticleRecordFactory.TryCreate(parser, logical);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var retained = created.Record.ArtData.ToArray();
            Assert.Contains(".X-Dot: kept\r\n"u8, retained);
            Assert.DoesNotContain("..X-Dot:"u8, retained);
            Assert.Contains("..only\r\n"u8, retained);
            Assert.Contains("...two\r\n"u8, retained);
            Assert.Contains("....three\r\n"u8, retained);

            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    "<dot-header@example.test>",
                    Guid.NewGuid(),
                    created.Record,
                    created.SelectedDateHeaderName).Kind);

            var written = await RunAsync(authority, "ARTICLE <dot-header@example.test>\r\n");
            var follows = Status("<dot-header@example.test>");
            var article = written.AsSpan(Greeting.Length + follows.Length);
            Assert.True(article.EndsWith(Terminator));
            Assert.Equal(1, Count(article, Terminator));
            var block = article[..^Terminator.Length];
            Assert.Contains("..X-Dot: kept\r\n"u8, block);
            Assert.Contains("..only\r\n"u8, block);
            Assert.Contains("...two\r\n"u8, block);
            Assert.Contains("....three\r\n"u8, block);
            Assert.DoesNotContain("...only"u8, block);
            Assert.DoesNotContain(".....three"u8, block);

            var decoded = DestuffMultiline(block);
            Assert.True(decoded.AsSpan().SequenceEqual(LogicalFromRetained(retained)));
            Assert.Contains(".only\r\n"u8, decoded);
            Assert.Contains("..two\r\n"u8, decoded);
            Assert.Contains("...three\r\n"u8, decoded);
            Assert.DoesNotContain("..only"u8, decoded);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(retained));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task Article_ending_in_crlf_is_transmitted_once_with_one_terminator()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "ends-crlf\r\n");
            Assert.True(prepared.ArtData.AsSpan().EndsWith("\r\n"u8));
            Assert.False(prepared.ArtData.AsSpan().EndsWith("\r\n.\r\n"u8));

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            var follows = Status("<a@b>");
            var article = written.AsSpan(Greeting.Length + follows.Length, prepared.ArtData.Length);
            Assert.True(article.SequenceEqual(prepared.ArtData));
            Assert.True(written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length).SequenceEqual(Terminator));
            Assert.Equal(1, Count(written, Terminator));
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Non_crlf_terminated_article_is_refused_without_mutation()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "unterminated");
            var before = prepared.ArtData.ToArray();

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nQUIT\r\n");

            Assert.Equal(Greeting.Length + "403 Article not available\r\n"u8.Length + "205 Connection closing\r\n"u8.Length, written.Length);
            Assert.Contains("403 Article not available\r\n"u8, written);
            Assert.DoesNotContain(Encoding.ASCII.GetBytes("unterminated"), written.AsSpan());
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
        }

        [Fact]
        public async Task Missing_article_is_430_and_invalid_forms_use_501_or_412()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var written = await RunAsync(
                authority,
                "ARTICLE <missing@b>\r\n"
                + "ARTICLE\r\n"
                + "ARTICLE 15\r\n"
                + "ARTICLE <not-an-id>\r\n"
                + "ARTICLE <a@b> extra\r\n"
                + "NOPE\r\n"
                + "QUIT extra\r\n"
                + "QUIT\r\n");

            Assert.Equal(
                Greeting.ToArray()
                    .Concat("430 No Such Article Found\r\n"u8.ToArray())
                    .Concat("412 No newsgroup selected\r\n"u8.ToArray())
                    .Concat("412 No newsgroup selected\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("501 Syntax error\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        [Fact]
        public async Task Keywords_are_case_insensitive_and_spaces_or_tabs_separate_the_argument()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "article\t<a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Repeated_spaces_are_accepted()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "ARTICLE   <a@b>  \r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Overlong_line_is_one_501_and_the_following_command_is_preserved()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var overlong = new string('X', 511) + "\r\n";
            var written = await RunAsync(authority, overlong + "ARTICLE <a@b>\r\n", maxRead: 64);

            Assert.Contains("501 Syntax error\r\n"u8, written);
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task A_512_octet_line_is_parsed_as_one_command()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var line = new string('Y', 510) + "\r\n";
            Assert.Equal(512, Encoding.ASCII.GetByteCount(line));
            var written = await RunAsync(authority, line + "QUIT\r\n", maxRead: 64);
            Assert.Equal(
                Greeting.ToArray()
                    .Concat("500 Unknown command\r\n"u8.ToArray())
                    .Concat("205 Connection closing\r\n"u8.ToArray())
                    .ToArray(),
                written);
        }

        [Fact]
        public async Task Pipelined_commands_are_processed_in_order()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var first = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var second = RetentionTestArticles.RetainPrepared(authority, "<c@d>");
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\nARTICLE <c@d>\r\n");

            var firstStatus = IndexOf(written, Status("<a@b>"));
            var secondStatus = IndexOf(written, Status("<c@d>"));
            Assert.True(firstStatus >= 0 && secondStatus > firstStatus);
            Assert.True(written.AsSpan(firstStatus + Status("<a@b>").Length, first.ArtData.Length).SequenceEqual(first.ArtData));
            Assert.True(written.AsSpan(secondStatus + Status("<c@d>").Length, second.ArtData.Length).SequenceEqual(second.ArtData));
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Quit_does_not_process_a_following_pipelined_command()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "QUIT\r\nARTICLE <a@b>\r\n");
            Assert.Equal(
                Greeting.ToArray().Concat("205 Connection closing\r\n"u8.ToArray()).ToArray(),
                written);
            Assert.Equal(1, authority.RetainedCount);
        }

        [Fact]
        public async Task Peer_close_before_a_command_sends_no_further_response()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var written = await RunAsync(authority, "ART");
            Assert.True(written.AsSpan().SequenceEqual(Greeting));
        }

        [Fact]
        public async Task Failed_status_body_and_terminator_writes_do_not_consume_the_entry()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var command = "ARTICLE <a@b>\r\n";

            var status = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 2 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(status, authority).RunAsync(CancellationToken.None));
            Assert.True(status.Written.AsSpan().SequenceEqual(Greeting));
            Assert.Equal(1, authority.RetainedCount);

            var body = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 3 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(body, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.DoesNotContain(prepared.ArtData, body.Written);

            var follows = Status("<a@b>");
            var midBody = new ScriptedTransport(Encoding.ASCII.GetBytes(command))
            {
                FailAfterAcceptedBytes = Greeting.Length + follows.Length + 4,
            };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(midBody, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(Greeting.Length + follows.Length + 4, midBody.Written.Length);
            Assert.False(midBody.Written.AsSpan(midBody.Written.Length - 3).SequenceEqual(Terminator));

            var terminator = new ScriptedTransport(Encoding.ASCII.GetBytes(command)) { FailOnWriteNumber = 4 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(terminator, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(prepared.ArtData.Length, authority.RetainedPayloadBytes);
            Assert.Contains(prepared.ArtData, terminator.Written);
            Assert.DoesNotContain(Terminator, terminator.Written.AsSpan(Greeting.Length + follows.Length + prepared.ArtData.Length));

            var recovered = await RunAsync(authority, command);
            Assert.Contains(prepared.ArtData, recovered);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Cancellation_during_the_article_write_releases_the_claim_for_a_retry()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var blocked = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { BlockOnWriteNumber = 3 };
            using var cts = new CancellationTokenSource();
            var running = new NntpCacheListenerSession(blocked, authority).RunAsync(cts.Token);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(1, authority.RetainedCount);

            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public async Task Two_sessions_cannot_deliver_the_same_entry_and_commit_makes_the_next_request_430()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var first = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { BlockOnWriteNumber = 3 };
            var second = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\nQUIT\r\n"));
            var running = new NntpCacheListenerSession(first, authority).RunAsync(CancellationToken.None);
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await new NntpCacheListenerSession(second, authority).RunAsync(CancellationToken.None);
            Assert.Contains("403 Article not available\r\n"u8, second.Written);
            Assert.Contains("205 Connection closing\r\n"u8, second.Written);
            Assert.Equal(1, authority.RetainedCount);

            first.Release.TrySetResult();
            await running;
            Assert.Equal(0, authority.RetainedCount);

            var third = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, third);
        }

        [Fact]
        public async Task A_completed_write_stays_consumed_when_the_client_then_disconnects()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains(Status("<a@b>"), written);
            Assert.Equal(0, authority.RetainedCount);

            var again = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, again);
        }

        [Fact]
        public async Task A_retry_after_rollback_fails_once_ttl_has_expired()
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await using var authority = ArticleRetentionAuthorityTests.Create(time, 1_000_000, TimeSpan.FromSeconds(60));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var failed = new ScriptedTransport(Encoding.ASCII.GetBytes("ARTICLE <a@b>\r\n")) { FailOnWriteNumber = 2 };
            await Assert.ThrowsAsync<IOException>(() => new NntpCacheListenerSession(failed, authority).RunAsync(CancellationToken.None));
            Assert.Equal(1, authority.RetainedCount);

            time.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(prepared.ArtData.Length, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedCount);
            var written = await RunAsync(authority, "ARTICLE <a@b>\r\n");
            Assert.Contains("430 No Such Article Found\r\n"u8, written);
            Assert.Equal(0, authority.RetainedCount);
        }

        private static async Task<byte[]> RunAsync(ArticleRetentionAuthority authority, string commands, int maxRead = int.MaxValue)
        {
            var transport = new ScriptedTransport(Encoding.ASCII.GetBytes(commands), maxRead);
            await new NntpCacheListenerSession(transport, authority).RunAsync(CancellationToken.None);
            return transport.Written;
        }

        private static int Count(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
        {
            var count = 0;
            for (var index = 0; index <= source.Length - value.Length; index++)
            {
                if (source.Slice(index, value.Length).SequenceEqual(value))
                {
                    count++;
                }
            }

            return count;
        }

        private static byte[] LogicalFromRetained(byte[] artData)
        {
            var separator = artData.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            var bodyStart = separator + 4;
            var body = DestuffMultiline(artData.AsSpan(bodyStart));
            var logical = new byte[bodyStart + body.Length];
            artData.AsSpan(0, bodyStart).CopyTo(logical);
            body.CopyTo(logical.AsSpan(bodyStart));
            return logical;
        }

        private static byte[] DestuffMultiline(ReadOnlySpan<byte> block)
        {
            var buffer = new byte[block.Length];
            var written = 0;
            var index = 0;
            while (index < block.Length)
            {
                var crlf = block[index..].IndexOf("\r\n"u8);
                Assert.True(crlf >= 0);
                var lineLength = crlf + 2;
                var line = block.Slice(index, lineLength);
                if (line[0] == (byte)'.' && !(line.Length == 3 && line[1] == (byte)'\r'))
                {
                    line[1..].CopyTo(buffer.AsSpan(written));
                    written += line.Length - 1;
                }
                else
                {
                    line.CopyTo(buffer.AsSpan(written));
                    written += line.Length;
                }

                index += lineLength;
            }

            return buffer.AsSpan(0, written).ToArray();
        }

        private static byte[] Status(string messageId) =>
            Encoding.ASCII.GetBytes("220 0 " + messageId + " article follows\r\n");

        private static void WriteRetainedByte(ArticleRecord record, int index, byte value)
        {
            Assert.True(MemoryMarshal.TryGetArray(record.ArtData, out ArraySegment<byte> segment));
            segment.Array![segment.Offset + index] = value;
        }

        private static int IndexOf(byte[] buffer, byte[] pattern)
        {
            for (var i = 0; i <= buffer.Length - pattern.Length; i++)
            {
                if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                {
                    return i;
                }
            }

            return -1;
        }

        private sealed class ScriptedTransport : ICacheListenerTransport
        {
            private readonly byte[] _inbound;

            private readonly int _maxRead;

            private readonly List<byte> _written = [];

            private int _offset;

            private int _accepted;

            private int _writeCalls;

            internal ScriptedTransport(byte[] inbound, int maxRead = int.MaxValue)
            {
                _inbound = inbound;
                _maxRead = maxRead;
            }

            internal byte[] Written => _written.ToArray();

            internal int FailOnWriteNumber { get; init; }

            internal int FailAfterAcceptedBytes { get; init; } = int.MaxValue;

            internal int BlockOnWriteNumber { get; init; }

            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_offset >= _inbound.Length || buffer.IsEmpty)
                {
                    return ValueTask.FromResult(0);
                }

                var count = Math.Min(buffer.Length, Math.Min(_maxRead, _inbound.Length - _offset));
                _inbound.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                _writeCalls++;
                if (_writeCalls == BlockOnWriteNumber)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (FailOnWriteNumber == _writeCalls || _accepted >= FailAfterAcceptedBytes)
                {
                    throw new IOException("scripted write failure");
                }

                var count = Math.Min(buffer.Length, FailAfterAcceptedBytes - _accepted);
                for (var i = 0; i < count; i++)
                {
                    _written.Add(buffer.Span[i]);
                }

                _accepted += count;
                return count;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
