using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Lowercase hex encoding of <see cref="ArticleId"/>: round-trip length and rejection of invalid text.
    /// </summary>
    public sealed class ArticleIdHexTests
    {
        /// <summary>
        /// <see cref="ArticleId.ToLowerHexString"/> emits <see cref="ArticleId.HexLength"/> lowercase hex characters
        /// that <see cref="ArticleId.TryParseLowerHex"/> parses back to the same identifier.
        /// </summary>
        [Fact]
        public void ToLowerHexString_and_TryParseLowerHex_round_trip()
        {
            var id = ArticleId.FromMessageId("<hex-roundtrip@example.test>"u8);
            var hex = id.ToLowerHexString();
            Assert.Equal(ArticleId.HexLength, hex.Length);
            Assert.Equal(hex, hex.ToLowerInvariant());
            Assert.True(ArticleId.TryParseLowerHex(hex, out var parsed));
            Assert.Equal(id, parsed);
        }

        /// <summary>
        /// <see cref="ArticleId.TryParseLowerHex"/> returns false for text that is the wrong length or contains non-hex characters.
        /// </summary>
        /// <param name="hex">Candidate lowercase-hex text that must be rejected.</param>
        [Theory]
        [InlineData("ABC")]
        [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
        public void TryParseLowerHex_rejects_invalid(string hex)
        {
            Assert.False(ArticleId.TryParseLowerHex(hex, out _));
        }
    }
}
