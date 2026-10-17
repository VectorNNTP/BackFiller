using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// Stream window credit, receive-stream flow control, ready-ring scheduling, and stream-table admission.
    /// </summary>
    public sealed class ArticleTransferWindowAndFairnessTests
    {
        /// <summary>
        /// Production defaults start at 1 MiB of credit with a 64 MiB ceiling. Consuming 64 KiB and adding it back restores 1 MiB. An initial credit above the ceiling throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        [Fact]
        public void Production_default_initial_window_is_one_mebibyte_and_refunds_once()
        {
            Assert.Equal(1024 * 1024, ArticleTransferLimits.Default.InitialStreamWindowBytes);
            Assert.Equal(64L * 1024 * 1024, ArticleTransferLimits.Default.MaxStreamCreditBytes);
            var window = new ArticleTransferWindow(
                ArticleTransferLimits.Default.InitialStreamWindowBytes,
                ArticleTransferLimits.Default.MaxStreamCreditBytes);
            Assert.Equal(1024 * 1024, window.Credit);
            Assert.True(window.TryConsume(64 * 1024));
            Assert.Equal((1024 * 1024) - (64 * 1024), window.Credit);
            Assert.Equal(64 * 1024, window.Add(64 * 1024));
            Assert.Equal(1024 * 1024, window.Credit);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ArticleTransferWindow(
                    (int)ArticleTransferLimits.Default.MaxStreamCreditBytes + 1,
                    ArticleTransferLimits.Default.MaxStreamCreditBytes));
        }

        /// <summary>
        /// Credit starts at the initial value, a consume that fits reduces it, a consume that does not fit leaves it unchanged, and a later add increases it by the added amount.
        /// </summary>
        [Fact]
        public void Window_InitialCredit_ConsumeAndRestore()
        {
            var window = new ArticleTransferWindow(initialCredit: 1024, maxCredit: 4096);
            Assert.Equal(1024, window.Credit);
            Assert.True(window.TryConsume(100));
            Assert.Equal(924, window.Credit);
            Assert.False(window.TryConsume(1000));
            Assert.Equal(924, window.Credit);
            Assert.Equal(1000, window.Add(1000));
            Assert.Equal(1924, window.Credit);
        }

        /// <summary>
        /// <c>Add</c> returns only the room remaining under the maximum, then returns zero once credit is saturated.
        /// </summary>
        [Fact]
        public void Window_Add_SaturatesAtMax()
        {
            var window = new ArticleTransferWindow(initialCredit: 100, maxCredit: 150);
            Assert.Equal(50, window.Add(1000));
            Assert.Equal(150, window.Credit);
            Assert.Equal(0, window.Add(10));
        }

        /// <summary>
        /// A consume larger than the balance fails and leaves credit unchanged. Consuming the exact balance reaches zero and clears <c>HasCredit</c>.
        /// </summary>
        [Fact]
        public void Window_Consume_CannotUnderflow()
        {
            var window = new ArticleTransferWindow(10, 100);
            Assert.False(window.TryConsume(11));
            Assert.Equal(10, window.Credit);
            Assert.True(window.TryConsume(10));
            Assert.Equal(0, window.Credit);
            Assert.False(window.HasCredit);
        }

        /// <summary>
        /// DATA larger than the stream's initial credit is rejected as <see cref="VatpErrorCode.FlowControlViolation"/>.
        /// </summary>
        [Fact]
        public void ReceiveStream_DataExceedingCredit_Fails()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 8, MaxStreamCreditBytes = 8 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            Assert.True(artData.Length > 8);
            var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            var result = stream.TryAcceptData(artData.AsSpan(0, 9), fin: false);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.FlowControlViolation, result.Error);
        }

        /// <summary>
        /// After credit is exhausted, a WINDOW replenishment allows the remaining bytes. The record stays unavailable until END.
        /// </summary>
        [Fact]
        public void ReceiveStream_WindowRestoresCredit()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 4, MaxStreamCreditBytes = 1024 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(2, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, 4), fin: false).Success);
            Assert.Equal(0, stream.ReceiveCredit);
            Assert.True(stream.TryAcceptWindow((uint)(artData.Length - 4)).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(4), fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
        }

        /// <summary>
        /// An 8-byte window needs at least three replenishments to accept an article larger than two windows, then END publishes the record at the exact received length.
        /// </summary>
        [Fact]
        public void ReceiveStream_MultipleWindowReplenishments_ReachExactArtSize()
        {
            // Mirror DefaultInitialStreamWindowBytes pacing with a tiny window so several
            // replenishments are required before ArtSize is complete.
            const int initialWindow = 8;
            const int chunk = 8;
            var limits = new ArticleTransferLimits
            {
                InitialStreamWindowBytes = initialWindow,
                MaxStreamCreditBytes = 1024,
            };
            var body = new string('x', 40) + "\r\n";
            var (record, selected, artData) = VatpTestArticles.CreateCanonical("<multi-window@example.test>", body);
            Assert.True(artData.Length > initialWindow * 2);

            var stream = new ArticleTransferReceiveStream(3, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);

            var offset = 0;
            var replenishments = 0;
            while (offset < artData.Length)
            {
                var length = Math.Min(chunk, artData.Length - offset);
                var fin = offset + length >= artData.Length;
                Assert.True(stream.TryAcceptData(artData.AsSpan(offset, length), fin).Success);
                Assert.True(stream.TryAcceptWindow((uint)length).Success);
                replenishments++;
                offset += length;
                Assert.True(stream.ReceiveCredit <= limits.MaxStreamCreditBytes);
                Assert.True(stream.ReceiveCredit >= 0);
            }

            Assert.True(replenishments >= 3);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
        }

        /// <summary>
        /// After cancel, WINDOW is rejected as <see cref="VatpErrorCode.UnknownStream"/> and the phase stays <see cref="ArticleTransferPhase.Cancelled"/>.
        /// </summary>
        [Fact]
        public void ReceiveStream_Cancel_DoesNotAcceptFurtherWindow()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16, MaxStreamCreditBytes = 1024 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(4, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, 4), fin: false).Success);
            Assert.True(stream.TryCancel().Success);
            Assert.False(stream.TryAcceptWindow(4).Success);
            Assert.Equal(VatpErrorCode.UnknownStream, stream.TryAcceptWindow(4).Error);
            Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
        }

        /// <summary>
        /// The ready ring returns stream ids 1 then 2. Removing 3 before it is taken makes the next turn wrap to 1.
        /// </summary>
        [Fact]
        public void ReadyRing_RoundRobinsAndSkipsRemoved()
        {
            var ring = new ArticleTransferReadyRing();
            ring.Enqueue(1);
            ring.Enqueue(2);
            ring.Enqueue(3);
            Assert.True(ring.TryTakeNext(out var a));
            Assert.Equal(1u, a);
            Assert.True(ring.TryTakeNext(out var b));
            Assert.Equal(2u, b);
            Assert.True(ring.Remove(3));
            Assert.True(ring.TryTakeNext(out var c));
            Assert.Equal(1u, c);
        }

        /// <summary>
        /// DATA payload length is the minimum of remaining article bytes, credit, and the maximum frame payload, and is zero when credit is zero.
        /// </summary>
        [Fact]
        public void ComputeDataPayloadLength_RespectsRemainingCreditAndMaxFrame()
        {
            Assert.Equal(0, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 0, maxFramePayload: 64));
            Assert.Equal(50, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 50, maxFramePayload: 64));
            Assert.Equal(64, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 1000, maxFramePayload: 64));
            Assert.Equal(20, ArticleTransferReadyRing.ComputeDataPayloadLength(20, credit: 1000, maxFramePayload: 64));
        }

        /// <summary>
        /// With a one-stream limit, stream id 0 is rejected, a duplicate id is rejected, a second id is rejected, and the same id can be opened again after removal.
        /// </summary>
        [Fact]
        public void StreamTable_RejectsDuplicateAndZeroAndOverflow()
        {
            var limits = new ArticleTransferLimits { MaxStreamsPerConnection = 1 };
            var table = new ArticleTransferStreamTable(limits);
            var artId = ArticleId.FromMessageId("<a@b>"u8);
            Assert.False(table.TryOpen(0, Guid.NewGuid(), artId, out _).Success);
            Assert.True(table.TryOpen(1, Guid.NewGuid(), artId, out var stream).Success);
            Assert.NotNull(stream);
            Assert.False(table.TryOpen(1, Guid.NewGuid(), artId, out _).Success);
            Assert.False(table.TryOpen(2, Guid.NewGuid(), artId, out _).Success);
            Assert.True(table.TryRemove(1));
            Assert.True(table.TryOpen(1, Guid.NewGuid(), artId, out _).Success);
        }

        /// <summary>
        /// WINDOW while the stream is still <see cref="ArticleTransferPhase.AwaitingMeta"/> is rejected as <see cref="VatpErrorCode.UnknownStream"/>.
        /// </summary>
        [Fact]
        public void WindowOnAwaitingMeta_IsUnknownStream()
        {
            var stream = new ArticleTransferReceiveStream(11, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
            var result = stream.TryAcceptWindow(100);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.UnknownStream, result.Error);
        }
    }
}
