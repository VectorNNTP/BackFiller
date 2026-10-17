using System.Text;
using VectorNNTPD.Common.Articles.DateParser;

namespace VectorNNTPD.Common.Tests.Articles.DateParser
{
    /// <summary>
    /// <see cref="NewsDateParser.TryGetCanonicalUtc(ReadOnlySpan{byte}, out DateTime, out DateParseFailureReason)"/>
    /// accepts numeric offsets, GMT, and trailing comments, and reports empty, non-printable, unknown-zone, and too-long failures.
    /// </summary>
    public sealed class NewsDateParserTests
    {
        /// <summary>
        /// A date with numeric offset <c>+0200</c> is accepted with <see cref="DateParseFailureReason.None"/>
        /// and converted to 05:30:10 UTC, which formats as <c>Fri, 23 Aug 2024 05:30:10 +0000</c>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenNumericOffsetPresent_ReturnsUtcWithoutFallback()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 07:30:10 +0200"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Format(utc));
        }

        /// <summary>
        /// The <c>GMT</c> abbreviation is accepted as a zero offset and yields the same UTC instant as the clock time in the input.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenGmtAbbreviationPresent_SubstitutesOffset()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 GMT"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// A trailing parenthetical comment such as <c>(UTC)</c> is ignored and does not change the parsed UTC instant.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenTrailingCommentPresent_StripsParenthetical()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 +0000 (UTC)"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        /// <summary>
        /// Whitespace-only input is rejected with <see cref="DateParseFailureReason.Empty"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenEmpty_ReturnsEmptyFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("   "u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.Empty, failure);
        }

        /// <summary>
        /// An embedded NUL is rejected with <see cref="DateParseFailureReason.NonPrintableAscii"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenNonPrintable_ReturnsNonPrintableFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("Fri, 23 Aug 2024 05:30:10 +\0"u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.NonPrintableAscii, failure);
        }

        /// <summary>
        /// With <see cref="DateParseOptions.RequireKnownTimezoneAbbreviation"/> set, an unrecognized zone abbreviation is rejected
        /// with <see cref="DateParseFailureReason.UnknownTimezoneAbbreviation"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenUnknownAbbreviationRequired_Rejects()
        {
            var options = DateParseOptions.Default with { RequireKnownTimezoneAbbreviation = true };
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 ZZQX"u8,
                options,
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.UnknownTimezoneAbbreviation, failure);
        }

        /// <summary>
        /// Input one byte longer than <see cref="DateParseOptions.MaxInputLength"/> is rejected with <see cref="DateParseFailureReason.TooLong"/>.
        /// </summary>
        [Fact]
        public void TryGetCanonicalUtc_WhenTooLong_Rejects()
        {
            var padded = new byte[DateParseOptions.Default.MaxInputLength + 1];
            padded.AsSpan().Fill((byte)'A');
            var accepted = NewsDateParser.TryGetCanonicalUtc(padded, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.TooLong, failure);
        }

        /// <summary>
        /// Formats <paramref name="utc"/> with <see cref="NewsDateParser.TryFormatCanonicalRfc5322Utc"/> and returns the ASCII text.
        /// </summary>
        /// <param name="utc">UTC instant to format. The test asserts formatting succeeds.</param>
        /// <returns>The canonical RFC 5322 UTC date text written by the parser.</returns>
        private static string Format(DateTime utc)
        {
            Span<byte> destination = stackalloc byte[40];
            Assert.True(NewsDateParser.TryFormatCanonicalRfc5322Utc(utc, destination, out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }
    }
}
