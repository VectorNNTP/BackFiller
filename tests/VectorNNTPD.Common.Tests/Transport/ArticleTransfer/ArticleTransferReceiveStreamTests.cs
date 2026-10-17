using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// Phase, error, and received-byte contracts for one <see cref="ArticleTransferReceiveStream"/>.
    /// </summary>
    public sealed class ArticleTransferReceiveStreamTests
    {
        /// <summary>
        /// META, DATA with FIN, and END accept one canonical article. The record becomes consumable only after END, and <c>TryTakeRecord</c> yields it once as <see cref="ArticleParseStatus.CanonicalV1"/>.
        /// </summary>
        [Fact]
        public void OpenMetaDataFinEnd_ValidArticle_ProducesOneConsumableRecord()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), record.ArtId);

            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta)).Success);
            Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);

            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.False(stream.HasConsumableRecord);

            Assert.True(stream.TryAcceptEnd().Success);
            Assert.Equal(ArticleTransferPhase.Completed, stream.Phase);
            Assert.True(stream.HasConsumableRecord);
            Assert.Equal(record.ArtId, stream.ConsumableRecord.ArtId);
            Assert.True(stream.TryTakeRecord(out var taken));
            Assert.Equal(ArticleParseStatus.CanonicalV1, taken.ParseStatus);
            Assert.False(stream.TryTakeRecord(out _));
        }

        /// <summary>
        /// DATA with FIN reaches <see cref="ArticleTransferPhase.AwaitingEnd"/> and does not publish a consumable record before END.
        /// </summary>
        [Fact]
        public void FinAlone_DoesNotProduceConsumableRecord()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(11, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// END while DATA has not set FIN fails as <see cref="VatpErrorCode.IncompleteTransfer"/> and moves the stream to <see cref="ArticleTransferPhase.Failed"/>.
        /// </summary>
        [Fact]
        public void EndWithoutFin_FailsIncomplete()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(2, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: false).Success);
            Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
            var end = stream.TryAcceptEnd();
            Assert.False(end.Success);
            Assert.Equal(VatpErrorCode.IncompleteTransfer, end.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
        }

        /// <summary>
        /// The exact article bytes without FIN stay in <see cref="ArticleTransferPhase.ReceivingData"/>. An empty FIN payload then allows END to publish a record.
        /// </summary>
        [Fact]
        public void ExactBytesThenEmptyFinThenEnd_Completes()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(12, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: false).Success);
            Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);
            Assert.True(stream.TryAcceptData(ReadOnlySpan<byte>.Empty, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
        }

        /// <summary>
        /// END after a partial DATA chunk, before the final bytes, fails as <see cref="VatpErrorCode.IncompleteTransfer"/>.
        /// </summary>
        [Fact]
        public void EndBeforeFinalData_FailsIncomplete()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(13, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, Math.Max(1, artData.Length / 2)), fin: false).Success);
            var end = stream.TryAcceptEnd();
            Assert.False(end.Success);
            Assert.Equal(VatpErrorCode.IncompleteTransfer, end.Error);
        }

        /// <summary>
        /// DATA after a successful FIN is rejected as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void DataAfterFin_Fails()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(14, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            var again = stream.TryAcceptData(ReadOnlySpan<byte>.Empty, fin: false);
            Assert.False(again.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, again.Error);
        }

        /// <summary>
        /// A second END after a completed transfer is rejected as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void DuplicateEnd_Fails()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(15, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.True(stream.TryAcceptEnd().Success);
            var second = stream.TryAcceptEnd();
            Assert.False(second.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, second.Error);
        }

        /// <summary>
        /// Exact DATA, FIN, and END with a corrupted meta hash fail as <see cref="VatpErrorCode.ArtHashMismatch"/>, leave no consumable record, and make <c>ConsumableRecord</c> throw <see cref="InvalidOperationException"/>.
        /// </summary>
        [Fact]
        public void ExactDataPlusFinPlusEndPlusInvalidHash_DoesNotProduceConsumableRecord()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash ^ 0xDEADUL,
                record.ArtLines,
                record.ArtSize,
                selected,
                record.Fields);
            var stream = new ArticleTransferReceiveStream(3, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta)).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            var result = stream.TryAcceptEnd();
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.ArtHashMismatch, result.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
            Assert.Throws<InvalidOperationException>(() => _ = stream.ConsumableRecord);
        }

        /// <summary>
        /// DATA before META is rejected as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void DataBeforeMeta_Fails()
        {
            var stream = new ArticleTransferReceiveStream(4, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
            var result = stream.TryAcceptData("abc"u8, fin: false);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, result.Error);
        }

        /// <summary>
        /// A second META after a successful META is rejected as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void DuplicateMeta_Fails()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var metaBytes = VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected));
            var stream = new ArticleTransferReceiveStream(5, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(metaBytes).Success);
            var second = stream.TryAcceptMeta(metaBytes);
            Assert.False(second.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, second.Error);
        }

        /// <summary>
        /// END before META fails as <see cref="VatpErrorCode.IncompleteTransfer"/>.
        /// </summary>
        [Fact]
        public void EndBeforeMeta_Fails()
        {
            var stream = new ArticleTransferReceiveStream(6, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
            var result = stream.TryAcceptEnd();
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.IncompleteTransfer, result.Error);
        }

        /// <summary>
        /// DATA after END has completed the stream is rejected as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void DataAfterCompleted_Fails()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(7, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.True(stream.TryAcceptEnd().Success);
            var again = stream.TryAcceptData("x"u8, fin: false);
            Assert.False(again.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, again.Error);
        }

        /// <summary>
        /// Cancel after META moves the stream to <see cref="ArticleTransferPhase.Cancelled"/> and publishes no record.
        /// </summary>
        [Fact]
        public void Cancel_TerminatesWithoutRecord()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(8, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryCancel().Success);
            Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// Two DATA chunks reassemble the original article bytes, and the record is consumable only after END.
        /// </summary>
        [Fact]
        public void ChunkedData_AssemblesExactBytes()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(9, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);

            var mid = artData.Length / 2;
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, mid), fin: false).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(mid), fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
            Assert.True(stream.ConsumableRecord.ArtData.Span.SequenceEqual(artData));
        }

        /// <summary>
        /// META whose article size is one byte above <see cref="ArticleResourceLimits.MaxArticleBytes"/> is rejected as <see cref="VatpErrorCode.ArticleTooLarge"/>.
        /// </summary>
        [Fact]
        public void OversizedArticleMeta_Rejected()
        {
            var fields = new ArticleFieldTable(
                new ArticleByteRange(0, 1),
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent);
            var meta = new ArticleCanonicalTransferMeta(
                0,
                0,
                ArticleResourceLimits.MaxArticleBytes + 1,
                VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName.Date,
                fields);
            var stream = new ArticleTransferReceiveStream(10, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
            var result = stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta));
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.ArticleTooLarge, result.Error);
        }

        /// <summary>
        /// Rejected oversized META fails the stream, leaves <c>ReceivedBytes</c> at zero, and publishes no record.
        /// </summary>
        [Fact]
        public void MetaRejected_PreservesZeroReceivedBytes()
        {
            var fields = new ArticleFieldTable(
                new ArticleByteRange(0, 1),
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent);
            var meta = new ArticleCanonicalTransferMeta(
                0,
                0,
                ArticleResourceLimits.MaxArticleBytes + 1,
                VectorNNTPD.Common.Articles.Parsing.NntpArticleHeaderName.Date,
                fields);
            var stream = new ArticleTransferReceiveStream(20, Guid.NewGuid(), ArticleId.FromMessageId("<meta-reject@b>"u8));
            var result = stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta));
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.ArticleTooLarge, result.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(0, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// DATA before META fails the stream as <see cref="VatpErrorCode.InvalidStateTransition"/> and leaves <c>ReceivedBytes</c> at zero.
        /// </summary>
        [Fact]
        public void DataBeforeMeta_PreservesZeroReceivedBytes()
        {
            var stream = new ArticleTransferReceiveStream(21, Guid.NewGuid(), ArticleId.FromMessageId("<data-before-meta@b>"u8));
            var result = stream.TryAcceptData("abc"u8, fin: false);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, result.Error);
            Assert.Equal(0, stream.ReceivedBytes);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
        }

        /// <summary>
        /// FIN with fewer bytes than the meta article size fails as <see cref="VatpErrorCode.ArtSizeMismatch"/> and keeps <c>ReceivedBytes</c> equal to the copied prefix.
        /// </summary>
        [Fact]
        public void ShortDataWithFin_PreservesCopiedPayloadLength()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var copied = Math.Max(1, artData.Length / 2);
            Assert.True(copied < artData.Length);
            Assert.NotEqual(record.ArtSize, copied);
            var stream = new ArticleTransferReceiveStream(22, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);

            var rejected = stream.TryAcceptData(artData.AsSpan(0, copied), fin: true);
            Assert.False(rejected.Success);
            Assert.Equal(VatpErrorCode.ArtSizeMismatch, rejected.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(copied, stream.ReceivedBytes);
            Assert.NotEqual(0, stream.ReceivedBytes);
            Assert.NotEqual(record.ArtSize, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// A later chunk that would exceed the meta article size is rejected as <see cref="VatpErrorCode.ArtSizeMismatch"/> and <c>ReceivedBytes</c> stays at the earlier accepted chunk.
        /// </summary>
        [Fact]
        public void DataRejectedBeforeCopy_PreservesEarlierChunkOnly()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var first = Math.Max(1, artData.Length / 2);
            Assert.True(first < artData.Length);
            var stream = new ArticleTransferReceiveStream(23, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, first), fin: false).Success);

            var overflow = new byte[artData.Length - first + 1];
            var rejected = stream.TryAcceptData(overflow, fin: false);
            Assert.False(rejected.Success);
            Assert.Equal(VatpErrorCode.ArtSizeMismatch, rejected.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(first, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// END without FIN fails as <see cref="VatpErrorCode.IncompleteTransfer"/> and leaves <c>ReceivedBytes</c> equal to the already accepted article length.
        /// </summary>
        [Fact]
        public void EndBeforeFin_PreservesAcceptedDataLength()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(24, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData, fin: false).Success);
            Assert.Equal(artData.Length, stream.ReceivedBytes);

            var end = stream.TryAcceptEnd();
            Assert.False(end.Success);
            Assert.Equal(VatpErrorCode.IncompleteTransfer, end.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);
        }

        /// <summary>
        /// A hash mismatch at END fails the stream as <see cref="VatpErrorCode.ArtHashMismatch"/>, keeps <c>ReceivedBytes</c> at the article size, and makes <c>ConsumableRecord</c> throw <see cref="InvalidOperationException"/>.
        /// </summary>
        [Fact]
        public void HashMismatchAfterFullBody_PreservesArtSize()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash ^ 0xDEADUL,
                record.ArtLines,
                record.ArtSize,
                selected,
                record.Fields);
            var stream = new ArticleTransferReceiveStream(25, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta)).Success);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.Equal(artData.Length, stream.ReceivedBytes);

            var result = stream.TryAcceptEnd();
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.ArtHashMismatch, result.Error);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(record.ArtSize, stream.ReceivedBytes);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);
            Assert.Throws<InvalidOperationException>(() => _ = stream.ConsumableRecord);
        }

        /// <summary>
        /// Cancel after a partial DATA chunk keeps that length, stays <see cref="ArticleTransferPhase.Cancelled"/>, rejects a later WINDOW as <see cref="VatpErrorCode.UnknownStream"/>, and rejects more DATA as <see cref="VatpErrorCode.InvalidStateTransition"/>.
        /// </summary>
        [Fact]
        public void CancelAfterPartialData_PreservesAcceptedLengthAndRejectsMoreInput()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var copied = Math.Max(1, artData.Length / 2);
            var stream = new ArticleTransferReceiveStream(26, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, copied), fin: false).Success);

            Assert.True(stream.TryCancel().Success);
            Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
            Assert.Equal(copied, stream.ReceivedBytes);
            Assert.False(stream.HasConsumableRecord);

            Assert.False(stream.TryAcceptWindow(1).Success);
            Assert.Equal(VatpErrorCode.UnknownStream, stream.TryAcceptWindow(1).Error);
            Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
            Assert.Equal(copied, stream.ReceivedBytes);

            var more = stream.TryAcceptData(artData.AsSpan(0, 1), fin: false);
            Assert.False(more.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, more.Error);
            Assert.Equal(copied, stream.ReceivedBytes);
        }

        /// <summary>
        /// A successful META, FIN DATA, END, and take leaves <c>ReceivedBytes</c> at the payload length and returns one <see cref="ArticleParseStatus.CanonicalV1"/> record with those bytes.
        /// </summary>
        [Fact]
        public void SuccessfulTransfer_PreservesPayloadLengthAfterEnd()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(27, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.Equal(0, stream.ReceivedBytes);
            Assert.True(stream.TryAcceptData(artData, fin: true).Success);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.Equal(ArticleTransferPhase.Completed, stream.Phase);
            Assert.True(stream.HasConsumableRecord);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.True(stream.TryTakeRecord(out var taken));
            Assert.Equal(ArticleParseStatus.CanonicalV1, taken.ParseStatus);
            Assert.True(taken.ArtData.Span.SequenceEqual(artData));
            Assert.Equal(artData.Length, stream.ReceivedBytes);
        }

        /// <summary>
        /// After a short FIN fails the stream, later DATA and END stay <see cref="ArticleTransferPhase.Failed"/>, do not change <c>ReceivedBytes</c>, and do not publish a record.
        /// </summary>
        [Fact]
        public void RepeatedFailure_DoesNotChangeReceivedBytesOrResurrect()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var copied = Math.Max(1, artData.Length / 2);
            Assert.True(copied < artData.Length);
            var stream = new ArticleTransferReceiveStream(28, Guid.NewGuid(), record.ArtId);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.False(stream.TryAcceptData(artData.AsSpan(0, copied), fin: true).Success);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.Equal(copied, stream.ReceivedBytes);

            var again = stream.TryAcceptData(artData.AsSpan(0, 1), fin: false);
            Assert.False(again.Success);
            Assert.Equal(VatpErrorCode.InvalidStateTransition, again.Error);
            Assert.Equal(copied, stream.ReceivedBytes);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.False(stream.HasConsumableRecord);

            var end = stream.TryAcceptEnd();
            Assert.False(end.Success);
            Assert.Equal(copied, stream.ReceivedBytes);
            Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
        }
    }
}
