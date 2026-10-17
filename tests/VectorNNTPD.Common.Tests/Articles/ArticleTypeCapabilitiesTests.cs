using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// <see cref="ArticleTypeCapabilities.All"/> is the bitwise union of every non-<see cref="ArticleType.None"/> flag in bits 0 through 15.
    /// </summary>
    public sealed class ArticleTypeCapabilitiesTests
    {
        /// <summary>
        /// Every defined <see cref="ArticleType"/> flag except <see cref="ArticleType.None"/> uses a bit at or below bit 15,
        /// their union equals 65535 and <see cref="ArticleTypeCapabilities.AllValue"/>, and masking off those bits yields <see cref="ArticleType.None"/>.
        /// </summary>
        [Fact]
        public void All_Equals65535_AndUsesOnlyBits0Through15()
        {
            uint combined = 0;
            foreach (ArticleType value in Enum.GetValues<ArticleType>())
            {
                if (value == ArticleType.None)
                {
                    continue;
                }

                var bits = (uint)value;
                Assert.NotEqual(0u, bits);
                Assert.True(bits <= 1u << 15, value + " uses a bit above 15.");
                combined |= bits;
            }

            Assert.Equal(65535u, combined);
            Assert.Equal(ArticleTypeCapabilities.AllValue, combined);
            Assert.Equal(ArticleTypeCapabilities.All, (ArticleType)combined);
            Assert.Equal(ArticleType.None, ArticleTypeCapabilities.All & ~((ArticleType)65535));
        }
    }
}
