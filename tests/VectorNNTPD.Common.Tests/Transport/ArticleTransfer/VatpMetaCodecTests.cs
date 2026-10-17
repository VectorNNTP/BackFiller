using System.Buffers.Binary;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// Fixed 76-byte <see cref="VatpMetaCodec"/> layout, pad and range rejection, and the META frame length check.
    /// </summary>
    public sealed class VatpMetaCodecTests
    {
        /// <summary>
        /// Canonical meta encodes to 76 bytes, decodes equal with <see cref="VatpErrorCode.None"/>, and keeps the three pad bytes at offsets 17 through 19 zero.
        /// </summary>
        [Fact]
        public void EncodeDecode_RoundTripsExact76Bytes()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var encoded = VatpMetaCodec.Encode(in meta);
            Assert.Equal(76, encoded.Length);

            Assert.True(VatpMetaCodec.TryDecode(encoded, out var decoded, out var error));
            Assert.Equal(VatpErrorCode.None, error);
            Assert.Equal(meta, decoded);
            Assert.Equal(0, encoded[17]);
            Assert.Equal(0, encoded[18]);
            Assert.Equal(0, encoded[19]);
        }

        /// <summary>
        /// A non-zero pad byte at offset 18 is <see cref="VatpErrorCode.InvalidMeta"/>.
        /// </summary>
        [Fact]
        public void Decode_NonZeroPad_Fails()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var encoded = VatpMetaCodec.Encode(in meta);
            encoded[18] = 1;
            Assert.False(VatpMetaCodec.TryDecode(encoded, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidMeta, error);
        }

        /// <summary>
        /// An absent field range (offset -1) with a non-zero length is <see cref="VatpErrorCode.InvalidFieldRange"/>.
        /// </summary>
        [Fact]
        public void Decode_AbsentRangeWithNonZeroLength_Fails()
        {
            var encoded = new byte[76];
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(20, 4), -1);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(24, 4), 5);
            Assert.False(VatpMetaCodec.TryDecode(encoded, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidFieldRange, error);
        }

        /// <summary>
        /// A 75-byte buffer is <see cref="VatpErrorCode.InvalidMeta"/>.
        /// </summary>
        [Fact]
        public void Decode_WrongLength_Fails()
        {
            Assert.False(VatpMetaCodec.TryDecode(new byte[75], out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidMeta, error);
        }

        /// <summary>
        /// Maximum hash, line count, article size, and a Message-ID range covering <see cref="ArticleResourceLimits.MaxArticleBytes"/> round-trip equal.
        /// </summary>
        [Fact]
        public void Encode_MaximumValidValues_RoundTrip()
        {
            var fields = new ArticleFieldTable(
                new ArticleByteRange(0, ArticleResourceLimits.MaxArticleBytes),
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent);
            var meta = new ArticleCanonicalTransferMeta(
                ulong.MaxValue,
                ArticleResourceLimits.MaxArticleBytes,
                ArticleResourceLimits.MaxArticleBytes,
                NntpArticleHeaderName.Date,
                fields);
            var encoded = VatpMetaCodec.Encode(in meta);
            Assert.True(VatpMetaCodec.TryDecode(encoded, out var decoded, out _));
            Assert.Equal(meta, decoded);
        }

        /// <summary>
        /// <see cref="VatpFrameEncoder.EncodeMeta"/> rejects a 75-byte payload with <see cref="ArgumentException"/>.
        /// </summary>
        [Fact]
        public void FrameEncoder_Meta_Requires76Bytes()
        {
            Assert.Throws<ArgumentException>(() => VatpFrameEncoder.EncodeMeta(1, new byte[75]));
        }
    }
}
