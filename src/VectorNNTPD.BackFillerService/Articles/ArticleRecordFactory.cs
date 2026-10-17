using System.IO.Hashing;
using VectorNNTPD.BackFillerService.Articles.Parsing;
using VectorNNTPD.BackFillerService.Articles.Processing;

namespace VectorNNTPD.BackFillerService.Articles
{
    /// <summary>
    /// Builds an <see cref="ArticleRecord"/> from destuffed article bytes.
    /// </summary>
    /// <remarks>
    /// Uses the existing Common parser, Diablo <see cref="ArticleTypeClassifier"/>, and
    /// Date/Path materializer. ArtHash is one-shot XXH3-64 of the completed
    /// canonical ArtData (<c>XxHash3.HashToUInt64</c>), not an incremental hash
    /// and not IEEE CRC-32.
    /// The acquisition scratch is not retained. Date and Path materialization writes the retained
    /// buffer directly: canonical headers and separator, body lines dot-stuffed by
    /// <see cref="ArticleBodyWireStuffer"/> in that same write, and no NNTP terminator. FieldTable ranges are
    /// located on that buffer. Header offsets match the logical article because stuffing
    /// inserts bytes only in the body. <see cref="ArticleRecord.ArtId"/> is still BLAKE3 of
    /// the Message-ID value. <see cref="ArticleRecord.ArtHash"/> is XXH3-64 of the retained
    /// wire bytes.
    /// This factory has no request Message-ID parameter and does not match an IHAVE/ARTICLE
    /// request. Request matching remains an ingestion/orchestration responsibility.
    /// NNTPD TAKETHIS and POST construct the record before queue admission.
    /// </remarks>
    internal static class ArticleRecordFactory
    {
        /// <summary>
        /// Parses and materializes an article, recording a receiving-system Path hop.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle)
            => TryCreate(parser, destuffedArticle, ArticlePathMode.Traverse);

        /// <summary>
        /// Parses, classifies, and materializes destuffed article bytes into a CanonicalV1 record.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN used by <see cref="ArticlePathMode.Traverse"/>.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <param name="pathMode"><see cref="ArticlePathMode.Traverse"/> prepends the parser FQDN. <see cref="ArticlePathMode.Normalize"/> does not.</param>
        /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            ArticlePathMode pathMode)
            => TryCreate(
                parser,
                destuffedArticle,
                pathMode,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost);

        /// <summary>
        /// Parses and materializes an article with one captured size limit and Path tracker.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <param name="pathMode"><see cref="ArticlePathMode.Traverse"/> prepends the parser FQDN.</param>
        /// <param name="maxArticleBytes">Article-size boundary captured for this operation.</param>
        /// <param name="organizationalTrackerHost">Path tracker captured with <paramref name="maxArticleBytes"/>.</param>
        /// <returns>Accepted record, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            ArticlePathMode pathMode,
            int maxArticleBytes,
            ReadOnlySpan<byte> organizationalTrackerHost)
        {
            ArgumentNullException.ThrowIfNull(parser);

            var parse = parser.Parse(destuffedArticle, maxArticleBytes, organizationalTrackerHost);
            if (!parse.IsAccepted)
            {
                return ArticleRecordCreateResult.RejectedParse(parse.FailureCode);
            }

            var materialize = NntpArticleCanonicalMaterializer.Materialize(
                in parse,
                pathMode,
                maxArticleBytes,
                organizationalTrackerHost);
            if (!materialize.IsAccepted || materialize.ArticleBytes is null)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(materialize.FailureCode);
            }

            var artData = materialize.ArticleBytes;
            var fields = ArticleFieldTable.Locate(artData, parse.SelectedDateHeaderName);
            var artId = ArticleId.FromMessageId(fields.MessageId.Slice(artData));
            var artHash = XxHash3.HashToUInt64(artData);
            var artType = ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span);
            var record = new ArticleRecord(
                artId,
                artHash,
                artType,
                parse.BodyLineCount,
                parse.CanonicalUtc,
                ArticleParseStatus.CanonicalV1,
                artData,
                fields);
            return ArticleRecordCreateResult.Accepted(record, parse.SelectedDateHeaderName);
        }


    }

    /// <summary>
    /// Result of one article-record creation attempt.
    /// </summary>
    internal readonly struct ArticleRecordCreateResult
    {
        /// <summary>
        /// Stores the acceptance flag, record, and failure codes exactly as the factory methods pass them.
        /// </summary>
        /// <param name="isAccepted"><see langword="true"/> only for <see cref="Accepted"/>.</param>
        /// <param name="record">Canonical record when accepted; otherwise <see langword="default"/>.</param>
        /// <param name="parseFailure">Parser failure, or <see cref="NntpArticleParseFailureCode.None"/> when parse succeeded.</param>
        /// <param name="materializeFailure">Materializer or transfer failure, or <see cref="NntpArticleCanonicalFailureCode.None"/> when that stage succeeded.</param>
        /// <param name="selectedDateHeaderName">Winning Date-family name, or <see cref="NntpArticleHeaderName.Unknown"/> when not accepted.</param>
        private ArticleRecordCreateResult(
            bool isAccepted,
            ArticleRecord record,
            NntpArticleParseFailureCode parseFailure,
            NntpArticleCanonicalFailureCode materializeFailure,
            NntpArticleHeaderName selectedDateHeaderName)
        {
            IsAccepted = isAccepted;
            Record = record;
            ParseFailure = parseFailure;
            MaterializeFailure = materializeFailure;
            SelectedDateHeaderName = selectedDateHeaderName;
        }

        /// <summary>True when parse and materialization produced a <see cref="ArticleParseStatus.CanonicalV1"/> record.</summary>
        internal bool IsAccepted { get; }

        /// <summary>Canonical record referencing owned ArtData when accepted; otherwise default.</summary>
        internal ArticleRecord Record { get; }

        /// <summary>Parser rejection when <see cref="IsAccepted"/> is false and failure happened before materialization.</summary>
        internal NntpArticleParseFailureCode ParseFailure { get; }

        /// <summary>Materializer rejection when parse succeeded but canonicalization or size limits failed.</summary>
        internal NntpArticleCanonicalFailureCode MaterializeFailure { get; }

        /// <summary>
        /// Gets the Date-family header name selected during parse.
        /// <see cref="NntpArticleHeaderName.Unknown"/> when not accepted.
        /// </summary>
        internal NntpArticleHeaderName SelectedDateHeaderName { get; }

        /// <summary>Creates an accepted result that carries <paramref name="record"/> (no ArtData copy).</summary>
        internal static ArticleRecordCreateResult Accepted(
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName = NntpArticleHeaderName.Unknown)
            => new(true, record, NntpArticleParseFailureCode.None, NntpArticleCanonicalFailureCode.None, selectedDateHeaderName);

        /// <summary>Creates a parse-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedParse(NntpArticleParseFailureCode parseFailure)
            => new(false, default, parseFailure, NntpArticleCanonicalFailureCode.None, NntpArticleHeaderName.Unknown);

        /// <summary>Creates a materialize-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedMaterialize(NntpArticleCanonicalFailureCode materializeFailure)
            => new(false, default, NntpArticleParseFailureCode.None, materializeFailure, NntpArticleHeaderName.Unknown);
    }
}
