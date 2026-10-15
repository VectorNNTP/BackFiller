namespace VectorNNTP.BackFiller.Articles
{
    /// <summary>
    /// Hard protocol and resource boundaries for NNTP article processing.
    /// These are safety limits, not runtime configuration settings.
    /// </summary>
    /// <remarks>
    /// The line boundary is 1024 characters by contract and is enforced on ASCII
    /// wire bytes because article framing is byte-oriented.
    /// </remarks>
    internal static class ArticleResourceLimits
    {
        /// <summary>
        /// StorageServer and VATP article-size ceiling, and the parser default when the caller does not supply
        /// <c>nntpsharedconfig.maxartsize</c>. NNTPD and BackFiller pass that database value into parse and materialize.
        /// </summary>
        /// <remarks>
        /// The same number bounds different representations. The parser applies it to the destuffed article.
        /// The materializer applies it to the canonical projection after Date and Path edits and before body dot-stuffing,
        /// then again to the stuffed wire image that becomes canonical ArtData and VATP <c>ArtSize</c>.
        /// Stuffing dots count only toward that stuffed length. The NNTP multiline terminator is not part of either length.
        /// </remarks>
        public const int MaxArticleBytes = 5 * 1024 * 1024;

        /// <summary>Maximum accepted ARTICLE/header line length in characters.</summary>
        public const int MaxArticleLineCharacters = 1024;

        /// <summary>Maximum accepted ARTICLE/header line length in wire bytes.</summary>
        public const int MaxArticleLineBytes = MaxArticleLineCharacters;
    }
}
