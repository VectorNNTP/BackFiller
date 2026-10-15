using VectorNNTP.BackFiller.Articles;
using VectorNNTP.BackFiller.Articles.Parsing;
using VectorNNTP.BackFiller.Articles.Processing;

namespace VectorNNTP.BackFiller.Tests.Articles
{
    /// <summary>
    /// Article-size ceiling for the pre-stuff projection and the stuffed wire image.
    /// </summary>
    public sealed class NntpArticleCanonicalMaterializerSizeTests
    {
        [Fact]
        public void Plain_article_below_the_limit_keeps_its_stuffed_length()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(0, measured.ExtraDots);
            Assert.Equal(measured.WireLength, measured.PreStuffLength);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength + 64, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(1, pool.RentCalls);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        [Fact]
        public void Plain_article_exactly_at_the_limit_is_accepted()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(0, measured.ExtraDots);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        [Fact]
        public void Plain_article_one_byte_over_the_limit_is_rejected_before_rent()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.WireLength - 1, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, rejected.CanonicalLength);
            Assert.Equal(0, pool.RentCalls);
        }

        [Fact]
        public void Stuffing_dots_that_cross_the_limit_are_rejected_before_rent()
        {
            var article = Article(DotLines(8));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(8, measured.ExtraDots);
            Assert.True(measured.PreStuffLength < measured.WireLength);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.PreStuffLength, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, pool.RentCalls);
        }

        [Fact]
        public void Dot_heavy_article_exactly_at_the_stuffed_limit_is_accepted()
        {
            var article = Article(DotLines(32));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(32, measured.ExtraDots);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(measured.PreStuffLength + measured.ExtraDots, accepted.CanonicalLength);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        [Fact]
        public void Dot_heavy_article_one_byte_over_the_stuffed_limit_is_rejected_before_rent()
        {
            var article = Article(DotLines(32));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.True(measured.PreStuffLength <= measured.WireLength - 1);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.WireLength - 1, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, pool.RentCalls);
        }

        [Fact]
        public void Empty_and_ordinary_articles_keep_their_existing_results()
        {
            var emptyParsed = Parse([], ArticleResourceLimits.MaxArticleBytes);
            Assert.False(emptyParsed.IsAccepted);
            var emptyPool = new RecordingArrayPool();
            var empty = Materialize(in emptyParsed, ArticleResourceLimits.MaxArticleBytes, emptyPool);
            Assert.Equal(NntpArticleCanonicalFailureCode.ParseNotAccepted, empty.FailureCode);
            Assert.Null(empty.ArticleBytes);
            Assert.Equal(0, emptyPool.RentCalls);

            var ordinary = Article("Hello\r\n");
            var parsed = new NntpArticleParser("backfiller.test").Parse(ordinary);
            Assert.True(parsed.IsAccepted, parsed.FailureCode.ToString());
            var materialized = NntpArticleCanonicalMaterializer.Materialize(in parsed, ArticlePathMode.Normalize);
            Assert.True(materialized.IsAccepted, materialized.FailureCode.ToString());
            Assert.NotNull(materialized.ArticleBytes);
            Assert.Equal(materialized.CanonicalLength, materialized.ArticleBytes.Length);
            Assert.InRange(materialized.CanonicalLength, 1, ArticleResourceLimits.MaxArticleBytes);
        }

        private static MeasuredArticle Measure(in NntpArticleParseResult parsed)
        {
            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, 1024 * 1024, pool);
            Assert.True(accepted.IsAccepted, accepted.FailureCode.ToString());
            var extra = ArticleBodyWireStuffer.CountExtraDots(parsed.ArticleBytes.Span[parsed.HeaderBytes.Length..]);
            Assert.Equal(1, pool.RentCalls);
            Assert.Equal(accepted.CanonicalLength, pool.LastMinimumLength);
            return new MeasuredArticle(accepted.CanonicalLength, accepted.CanonicalLength - extra, extra);
        }

        private static NntpArticleParseResult Parse(byte[] article, int maxArticleBytes)
        {
            var parsed = new NntpArticleParser("backfiller.test").Parse(
                article,
                maxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost);
            Assert.True(parsed.IsAccepted || article.Length == 0, parsed.FailureCode.ToString());
            return parsed;
        }

        private static NntpArticleCanonicalMaterializeResult Materialize(
            in NntpArticleParseResult parsed,
            int maxArticleBytes,
            ArrayPool<byte> pool) =>
            NntpArticleCanonicalMaterializer.Materialize(
                in parsed,
                ArticlePathMode.Normalize,
                maxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                pool);

        private static byte[] Article(string body)
        {
            var text =
                "Path: news.example.com!not-for-mail\r\n" +
                "Newsgroups: example.test\r\n" +
                "Subject: size boundary\r\n" +
                "From: user@example.com\r\n" +
                "Date: Sat, 06 Mar 2004 21:39:44 -0800\r\n" +
                "Message-ID: <size-boundary@example.com>\r\n" +
                "\r\n" +
                body;
            return Encoding.ASCII.GetBytes(text);
        }

        private static string DotLines(int count) => string.Concat(Enumerable.Repeat(".\r\n", count));

        private readonly record struct MeasuredArticle(int WireLength, int PreStuffLength, int ExtraDots);

        private sealed class RecordingArrayPool : ArrayPool<byte>
        {
            public int RentCalls { get; private set; }

            public int LastMinimumLength { get; private set; }

            public override byte[] Rent(int minimumLength)
            {
                RentCalls++;
                LastMinimumLength = minimumLength;
                return GC.AllocateUninitializedArray<byte>(minimumLength);
            }

            public override void Return(byte[] array, bool clearArray = false)
            {
            }
        }
    }
}
