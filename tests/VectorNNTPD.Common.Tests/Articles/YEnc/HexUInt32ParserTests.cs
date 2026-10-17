using System.Text;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles.YEnc
{
    /// <summary>
    /// Asserts strict hexadecimal parsing of one to eight digits and rejection of every other shape.
    /// </summary>
    public sealed class HexUInt32ParserTests
    {
        /// <summary>
        /// One to eight ASCII hex digits, in either case, parse to <paramref name="expected"/>.
        /// </summary>
        /// <param name="input">Strict hexadecimal text.</param>
        /// <param name="expected">Value required from the parser.</param>
        [Theory]
        [InlineData("1", 0x1u)]
        [InlineData("ABCDEF12", 0xABCDEF12u)]
        [InlineData("abcdef12", 0xABCDEF12u)]
        [InlineData("00000000", 0x00000000u)]
        public void TryParseHexUInt32_WhenInputIsStrictHex_ReturnsExpectedValue(string input, uint expected)
        {
            var parsed = HexUInt32Parser.TryParseHexUInt32(Encoding.ASCII.GetBytes(input), out var value);

            Assert.True(parsed);
            Assert.Equal(expected, value);
        }

        /// <summary>
        /// Empty input, more than eight digits, non-hex characters, signs, spaces, underscores, and fullwidth digits fail and leave the value at zero.
        /// </summary>
        /// <param name="input">Text that is not one to eight strict hex digits.</param>
        [Theory]
        [InlineData("")]
        [InlineData("123456789")]
        [InlineData("ABCDEF12G")]
        [InlineData("12345678XYZ")]
        [InlineData("-1")]
        [InlineData("+")]
        [InlineData("G")]
        [InlineData(" ")]
        [InlineData("12 34")]
        [InlineData("12_34")]
        [InlineData("１２")]
        public void TryParseHexUInt32_WhenInputContainsGarbageOrIsOutOfBounds_ReturnsFalse(string input)
        {
            var parsed = HexUInt32Parser.TryParseHexUInt32(Encoding.ASCII.GetBytes(input), out var value);

            Assert.False(parsed);
            Assert.Equal(0u, value);
        }
    }
}
