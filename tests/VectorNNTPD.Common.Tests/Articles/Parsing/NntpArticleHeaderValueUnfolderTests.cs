using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.Common.Tests.Articles.Parsing
{
    /// <summary>
    /// Asserts <see cref="NntpArticleHeaderValueUnfolder.TryUnfold"/> fold replacement, rejection, and exact copy.
    /// </summary>
    public sealed class NntpArticleHeaderValueUnfolderTests
    {
        /// <summary>
        /// A CRLF fold followed by a space is replaced by one space and the surrounding header text is preserved.
        /// </summary>
        [Fact]
        public void Unfold_replaces_crlf_fold_with_a_single_space()
        {
            ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n 2026 12:00:00 +0000"u8;
            Span<byte> destination = stackalloc byte[64];

            Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
            Assert.True("Sat, 26 Sep 2026 12:00:00 +0000"u8.SequenceEqual(destination[..written]));
        }

        /// <summary>
        /// A fold that uses a bare CR or a bare LF, followed by a space, unfolds to the same single-space value as CRLF.
        /// </summary>
        /// <param name="rawText">Folded header value using a one-character line break.</param>
        /// <param name="expected">Unfolded ASCII text required in the destination.</param>
        [Theory]
        [InlineData("Sat, 26 Sep\r 2026", "Sat, 26 Sep 2026")]
        [InlineData("Sat, 26 Sep\n 2026", "Sat, 26 Sep 2026")]
        public void Unfold_accepts_single_character_fold_terminators(string rawText, string expected)
        {
            var raw = System.Text.Encoding.ASCII.GetBytes(rawText);
            Span<byte> destination = stackalloc byte[64];

            Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
            Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(destination[..written]));
        }

        /// <summary>
        /// A CRLF that is not followed by whitespace is not a continuation, so unfolding fails.
        /// </summary>
        [Fact]
        public void Unfold_rejects_a_line_break_that_is_not_a_continuation()
        {
            ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n2026"u8;
            Span<byte> destination = stackalloc byte[64];
            Assert.False(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out _));
        }

        /// <summary>
        /// Unfolding fails when the destination cannot hold the unfolded value.
        /// </summary>
        [Fact]
        public void Unfold_rejects_when_destination_is_too_small()
        {
            ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n 2026"u8;
            Span<byte> destination = stackalloc byte[4];
            Assert.False(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out _));
        }

        /// <summary>
        /// A value with no fold is copied into the destination unchanged.
        /// </summary>
        [Fact]
        public void Unfold_copies_an_unfolded_value_exactly()
        {
            ReadOnlySpan<byte> raw = "plain-value"u8;
            Span<byte> destination = stackalloc byte[16];
            Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
            Assert.True(raw.SequenceEqual(destination[..written]));
        }
    }
}
