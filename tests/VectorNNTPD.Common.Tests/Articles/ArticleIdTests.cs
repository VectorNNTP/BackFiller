using System.Buffers.Binary;
using Blake3;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// <see cref="ArticleId"/> is a BLAKE3 digest of the exact Message-ID value bytes, including angle brackets,
    /// with no trim or case folding.
    /// </summary>
    public sealed class ArticleIdTests
    {
        /// <summary>Exact Message-ID value bytes, including angle brackets, used as the BLAKE3 input.</summary>
        private static readonly byte[] ExampleId = "<id@example>"u8.ToArray();

        /// <summary>
        /// Two calls with the same Message-ID bytes produce the same <see cref="ArticleId"/>,
        /// and that identifier's bytes equal <c>Blake3.Hasher.Hash</c> of those bytes.
        /// </summary>
        [Fact]
        public void FromMessageId_IsDeterministicAndIncludesAngleBrackets()
        {
            var first = ArticleId.FromMessageId(ExampleId);
            var second = ArticleId.FromMessageId("<id@example>"u8);
            Assert.Equal(first, second);

            Span<byte> bytes = stackalloc byte[ArticleId.Length];
            first.CopyTo(bytes);
            var expected = Hasher.Hash(ExampleId);
            Assert.True(bytes.SequenceEqual(expected.AsSpan()));
        }

        /// <summary>
        /// Hashing the Message-ID value differs from hashing a buffer that also contains the <c>Message-ID:</c> field name.
        /// </summary>
        [Fact]
        public void FromMessageId_HashesValueBytes_NotFieldName()
        {
            var valueOnly = ArticleId.FromMessageId(ExampleId);
            var includingFieldName = ArticleId.FromMessageId("Message-ID: <id@example>"u8);
            Assert.NotEqual(valueOnly, includingFieldName);
            Assert.Equal(ArticleId.FromMessageId("<id@example>"u8), valueOnly);
        }

        /// <summary>
        /// Case differences and surrounding spaces change the digest; the bytes are not normalized before hashing.
        /// </summary>
        [Fact]
        public void FromMessageId_DoesNotTrimOrCaseFold()
        {
            var exact = ArticleId.FromMessageId("<Id@Example>"u8);
            var folded = ArticleId.FromMessageId("<id@example>"u8);
            var padded = ArticleId.FromMessageId(" <id@example> "u8);
            Assert.NotEqual(exact, folded);
            Assert.NotEqual(folded, padded);
        }

        /// <summary>
        /// The digest of <c>&lt;id@example&gt;</c> matches the known BLAKE3 hex
        /// <c>CFC44E7F244E2AAB6D1E7FF93F9FB93C5DDE9DAFAA16F6DB2FBBAE712881D250</c>
        /// and is <see cref="ArticleId.Length"/> bytes.
        /// </summary>
        [Fact]
        public void FromMessageId_KnownDigest_MatchesBlake3OfExactBytes()
        {
            var id = ArticleId.FromMessageId(ExampleId);
            Span<byte> actual = stackalloc byte[ArticleId.Length];
            id.CopyTo(actual);
            var expected = Hasher.Hash(ExampleId);
            Assert.True(actual.SequenceEqual(expected.AsSpan()));
            Assert.Equal(
                "CFC44E7F244E2AAB6D1E7FF93F9FB93C5DDE9DAFAA16F6DB2FBBAE712881D250",
                Convert.ToHexString(actual));
            Assert.Equal(ArticleId.Length, actual.Length);
        }

        /// <summary>
        /// After warmup, <see cref="ArticleId.FromMessageId"/> and <see cref="ArticleId.CopyTo(Span{byte})"/>
        /// allocate nothing on the current thread, and <see cref="ArticleId.FromSpan(ReadOnlySpan{byte})"/> rebuilds the same value.
        /// </summary>
        [Fact]
        public void ValueType_DoesNotAllocateAfterWarmup()
        {
            _ = ArticleId.FromMessageId(ExampleId);
            _ = ArticleId.FromMessageId(ExampleId);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var id = ArticleId.FromMessageId(ExampleId);
            Span<byte> destination = stackalloc byte[ArticleId.Length];
            id.CopyTo(destination);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(id, ArticleId.FromSpan(destination));
        }

        /// <summary>
        /// <see cref="ArticleId.FromSpan(ReadOnlySpan{byte})"/> throws <see cref="ArgumentException"/> when the span is not <see cref="ArticleId.Length"/> bytes.
        /// </summary>
        [Fact]
        public void FromSpan_RejectsWrongLength()
        {
            Assert.Throws<ArgumentException>(() => ArticleId.FromSpan([1, 2, 3]));
        }

        /// <summary>
        /// Copying an identifier to a span and reading it back with <see cref="ArticleId.FromSpan(ReadOnlySpan{byte})"/>
        /// restores the same value, and the first little-endian 64-bit word of the example digest is non-zero.
        /// </summary>
        [Fact]
        public void LittleEndianWords_RoundTrip()
        {
            var id = ArticleId.FromMessageId(ExampleId);
            Span<byte> bytes = stackalloc byte[ArticleId.Length];
            id.CopyTo(bytes);
            Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            Assert.Equal(id, ArticleId.FromSpan(bytes));
        }
    }
}
