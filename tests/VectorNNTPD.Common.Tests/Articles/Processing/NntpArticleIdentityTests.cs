using VectorNNTPD.Common.Articles.Processing;

namespace VectorNNTPD.Common.Tests.Articles.Processing
{
    /// <summary>
    /// Asserts that message-id bytes match a requested character sequence only when the length and octets are equal.
    /// </summary>
    public sealed class NntpArticleIdentityTests
    {
        /// <summary>
        /// Equal length and equal octets, including the angle brackets, match.
        /// </summary>
        [Fact]
        public void MatchesRequest_WhenBytesEqualRequestedChars_ReturnsTrue()
        {
            Assert.True(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<abc@example.test>"));
        }

        /// <summary>
        /// A requested value that is longer than the byte sequence does not match.
        /// </summary>
        [Fact]
        public void MatchesRequest_WhenLengthDiffers_ReturnsFalse()
        {
            Assert.False(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<abc@example.test>.extra"));
        }

        /// <summary>
        /// A single ASCII case difference fails the match, so comparison is not case-insensitive.
        /// </summary>
        [Fact]
        public void MatchesRequest_WhenOneByteDiffers_ReturnsFalse()
        {
            Assert.False(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<Abc@example.test>"));
        }
    }
}
