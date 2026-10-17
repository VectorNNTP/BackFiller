using System.Text;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.Common.Tests.Articles.Parsing
{
    /// <summary>
    /// Asserts <see cref="ArticlePathCanonicalizer"/> classification and canonical Path bytes
    /// for missing, empty, tracker, and application-hop inputs.
    /// </summary>
    public sealed class ArticlePathCanonicalizerTests
    {
        /// <summary>
        /// Organizational Path token that a canonical write is required to contain exactly once.
        /// </summary>
        private const string Tracker = "news.usenet.ninja";
        /// <summary>
        /// Application hop used when a test does not pass a different local identity.
        /// </summary>
        /// <value>The UTF-8 bytes of <c>bf01.usenet.ninja</c>.</value>
        private static ReadOnlySpan<byte> LocalIdentity => "bf01.usenet.ninja"u8;

        /// <summary>
        /// A path that was not present classifies as <see cref="ArticlePathKind.Missing"/>,
        /// reports no tracker, and does not fail analysis.
        /// </summary>
        [Fact]
        public void TryAnalyze_WhenMissing_ClassifiesMissing()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                [],
                LocalIdentity,
                pathPresent: false,
                out var kind,
                out var containsTracker,
                out var failure));
            Assert.Equal(ArticlePathKind.Missing, kind);
            Assert.False(containsTracker);
            Assert.Equal(NntpArticleParseFailureCode.None, failure);
        }

        /// <summary>
        /// A present path made only of separators and spaces classifies as
        /// <see cref="ArticlePathKind.Empty"/> and does not count a tracker.
        /// </summary>
        [Fact]
        public void TryAnalyze_WhenOnlySeparators_ClassifiesEmpty()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                "!!!  "u8,
                LocalIdentity,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));
            Assert.Equal(ArticlePathKind.Empty, kind);
            Assert.False(containsTracker);
        }

        /// <summary>
        /// A path that needs a local prepend and has no tracker is classified
        /// <see cref="ArticlePathKind.NeedsLocalIdentityPrepend"/>, and a traverse write
        /// emits the tracker, then the local identity, then the existing hops with the trailing separator removed.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenNeedsPrependAndTrackerAbsent_WritesTrackerThenLocalIdentity()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                "foo!bar!"u8,
                LocalIdentity,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));
            Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
            Assert.False(containsTracker);

            Span<byte> destination = stackalloc byte[80];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                "foo!bar!"u8,
                LocalIdentity,
                kind,
                containsTracker,
                ArticlePathMode.Traverse,
                destination,
                out var written));
            Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja!foo!bar"u8));
            Assert.Equal(1, CountToken(destination[..written], "news.usenet.ninja"u8));
        }

        /// <summary>
        /// When the tracker is already the leftmost hop, a traverse write prepends only the local identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenTrackerAlreadyFirst_DoesNotDuplicateTracker()
        {
            WriteAndAssert(
                "news.usenet.ninja!foo!",
                "bf01.usenet.ninja",
                "bf01.usenet.ninja!news.usenet.ninja!foo");
        }

        /// <summary>
        /// A tracker hop later in the path still suppresses a second tracker; the write prepends only the local identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenTrackerOccursLater_DoesNotDuplicateTracker()
        {
            WriteAndAssert(
                "foo!news.usenet.ninja!bar!",
                "bf01.usenet.ninja",
                "bf01.usenet.ninja!foo!news.usenet.ninja!bar");
        }

        /// <summary>
        /// A tracker token that differs only by ASCII case is detected, so the traverse write
        /// prepends the local identity and leaves that original token in place.
        /// </summary>
        [Fact]
        public void TryAnalyze_WhenTrackerIdentityDiffersOnlyByCase_DetectsTracker()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                "NEWS.USENET.NINJA!foo"u8,
                LocalIdentity,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));
            Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
            Assert.True(containsTracker);

            Span<byte> destination = stackalloc byte[80];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                "NEWS.USENET.NINJA!foo"u8,
                LocalIdentity,
                kind,
                containsTracker,
                ArticlePathMode.Traverse,
                destination,
                out var written));
            Assert.True(destination[..written].SequenceEqual("bf01.usenet.ninja!NEWS.USENET.NINJA!foo"u8));
            Assert.Equal(1, CountToken(destination[..written], "NEWS.USENET.NINJA"u8));
        }

        /// <summary>
        /// A hop that merely begins with the tracker text is not a tracker token, so the write
        /// inserts the tracker and the local identity ahead of it.
        /// </summary>
        [Fact]
        public void TryAnalyze_WhenTrackerIsOnlyASubstringOfAToken_DoesNotCountAsTracker()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                "news.usenet.ninja.extra!foo"u8,
                LocalIdentity,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));
            Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
            Assert.False(containsTracker);

            Span<byte> destination = stackalloc byte[96];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                "news.usenet.ninja.extra!foo"u8,
                LocalIdentity,
                kind,
                containsTracker,
                ArticlePathMode.Traverse,
                destination,
                out var written));
            Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja!news.usenet.ninja.extra!foo"u8));
            Assert.Equal(1, CountToken(destination[..written], "news.usenet.ninja"u8));
        }

        /// <summary>
        /// A traverse write prepends the tracker and the local identity even when that identity is already leftmost.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenLocalIdentityIsLeftmost_PrependsAgain()
        {
            WriteAndAssert(
                "bf01.usenet.ninja!news.example.org",
                "bf01.usenet.ninja",
                "news.usenet.ninja!bf01.usenet.ninja!bf01.usenet.ninja!news.example.org");
        }

        /// <summary>
        /// The first application traversal of a peer path inserts the tracker and then that application's identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_FirstBackFillerTraversal_InsertsTrackerThenApplication()
        {
            WriteAndAssert(
                "news.example.org!feed2",
                "backfiller01.usenet.ninja",
                "news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
        }

        /// <summary>
        /// A later storage traversal prepends its identity in front of an existing tracker and does not add another tracker.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_SubsequentStorageTraversal_PrependsApplicationBeforeExistingTracker()
        {
            WriteAndAssert(
                "news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2",
                "storage01.usenet.ninja",
                "storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
        }

        /// <summary>
        /// A later nntpd traversal prepends its identity and leaves exactly one tracker hop.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_SubsequentNntpdTraversal_PrependsApplicationAndKeepsSingleTracker()
        {
            WriteAndAssert(
                "storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2",
                "nntpd01.usenet.ninja",
                "nntpd01.usenet.ninja!storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
            Assert.Equal(1, CountToken(
                "nntpd01.usenet.ninja!storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2"u8,
                "news.usenet.ninja"u8));
        }

        /// <summary>
        /// A missing path is rewritten as the tracker followed by the local identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenMissing_WritesTrackerAndLocalIdentity()
        {
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                [],
                LocalIdentity,
                pathPresent: false,
                out var kind,
                out var containsTracker,
                out _));

            Span<byte> destination = stackalloc byte[64];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                [],
                LocalIdentity,
                kind,
                containsTracker,
                ArticlePathMode.Traverse,
                destination,
                out var written));
            Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja"u8));
        }

        /// <summary>
        /// A local identity that already appears later in the path is still prepended again on traverse.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenLocalIdentityOccursLater_PrependsAgain()
        {
            WriteAndAssert(
                "cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja",
                "nntpd01.usenet.ninja",
                "nntpd01.usenet.ninja!cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja");
        }

        /// <summary>
        /// Normalize with the tracker as the local identity leaves an already tracked path unchanged
        /// and does not prepend another application hop.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_Normalize_WhenTrackerIsPresent_DoesNotPrependApplicationHop()
        {
            WriteAndAssert(
                "cache01.usenet.ninja!news.usenet.ninja!nntpd01.usenet.ninja",
                "news.usenet.ninja",
                "cache01.usenet.ninja!news.usenet.ninja!nntpd01.usenet.ninja",
                ArticlePathMode.Normalize);
        }

        /// <summary>
        /// A leftmost hop that matches the local identity only by case still receives a tracker and a new local hop.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_WhenLeftmostMatchesIgnoringCase_PrependsAgain()
        {
            WriteAndAssert(
                "NNTPD01.USENET.NINJA!cache02.usenet.ninja",
                "nntpd01.usenet.ninja",
                "news.usenet.ninja!nntpd01.usenet.ninja!NNTPD01.USENET.NINJA!cache02.usenet.ninja");
        }

        /// <summary>
        /// Successive traverse rewrites keep the newest application hop first, repeat an application identity,
        /// leave normalize idempotent for the current leftmost hop, and keep a single tracker.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_BackFillerThenNntpdThenCaches_KeepsNewestFirst()
        {
            var path = "peer.example";
            path = Rewrite(path, "backfiller01.usenet.ninja");
            Assert.Equal("news.usenet.ninja!backfiller01.usenet.ninja!peer.example", path);
            path = Rewrite(path, "nntpd01.usenet.ninja");
            path = Rewrite(path, "cache02.usenet.ninja");
            path = Rewrite(path, "nntpd01.usenet.ninja");
            path = Rewrite(path, "cache01.usenet.ninja");
            Assert.Equal(
                "cache01.usenet.ninja!nntpd01.usenet.ninja!cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!peer.example",
                path);
            Assert.Equal(
                "cache01.usenet.ninja!" + path,
                Rewrite(path, "cache01.usenet.ninja"));
            Assert.Equal(path, Rewrite(path, "cache01.usenet.ninja", ArticlePathMode.Normalize));
            Assert.Equal(1, CountToken(Encoding.ASCII.GetBytes(path), "news.usenet.ninja"u8));
        }

        /// <summary>
        /// Normalize inserts one tracker and keeps repeated application hops instead of prepending the supplied identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_Normalize_InsertsTrackerOnceAndKeepsApplicationHops()
        {
            WriteAndAssert(
                "nntpd01.usenet.ninja!nntpd01.usenet.ninja!peer.example",
                "cache01.usenet.ninja",
                "news.usenet.ninja!nntpd01.usenet.ninja!nntpd01.usenet.ninja!peer.example",
                ArticlePathMode.Normalize);
        }

        /// <summary>
        /// Normalize collapses a repeated tracker that differs only by case, and a second normalize leaves that result unchanged.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_Normalize_CollapsesRepeatedTrackerAndIsIdempotent()
        {
            const string raw = "news.usenet.ninja!nntpd01.usenet.ninja!NEWS.USENET.NINJA!peer.example";
            const string once = "news.usenet.ninja!nntpd01.usenet.ninja!peer.example";
            WriteAndAssert(raw, "cache01.usenet.ninja", once, ArticlePathMode.Normalize);
            WriteAndAssert(once, "cache01.usenet.ninja", once, ArticlePathMode.Normalize);
        }

        /// <summary>
        /// Traverse prepends the local identity again and does not remove earlier copies of that same identity.
        /// </summary>
        [Fact]
        public void TryWriteCanonicalPath_Traverse_DoesNotDeduplicateApplicationFqdn()
        {
            WriteAndAssert(
                "nntpd01.usenet.ninja!cache01.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja",
                "nntpd01.usenet.ninja",
                "nntpd01.usenet.ninja!nntpd01.usenet.ninja!cache01.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja");
        }

        /// <summary>
        /// A DEL octet in a path hop makes analysis fail with <see cref="NntpArticleParseFailureCode.InvalidPath"/>.
        /// </summary>
        [Fact]
        public void TryAnalyze_WhenControlBytePresent_Rejects()
        {
            Assert.False(ArticlePathCanonicalizer.TryAnalyze(
                "feed\x7f!other"u8,
                LocalIdentity,
                pathPresent: true,
                out _,
                out _,
                out var failure));
            Assert.Equal(NntpArticleParseFailureCode.InvalidPath, failure);
        }

        /// <summary>
        /// Analyzes <paramref name="rawPath"/> and returns the canonical Path text for <paramref name="localIdentity"/>.
        /// </summary>
        /// <param name="rawPath">Path value encoded as ASCII before analysis.</param>
        /// <param name="localIdentity">Application hop supplied to analysis and the canonical write.</param>
        /// <param name="mode">Traverse or normalize mode passed to the canonical write. Defaults to traverse.</param>
        /// <returns>ASCII text of the bytes written by the canonical Path writer.</returns>
        private static string Rewrite(string rawPath, string localIdentity, ArticlePathMode mode = ArticlePathMode.Traverse)
        {
            var raw = Encoding.ASCII.GetBytes(rawPath);
            var local = Encoding.ASCII.GetBytes(localIdentity);
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                raw,
                local,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));
            Span<byte> destination = stackalloc byte[512];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                raw,
                local,
                kind,
                containsTracker,
                mode,
                destination,
                out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }

        /// <summary>
        /// Asserts that analyzing and writing <paramref name="rawPath"/> produces <paramref name="expected"/>
        /// and that the result contains the organizational tracker exactly once.
        /// </summary>
        /// <param name="rawPath">Input Path value.</param>
        /// <param name="localIdentity">Application hop used for analysis and the write.</param>
        /// <param name="expected">Canonical Path text required from the write.</param>
        /// <param name="mode">Path mode under test. Defaults to traverse.</param>
        private static void WriteAndAssert(
            string rawPath,
            string localIdentity,
            string expected,
            ArticlePathMode mode = ArticlePathMode.Traverse)
        {
            var raw = Encoding.ASCII.GetBytes(rawPath);
            var local = Encoding.ASCII.GetBytes(localIdentity);
            Assert.True(ArticlePathCanonicalizer.TryAnalyze(
                raw,
                local,
                pathPresent: true,
                out var kind,
                out var containsTracker,
                out _));

            Span<byte> destination = stackalloc byte[512];
            Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
                raw,
                local,
                kind,
                containsTracker,
                mode,
                destination,
                out var written));
            Assert.Equal(expected, Encoding.ASCII.GetString(destination[..written]));
            Assert.Equal(1, CountToken(destination[..written], Encoding.ASCII.GetBytes(Tracker)));
        }

        /// <summary>
        /// Counts hops in <paramref name="path"/> that are byte-for-byte equal to <paramref name="token"/>.
        /// </summary>
        /// <param name="path">Canonical Path bytes split on <c>!</c>.</param>
        /// <param name="token">Hop bytes to count. Comparison is ordinal and case-sensitive.</param>
        /// <returns>The number of exact hop matches.</returns>
        private static int CountToken(ReadOnlySpan<byte> path, ReadOnlySpan<byte> token)
        {
            var remaining = path;
            var count = 0;
            while (!remaining.IsEmpty)
            {
                var separator = remaining.IndexOf((byte)'!');
                ReadOnlySpan<byte> component;
                if (separator < 0)
                {
                    component = remaining;
                    remaining = default;
                }
                else
                {
                    component = remaining[..separator];
                    remaining = remaining[(separator + 1)..];
                }

                if (component.Length == token.Length && component.SequenceEqual(token))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
