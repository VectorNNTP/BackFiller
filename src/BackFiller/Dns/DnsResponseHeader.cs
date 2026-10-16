using System.Buffers.Binary;

namespace VectorNNTP.BackFiller.Dns
{
    /// <summary>
    /// Shared DNS response header checks (transaction ID, QR, OPCODE, TC, RCODE).
    /// </summary>
    internal static class DnsResponseHeader
    {
        /// <summary>
        /// Mask requiring QR=1, OPCODE=0, TC=0, and RCODE=0 (AA/RD/RA/Z ignored).
        /// </summary>
        private const ushort ResponseSanityMask = 0xFA0F;

        /// <summary>Expected masked value: response with NoError and no truncation.</summary>
        private const ushort ResponseSanityValue = 0x8000;

        /// <summary>DNS header TC (truncated) flag.</summary>
        public const ushort TruncatedFlag = 0x0200;

        /// <summary>
        /// Returns <see langword="true"/> when the buffer has a header, matching ID, and sane response flags.
        /// </summary>
        /// <param name="buffer">DNS message starting at offset zero.</param>
        /// <param name="expectedId">Transaction id that must match the header.</param>
        /// <param name="questionCount">QD count from the header when the read succeeds.</param>
        /// <param name="answerCount">AN count from the header when the read succeeds.</param>
        /// <param name="authorityCount">NS count from the header when the read succeeds.</param>
        /// <param name="additionalCount">AR count from the header when the read succeeds.</param>
        /// <returns><see langword="false"/> when the buffer is too short, the id mismatches, or flags fail the sanity mask.</returns>
        internal static bool TryReadCounts(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            out ushort questionCount,
            out ushort answerCount,
            out ushort authorityCount,
            out ushort additionalCount)
        {
            questionCount = 0;
            answerCount = 0;
            authorityCount = 0;
            additionalCount = 0;

            if (buffer.Length < DnsWireFormat.HeaderSize)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(buffer) != expectedId)
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            if ((flags & ResponseSanityMask) != ResponseSanityValue)
            {
                return false;
            }

            questionCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
            answerCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[6..]);
            authorityCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[8..]);
            additionalCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[10..]);
            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the UDP response header indicates truncation (TC).
        /// </summary>
        /// <param name="buffer">DNS message. Buffers shorter than <see cref="DnsWireFormat.HeaderSize"/> are treated as truncated.</param>
        /// <returns><see langword="true"/> when TC is set or the buffer cannot contain a full header.</returns>
        internal static bool IsTruncated(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < DnsWireFormat.HeaderSize)
            {
                return true;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            return (flags & TruncatedFlag) != 0;
        }

        /// <summary>
        /// Advances past the question section.
        /// </summary>
        /// <param name="span">Full DNS message.</param>
        /// <param name="offset">Start of the question section. On success, the first byte after the last question.</param>
        /// <param name="questionCount">QD count from the header.</param>
        /// <returns><see langword="false"/> when a QNAME or QTYPE/QCLASS does not fit.</returns>
        internal static bool TrySkipQuestions(ReadOnlySpan<byte> span, ref int offset, ushort questionCount)
        {
            for (int q = 0; q < questionCount; q++)
            {
                if (!DnsNameCodec.TrySkipName(span, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormat.QuestionSuffixSize > span.Length)
                {
                    return false;
                }

                offset += DnsWireFormat.QuestionSuffixSize;
            }

            return true;
        }
    }
}
