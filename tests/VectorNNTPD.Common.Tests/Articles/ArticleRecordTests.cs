using System.Reflection;
using System.Text;
using VectorNNTPD.Common.Articles;
using System.IO.Hashing;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Canonical V1 records store final ArtData ranges, Diablo <see cref="ArticleType"/> flags, and an XXH3-64 ArtHash of the whole article.
    /// </summary>
    public sealed class ArticleRecordTests
    {
        /// <summary>Local hop inserted into canonical Path.</summary>
        private const string LocalFqdn = "backfiller01.usenet.ninja";

        /// <summary>
        /// An accepted article is <see cref="ArticleParseStatus.CanonicalV1"/>, uses <see cref="ArticleType"/> rather than <c>NntpArticleType</c>,
        /// stores rewritten Path bytes, shifts the Subject range off the destuffed offset, and sets ArtId from the Message-ID value.
        /// ArtHash matches XXH3-64 of ArtData, and ArtSize is not the destuffed length.
        /// </summary>
        [Fact]
        public void TryCreate_ProducesCanonicalV1RecordWithDiabloTypeAndFinalRanges()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<shift@example.test>",
                newsgroups: "alt.test,alt.binaries.test",
                from: "user@example.test",
                subject: "after-path",
                references: "<prev@example.test>",
                body: "line1\r\nline2\r\n");

            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var destuffedSubjectOffset = FindHeaderValueOffset(parse, NntpArticleHeaderName.Subject);
            var destuffedPath = parse.OriginalPathValue.ToArray();

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;

            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(record.ArtData.Length, record.ArtSize);
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
            Assert.Equal(parse.BodyLineCount, record.ArtLines);
            Assert.Equal(2, record.ArtLines);
            Assert.Equal(parse.CanonicalUtc, record.CanonicalUtc);
            Assert.Equal(ArticleType.Default, record.ArtType);
            Assert.NotEqual(typeof(NntpArticleType), record.ArtType.GetType());

            Assert.True(record.Fields.MessageId.IsPresent);
            Assert.True(record.Fields.Newsgroups.IsPresent);
            Assert.True(record.Fields.Subject.IsPresent);
            Assert.True(record.Fields.From.IsPresent);
            Assert.True(record.Fields.Date.IsPresent);
            Assert.True(record.Fields.References.IsPresent);
            Assert.True(record.Fields.Path.IsPresent);

            Assert.True(record.MessageId.SequenceEqual("<shift@example.test>"u8));
            Assert.True(record.Newsgroups.SequenceEqual("alt.test,alt.binaries.test"u8));
            Assert.True(record.Subject.SequenceEqual("after-path"u8));
            Assert.True(record.From.SequenceEqual("user@example.test"u8));
            Assert.True(record.References.SequenceEqual("<prev@example.test>"u8));
            Assert.True(record.Path.SequenceEqual("news.usenet.ninja!backfiller01.usenet.ninja!peer.example"u8));
            Assert.False(record.Path.SequenceEqual(destuffedPath));
            Assert.NotEqual(destuffedSubjectOffset, record.Fields.Subject.Offset);
            Assert.True(record.ArtData.Span.Slice(record.Fields.Subject.Offset, record.Fields.Subject.Length)
                .SequenceEqual("after-path"u8));

            Assert.Equal(ArticleId.FromMessageId(record.MessageId), record.ArtId);
            Assert.NotEqual(destuffed.Length, record.ArtSize);
        }

        /// <summary>
        /// A yEnc body is classified with <see cref="ArticleType.YEncoded"/> and <see cref="ArticleType.Binary"/>,
        /// the <c>=ybegin</c> line remains in ArtData, and a missing References field stays absent.
        /// </summary>
        [Fact]
        public void TryCreate_YEnc_ClassifiesDiabloFlagsAndLeavesEncodedBody()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var decoded = new byte[] { 0x41 };
            var crc = YEncCrc32.Compute(decoded);
            var encoded = unchecked((byte)(decoded[0] + 42));
            var body = $"=ybegin line=128 size=1 name=t.bin\r\n{(char)encoded}\r\n=yend size=1 crc32={crc:x8}\r\n";
            var destuffed = BuildDestuffed(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<yenc@example.test>",
                newsgroups: "alt.binaries.test",
                from: "user@example.test",
                subject: "y",
                references: null,
                body: body);

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var record = created.Record;
            Assert.True(record.ArtType.HasFlag(ArticleType.YEncoded));
            Assert.True(record.ArtType.HasFlag(ArticleType.Binary));
            Assert.True(record.ArtData.Span.IndexOf("=ybegin"u8) >= 0);
            Assert.False(record.Fields.References.IsPresent);
        }

        /// <summary>
        /// ArtHash equals XXH3-64 of the full ArtData span, and hashing a one-byte mutation of that span does not equal the stored hash.
        /// </summary>
        [Fact]
        public void TryCreate_ArtHash_CoversEntireArtData()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<hash@example.test>",
                newsgroups: "alt.test",
                from: "user@example.test",
                subject: "hash",
                references: null,
                body: "payload\r\n");

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);

            var mutated = record.ArtData.ToArray();
            mutated[^2] ^= 0x01;
            Assert.NotEqual(record.ArtHash, XxHash3.HashToUInt64(mutated));
            Assert.Equal(record.ArtHash, XxHash3.HashToUInt64(record.ArtData.Span));
        }

        /// <summary>
        /// <see cref="ArticleRecord"/> exposes no <see cref="List{T}"/> or <see cref="string"/> array properties, and Newsgroups is an <see cref="ArticleByteRange"/>.
        /// </summary>
        [Fact]
        public void TryCreate_DoesNotCreateGroupStringCollection()
        {
            var recordType = typeof(ArticleRecord);
            foreach (var property in recordType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.NotEqual(typeof(List<string>), property.PropertyType);
                Assert.NotEqual(typeof(string[]), property.PropertyType);
                Assert.False(
                    property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>));
            }

            var fieldTableType = typeof(ArticleFieldTable);
            Assert.Equal(typeof(ArticleByteRange), fieldTableType.GetProperty(nameof(ArticleFieldTable.Newsgroups))!.PropertyType);
        }

        /// <summary>
        /// After warmup, one more <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte})"/> allocates at least ArtSize bytes and less than twice ArtSize.
        /// </summary>
        [Fact]
        public void TryCreate_AllocatesOneArticleSizedBuffer()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<alloc@example.test>",
                newsgroups: "alt.test",
                from: "user@example.test",
                subject: "alloc",
                references: null,
                body: "body\r\n");

            _ = ArticleRecordFactory.TryCreate(parser, destuffed);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var artSize = created.Record.ArtSize;

            var before = GC.GetAllocatedBytesForCurrentThread();
            var again = ArticleRecordFactory.TryCreate(parser, destuffed);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(again.IsAccepted);
            Assert.Equal(artSize, again.Record.ArtSize);
            Assert.True(allocated >= artSize);
            Assert.True(allocated < artSize * 2, $"allocated {allocated} for ArtSize {artSize}");
        }

        /// <summary>
        /// Text that is not an article is rejected with a non-<see cref="NntpArticleParseFailureCode.None"/> parse failure and <see cref="ArticleParseStatus.None"/>.
        /// </summary>
        [Fact]
        public void TryCreate_RejectsInvalidArticle()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var created = ArticleRecordFactory.TryCreate(parser, "not-an-article"u8.ToArray());
            Assert.False(created.IsAccepted);
            Assert.NotEqual(NntpArticleParseFailureCode.None, created.ParseFailure);
            Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
        }

        /// <summary>
        /// Canonical V1 means the record's own Message-ID bytes, ArtId, and ArtHash agree. ArtId is not the digest of a buffer that includes the field name.
        /// </summary>
        [Fact]
        public void CanonicalV1_DescribesRecordState_NotRequestMessageIdMatch()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<record-only@example.test>",
                newsgroups: "alt.test",
                from: "user@example.test",
                subject: "canonical",
                references: null,
                body: "body\r\n");

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;

            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(ArticleId.FromMessageId(record.MessageId), record.ArtId);
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
            Assert.True(record.Fields.MessageId.IsPresent);
            Assert.True(record.MessageId.SequenceEqual("<record-only@example.test>"u8));
            Assert.NotEqual(ArticleId.FromMessageId("Message-ID: <record-only@example.test>"u8), record.ArtId);
        }

        /// <summary>
        /// Canonical materialization changes the byte length from the destuffed input, and the record's Message-ID range lies inside ArtData and matches the Message-ID value.
        /// </summary>
        [Fact]
        public void MaterializedRanges_AreNotSourceOffsets()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(
                path: "p",
                date: "Fri, 23 Aug 2024 07:30:10 +0200",
                messageId: "<offset@example.test>",
                newsgroups: "alt.test",
                from: "user@example.test",
                subject: "shifted",
                references: "<r@example.test>",
                body: "x\r\n");

            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(materialize.IsAccepted);
            Assert.NotNull(materialize.ArticleBytes);
            Assert.NotEqual(destuffed.Length, materialize.ArticleBytes.Length);

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            Assert.Equal(materialize.ArticleBytes.Length, record.ArtSize);
            Assert.True(record.ArtData.Span.SequenceEqual(materialize.ArticleBytes));
            Assert.True(record.Fields.MessageId.Offset + record.Fields.MessageId.Length <= record.ArtSize);
            Assert.True(record.MessageId.SequenceEqual("<offset@example.test>"u8));
        }

        /// <summary>
        /// Returns the value offset of the first header whose known name is <paramref name="name"/>.
        /// </summary>
        /// <param name="parse">Accepted parse whose headers are scanned.</param>
        /// <param name="name">Header name to find.</param>
        /// <returns>The header value offset in the destuffed article.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="name"/> is not among the parsed headers.</exception>
        private static int FindHeaderValueOffset(NntpArticleParseResult parse, NntpArticleHeaderName name)
        {
            for (var i = 0; i < parse.HeaderCount; i++)
            {
                var header = parse.GetHeader(i);
                if (header.KnownName == name)
                {
                    return header.ValueOffset;
                }
            }

            throw new InvalidOperationException($"Missing {name}");
        }

        /// <summary>
        /// Builds an ASCII article. A null <paramref name="references"/> omits the References header.
        /// </summary>
        /// <param name="path">Path header value.</param>
        /// <param name="date">Date header value.</param>
        /// <param name="messageId">Message-ID header value.</param>
        /// <param name="newsgroups">Newsgroups header value.</param>
        /// <param name="from">From header value.</param>
        /// <param name="subject">Subject header value.</param>
        /// <param name="references">References header value, or null to omit the header.</param>
        /// <param name="body">Body text appended after the blank line.</param>
        /// <returns>ASCII bytes of the destuffed article.</returns>
        private static byte[] BuildDestuffed(
            string path,
            string date,
            string messageId,
            string newsgroups,
            string from,
            string subject,
            string? references,
            string body)
        {
            var builder = new StringBuilder();
            _ = builder.Append("Path: ").Append(path).Append("\r\n");
            _ = builder.Append("Date: ").Append(date).Append("\r\n");
            _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
            _ = builder.Append("Newsgroups: ").Append(newsgroups).Append("\r\n");
            _ = builder.Append("From: ").Append(from).Append("\r\n");
            _ = builder.Append("Subject: ").Append(subject).Append("\r\n");
            if (references is not null)
            {
                _ = builder.Append("References: ").Append(references).Append("\r\n");
            }

            _ = builder.Append("\r\n").Append(body);
            return Encoding.ASCII.GetBytes(builder.ToString());
        }
    }
}
