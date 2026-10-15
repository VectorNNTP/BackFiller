using VectorNNTP.BackFiller.Articles.Parsing;
using VectorNNTP.BackFiller.Articles.YEnc;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Articles
{
    /// <summary>
    /// yEnc validation of the SABCTools fixtures after NNTP destuffing.
    /// </summary>
    public sealed class SabctoolsYEncArticleValidationTests
    {
        public static IEnumerable<object[]> ExpectedStatuses()
        {
            yield return ["test_regular.yenc", (int)YEncArticleValidationStatus.ValidMultiPart];
            yield return ["test_regular_2.yenc", (int)YEncArticleValidationStatus.ValidMultiPart];
            yield return ["test_partial.yenc", (int)YEncArticleValidationStatus.DecodedSizeMismatch];
            yield return ["test_special_chars.yenc", (int)YEncArticleValidationStatus.ValidMultiPart];
            yield return ["test_special_utf8_chars.yenc", (int)YEncArticleValidationStatus.ValidMultiPart];
            yield return ["test_bad_crc.yenc", (int)YEncArticleValidationStatus.ValidMultiPart];
            yield return ["test_bad_crc_end.yenc", (int)YEncArticleValidationStatus.CrcMismatch];
            yield return ["test_invalid_crc_chars.yenc", (int)YEncArticleValidationStatus.InvalidMetadata];
            yield return ["test_invalid_escape.yenc", (int)YEncArticleValidationStatus.InvalidEscapeSequence];
            yield return ["test_malformed_ybegin.yenc", (int)YEncArticleValidationStatus.InvalidMetadata];
            yield return ["test_missing_yend.yenc", (int)YEncArticleValidationStatus.Truncated];
            yield return ["test_ypart_without_ybegin.yenc", (int)YEncArticleValidationStatus.ValidNonYEnc];
        }

        [Theory]
        [MemberData(nameof(ExpectedStatuses))]
        public async Task Destuffed_body_matches_the_validator_status(string name, int expectedStatus)
        {
            var file = File.ReadAllBytes(Fixture(name));
            var statusLine = file.AsSpan().IndexOf("\r\n"u8);
            Assert.True(statusLine > 3);
            var payload = file[(statusLine + 2)..];
            var article = await ReadPayloadAsync(payload);
            var parsed = new NntpArticleParser("backfiller.test").Parse(article);
            Assert.True(parsed.BodyBytes.Length > 0, name);

            var result = YEncArticleValidator.Validate(parsed.BodyBytes.Span);
            Assert.Equal((YEncArticleValidationStatus)expectedStatus, result.Status);
            if (parsed.YEncDetected)
            {
                Assert.Equal(result.Status, parsed.YEncValidation.Status);
            }

            var framed = FrameLogicalArticle(article);
            var roundTrip = await ReadPayloadAsync(framed);
            Assert.Equal(article, roundTrip);
        }

        [Fact]
        public async Task Stuffed_yenc_payload_is_not_validated_as_the_destuffed_article()
        {
            var file = File.ReadAllBytes(Fixture("test_regular.yenc"));
            var statusLine = file.AsSpan().IndexOf("\r\n"u8);
            var payload = file[(statusLine + 2)..];
            Assert.True(payload.AsSpan().EndsWith(".\r\n"u8));
            var stuffedArticle = payload[..^3];
            var destuffed = await ReadPayloadAsync(payload);

            var stuffed = YEncArticleValidator.Validate(stuffedArticle);
            var logical = YEncArticleValidator.Validate(
                new NntpArticleParser("backfiller.test").Parse(destuffed).BodyBytes.Span);

            Assert.Equal(YEncArticleValidationStatus.ValidMultiPart, logical.Status);
            Assert.NotEqual(logical.Status, stuffed.Status);
            Assert.False(stuffed.IsValid);
        }

        [Fact]
        public void Empty_file_fixture_is_not_an_nntp_article()
        {
            var file = File.ReadAllBytes(Fixture("test_empty_file.yenc"));
            Assert.Equal(new byte[] { 0xFF, 0xFE, 0x0D, 0x00, 0x0A, 0x00 }, file);
            var result = YEncArticleValidator.Validate(file);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.Status);
            Assert.Equal(0, result.SectionsValidated);
        }

        private static async Task<byte[]> ReadPayloadAsync(byte[] framed)
        {
            var stream = new MemoryStream(framed, writable: false);
            var reader = new NntpStreamReader(stream, 4096);
            return await reader.ReadArticlePayloadAsync(
                VectorNNTP.BackFiller.Articles.ArticleResourceLimits.MaxArticleBytes,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
        }

        private static byte[] FrameLogicalArticle(ReadOnlySpan<byte> logical)
        {
            var extra = 0;
            var index = 0;
            while (index < logical.Length)
            {
                if (logical[index] == (byte)'.')
                {
                    extra++;
                }

                var newline = logical[index..].IndexOf((byte)'\n');
                index = newline < 0 ? logical.Length : index + newline + 1;
            }

            var framed = new byte[logical.Length + extra + 3];
            var written = 0;
            index = 0;
            while (index < logical.Length)
            {
                if (logical[index] == (byte)'.')
                {
                    framed[written++] = (byte)'.';
                }

                var newline = logical[index..].IndexOf((byte)'\n');
                var length = newline < 0 ? logical.Length - index : newline + 1;
                logical.Slice(index, length).CopyTo(framed.AsSpan(written));
                written += length;
                index += length;
            }

            framed[written++] = (byte)'.';
            framed[written++] = (byte)'\r';
            framed[written] = (byte)'\n';
            return framed;
        }

        private static string Fixture(string name)
            => RepositoryFiles.Under("tests", "fixtures", "SabctoolsYEnc", name);
    }
}
