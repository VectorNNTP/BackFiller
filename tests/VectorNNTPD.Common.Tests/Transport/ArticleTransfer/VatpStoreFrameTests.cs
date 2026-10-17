using System.Buffers.Binary;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// STORE and RESULT payload round-trips and rejection of a short STORE frame.
    /// </summary>
    public sealed class VatpStoreFrameTests
    {
        /// <summary>
        /// A STORE frame on stream 7 carries the encoded <see cref="ArticleId"/> and decodes back to the same id.
        /// </summary>
        [Fact]
        public void Store_RoundTripsArticleId()
        {
            var id = ArticleId.FromMessageId("<store@example.test>"u8);
            Span<byte> bytes = stackalloc byte[ArticleId.Length];
            id.CopyTo(bytes);
            var encoded = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeStore(7, bytes));
            var parsed = VatpFrameParser.ParseOneFrame(encoded, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
            Assert.Equal(VatpFrameType.Store, parsed.Frame!.Value.Header.Type);
            Assert.Equal(7u, parsed.Frame.Value.Header.StreamId);
            Assert.True(VatpStorePayload.TryDecode(parsed.Frame.Value.Payload, out var decoded, out _));
            Assert.Equal(id, decoded);
        }

        /// <summary>
        /// A RESULT frame on stream 3 carries outcome byte 2 through encode, parse, and decode.
        /// </summary>
        [Fact]
        public void Result_RoundTripsOutcomeByte()
        {
            var encoded = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeResult(3, 2));
            var parsed = VatpFrameParser.ParseOneFrame(encoded, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
            Assert.Equal(VatpFrameType.Result, parsed.Frame!.Value.Header.Type);
            Assert.True(VatpResultPayload.TryDecode(parsed.Frame.Value.Payload, out var outcome, out _));
            Assert.Equal((byte)2, outcome);
        }

        /// <summary>
        /// A STORE frame that declares a 4-byte payload is <see cref="VatpFrameParseStatus.Invalid"/> with <see cref="VatpErrorCode.InvalidFrameLength"/>.
        /// </summary>
        [Fact]
        public void Parser_RejectsMalformedStorePayload()
        {
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), VatpProtocol.HeaderLengthBytes);
            header[0] = VatpProtocol.Version1;
            header[1] = (byte)VatpFrameType.Store;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 4);
            var frame = new byte[header.Length + 4];
            header.CopyTo(frame);
            var parsed = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Invalid, parsed.Status);
            Assert.Equal(VatpErrorCode.InvalidFrameLength, parsed.Error);
        }
    }
}
