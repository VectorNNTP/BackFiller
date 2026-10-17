using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// Canonical article fixtures and byte-buffer helpers shared by the article-transfer tests.
    /// </summary>
    internal static class VatpTestArticles
    {
        /// <summary>Path FQDN supplied to <see cref="NntpArticleParser"/> when building a canonical fixture.</summary>
        internal const string LocalFqdn = "vatp01.usenet.ninja";

        /// <summary>
        /// Parses and materializes one destuffed article, requiring both steps to accept it.
        /// </summary>
        /// <param name="messageId">Message-ID text, including angle brackets. Defaults to a simple example id.</param>
        /// <param name="body">Article body, including its CRLF line endings. Defaults to two lines.</param>
        /// <returns>The accepted record, the date header the parser selected, and a copy of the canonical article bytes.</returns>
        internal static (ArticleRecord Record, NntpArticleHeaderName SelectedDate, byte[] ArtData) CreateCanonical(
            string messageId = "<vatp@example.test>",
            string body = "line1\r\nline2\r\n")
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildDestuffed(messageId, body);
            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted, parse.FailureCode.ToString());
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var artData = created.Record.ArtData.ToArray();
            return (created.Record, parse.SelectedDateHeaderName, artData);
        }

        /// <summary>
        /// Builds ASCII article bytes with fixed Path, Date, Newsgroups, From, and Subject headers, the supplied Message-ID, and <paramref name="body"/>.
        /// </summary>
        /// <param name="messageId">Message-ID text inserted after the Message-ID header label.</param>
        /// <param name="body">Body text appended after the header blank line.</param>
        /// <returns>The destuffed article bytes. No leading-dot stuffing is applied.</returns>
        internal static byte[] BuildDestuffed(string messageId, string body)
        {
            var builder = new StringBuilder();
            _ = builder.Append("Path: peer.example\r\n");
            _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
            _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
            _ = builder.Append("Newsgroups: alt.test\r\n");
            _ = builder.Append("From: user@example.test\r\n");
            _ = builder.Append("Subject: vatp\r\n");
            _ = builder.Append("\r\n").Append(body);
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        /// <summary>
        /// Copies <paramref name="parts"/> into one array in order.
        /// </summary>
        /// <param name="parts">Buffers to concatenate. An empty list yields an empty array.</param>
        /// <returns>The concatenated bytes.</returns>
        internal static byte[] Concat(params ReadOnlyMemory<byte>[] parts)
        {
            var total = 0;
            foreach (var part in parts)
            {
                total += part.Length;
            }

            var buffer = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                part.Span.CopyTo(buffer.AsSpan(offset));
                offset += part.Length;
            }

            return buffer;
        }

        /// <summary>
        /// Splits <paramref name="frame"/> into linked segments of at most <paramref name="chunkSize"/> bytes.
        /// </summary>
        /// <param name="frame">Bytes to expose as a sequence.</param>
        /// <param name="chunkSize">Maximum segment length. A non-positive size, or a size at least the frame length, yields one segment over the whole frame.</param>
        /// <returns>A sequence whose segments cover <paramref name="frame"/> without copying it.</returns>
        internal static ReadOnlySequence<byte> Fragment(byte[] frame, int chunkSize)
        {
            if (chunkSize <= 0 || chunkSize >= frame.Length)
            {
                return new ReadOnlySequence<byte>(frame);
            }

            BufferSegment? first = null;
            BufferSegment? previous = null;
            for (var offset = 0; offset < frame.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, frame.Length - offset);
                var segment = new BufferSegment(frame.AsMemory(offset, length));
                if (first is null)
                {
                    first = segment;
                }
                else
                {
                    previous!.Append(segment);
                }

                previous = segment;
            }

            return new ReadOnlySequence<byte>(first!, 0, previous!, previous!.Memory.Length);
        }

        /// <summary>
        /// One segment in the chain produced by <see cref="Fragment"/>.
        /// </summary>
        private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
        {
            /// <summary>
            /// Creates a segment whose <see cref="ReadOnlySequenceSegment{T}.Memory"/> is <paramref name="memory"/>.
            /// </summary>
            /// <param name="memory">Bytes this segment exposes. The segment does not copy them.</param>
            public BufferSegment(ReadOnlyMemory<byte> memory)
            {
                Memory = memory;
            }

            /// <summary>
            /// Links <paramref name="next"/> and sets its running index to this segment's running index plus this segment's length.
            /// </summary>
            /// <param name="next">Segment that follows this one.</param>
            public void Append(BufferSegment next)
            {
                next.RunningIndex = RunningIndex + Memory.Length;
                Next = next;
            }
        }
    }
}
