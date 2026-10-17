using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Locks the hard article size and line-length ceilings exposed by <see cref="ArticleResourceLimits"/>.
    /// </summary>
    public sealed class ArticleResourceLimitsTests
    {
        /// <summary>
        /// Maximum article size is 5 MiB, maximum line length is 1024 characters, and the byte and character line limits are equal.
        /// </summary>
        [Fact]
        public void Hard_limits_match_the_old_article_contract()
        {
            Assert.Equal(5 * 1024 * 1024, ArticleResourceLimits.MaxArticleBytes);
            Assert.Equal(1024, ArticleResourceLimits.MaxArticleLineCharacters);
            Assert.Equal(ArticleResourceLimits.MaxArticleLineCharacters, ArticleResourceLimits.MaxArticleLineBytes);
        }
    }
}
