using VectorNNTPD.Common.Articles.Checksum;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles.Checksum
{
    /// <summary>
    /// <see cref="IeeeCrc32"/> uses the IEEE reflected polynomial, matches the standard check vector, and agrees with <see cref="YEncCrc32"/>.
    /// </summary>
    public sealed class IeeeCrc32Tests
    {
        /// <summary>
        /// <see cref="IeeeCrc32.Polynomial"/> is the IEEE reflected polynomial <c>0xEDB88320</c>, not the Castagnoli polynomial <c>0x82F63B78</c>.
        /// </summary>
        [Fact]
        public void Polynomial_is_ieee_crc32_not_castagnoli()
        {
            Assert.Equal(0xEDB88320u, IeeeCrc32.Polynomial);
            Assert.NotEqual(0x82F63B78u, IeeeCrc32.Polynomial);
        }

        /// <summary>
        /// <see cref="IeeeCrc32.Compute(ReadOnlySpan{byte})"/> of ASCII <c>123456789</c> equals the IEEE check value <c>0xCBF43926</c>.
        /// </summary>
        [Fact]
        public void Compute_matches_the_standard_ieee_vector()
        {
            Assert.Equal(0xCBF43926u, IeeeCrc32.Compute("123456789"u8));
        }

        /// <summary>
        /// The IEEE check vector does not equal the CRC-32C result <c>0xE3069283</c> for the same ASCII input.
        /// </summary>
        [Fact]
        public void Compute_is_not_crc32c()
        {
            Assert.NotEqual(0xE3069283u, IeeeCrc32.Compute("123456789"u8));
        }

        /// <summary>
        /// Changing one byte of the input changes the CRC.
        /// </summary>
        [Fact]
        public void Changed_byte_changes_crc()
        {
            var original = IeeeCrc32.Compute("article-bytes"u8);
            var changed = IeeeCrc32.Compute("article-byteS"u8);
            Assert.NotEqual(original, changed);
        }

        /// <summary>
        /// Identical input bytes produce identical CRC values.
        /// </summary>
        [Fact]
        public void Same_bytes_produce_same_crc()
        {
            var first = IeeeCrc32.Compute("canonical"u8);
            var second = IeeeCrc32.Compute("canonical"u8);
            Assert.Equal(first, second);
        }

        /// <summary>
        /// <see cref="YEncCrc32.Compute(ReadOnlySpan{byte})"/> and <see cref="YEncCrc32.Polynomial"/> match <see cref="IeeeCrc32"/> on the standard check vector.
        /// </summary>
        [Fact]
        public void YEncCrc32_still_matches_ieee_primitive()
        {
            Assert.Equal(IeeeCrc32.Compute("123456789"u8), YEncCrc32.Compute("123456789"u8));
            Assert.Equal(IeeeCrc32.Polynomial, YEncCrc32.Polynomial);
        }
    }
}
