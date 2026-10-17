using VectorNNTPD.Common.Articles.DateParser;

namespace VectorNNTPD.Common.Tests.Articles.DateParser
{
    /// <summary>
    /// <see cref="PrintableAsciiSimd.IsAllPrintableAscii(ReadOnlySpan{char})"/> and the byte overload accept only bytes and chars in the printable ASCII range.
    /// </summary>
    public sealed class PrintableAsciiSimdTests
    {
        /// <summary>
        /// Empty char and byte spans are treated as entirely printable ASCII.
        /// </summary>
        [Fact]
        public void Empty_spans_are_printable()
        {
            Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(ReadOnlySpan<char>.Empty));
            Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(ReadOnlySpan<byte>.Empty));
        }

        /// <summary>
        /// A string longer than a single SIMD vector, composed of printable ASCII including space and tilde, is accepted by both overloads.
        /// </summary>
        [Fact]
        public void Printable_ascii_is_accepted_across_vector_widths()
        {
            var text = new string('A', 40) + " ~";
            Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(text.AsSpan()));
            Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(System.Text.Encoding.ASCII.GetBytes(text)));
        }

        /// <summary>
        /// Newline, DEL (<c>0x7F</c>), NUL, and <c>0xFF</c> cause the span to be rejected.
        /// </summary>
        [Fact]
        public void Control_and_high_bytes_are_rejected()
        {
            Assert.False(PrintableAsciiSimd.IsAllPrintableAscii("\nDate".AsSpan()));
            Assert.False(PrintableAsciiSimd.IsAllPrintableAscii("Date\x7F".AsSpan()));
            Assert.False(PrintableAsciiSimd.IsAllPrintableAscii([(byte)'A', 0x00]));
            Assert.False(PrintableAsciiSimd.IsAllPrintableAscii([(byte)'A', 0xFF]));
        }

        /// <summary>
        /// The char and byte overloads return the same result for an ASCII RFC 5322 date.
        /// </summary>
        [Fact]
        public void Byte_and_char_overloads_agree_on_ascii()
        {
            const string value = "Sat, 26 Sep 2026 12:00:00 +0000";
            Assert.Equal(
                PrintableAsciiSimd.IsAllPrintableAscii(value.AsSpan()),
                PrintableAsciiSimd.IsAllPrintableAscii(System.Text.Encoding.ASCII.GetBytes(value)));
        }
    }
}
