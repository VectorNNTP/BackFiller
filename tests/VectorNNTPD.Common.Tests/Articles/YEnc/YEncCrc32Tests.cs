using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles.YEnc
{
    /// <summary>
    /// Asserts that <see cref="YEncCrc32"/> uses the IEEE polynomial, check value, incremental update, and empty-input result.
    /// </summary>
    public sealed class YEncCrc32Tests
    {
        /// <summary>
        /// The reflected polynomial is the IEEE value <c>0xEDB88320</c> and not the Castagnoli value <c>0x82F63B78</c>.
        /// </summary>
        [Fact]
        public void Polynomial_is_ieee_crc32_not_castagnoli()
        {
            Assert.Equal(0xEDB88320u, YEncCrc32.Polynomial);
            Assert.NotEqual(0x82F63B78u, YEncCrc32.Polynomial);
        }

        /// <summary>
        /// <c>123456789</c> hashes to the IEEE check value <c>0xCBF43926</c>.
        /// </summary>
        [Fact]
        public void Compute_matches_the_standard_ieee_vector()
        {
            Assert.Equal(0xCBF43926u, YEncCrc32.Compute("123456789"u8));
        }

        /// <summary>
        /// Updating from the initial accumulator across <c>abc</c> and <c>def</c>, then finalizing, matches one compute of <c>abcdef</c>.
        /// </summary>
        [Fact]
        public void Update_then_finalize_matches_compute()
        {
            var first = "abc"u8;
            var second = "def"u8;
            var crc = YEncCrc32.Update(YEncCrc32.InitialAccumulator, first);
            crc = YEncCrc32.Update(crc, second);
            Assert.Equal(YEncCrc32.Compute("abcdef"u8), YEncCrc32.Finalize(crc));
        }

        /// <summary>
        /// An empty buffer computes as CRC zero.
        /// </summary>
        [Fact]
        public void Empty_input_has_the_ieee_empty_crc()
        {
            Assert.Equal(0u, YEncCrc32.Compute([]));
        }
    }
}
