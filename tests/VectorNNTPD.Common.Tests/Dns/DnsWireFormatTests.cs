using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Dns;

namespace VectorNNTPD.Common.Tests.Dns
{
    /// <summary>
    /// Deterministic wire-format fixtures for the owned ACME DNS stack (no public DNS required).
    /// </summary>
    public sealed class DnsWireFormatTests
    {
        /// <summary>
        /// Asserts that a TXT query is longer than the DNS header, echoes the generated query id, leaves recursion desired unset, and asks one question.
        /// </summary>
        [Fact]
        public void QueryBuilder_BuildsSingleQuestionTxtQuery()
        {
            byte[] query = DnsQueryBuilder.Build("_acme-challenge.example.com", DnsRecordType.Txt, out ushort queryId);
            Assert.True(query.Length > DnsWireFormat.HeaderSize);
            Assert.Equal(queryId, BinaryPrimitives.ReadUInt16BigEndian(query));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2))); // RD=0 default
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4))); // QDCOUNT
        }

        /// <summary>
        /// Asserts that requesting recursion sets the RD bit and leaves the other header flag bits clear.
        /// </summary>
        [Fact]
        public void QueryBuilder_RecursiveDesired_SetsRdBit()
        {
            byte[] query = DnsQueryBuilder.Build(
                "example.com",
                DnsRecordType.Ns,
                out _,
                recursionDesired: true);
            Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2)));
        }

        /// <summary>
        /// Asserts that a valid ACME name has no validation error, encodes to the computed wire length, and decodes back to the same name.
        /// </summary>
        [Fact]
        public void TryValidateDnsName_ValidName_EncodesAndRoundTrips()
        {
            const string name = "_acme-challenge.example.com";
            Assert.True(DnsWireFormat.TryValidateDnsName(name, out string? error));
            Assert.Null(error);

            int wireLength = DnsWireFormat.ComputeWireNameLength(name);
            Span<byte> buffer = stackalloc byte[wireLength];
            int written = DnsWireFormat.EncodeDnsName(name, buffer);
            Assert.Equal(wireLength, written);

            int offset = 0;
            Assert.True(DnsNameCodec.TryReadDomainName(buffer, ref offset, out string decoded));
            Assert.Equal(name, decoded);
            Assert.Equal(written, offset);
        }

        /// <summary>
        /// Asserts that an empty label, a non-ASCII label, and an empty name fail DNS name validation.
        /// </summary>
        [Fact]
        public void TryValidateDnsName_RejectsEmptyLabelAndNonAscii()
        {
            Assert.False(DnsWireFormat.TryValidateDnsName("example..com", out _));
            Assert.False(DnsWireFormat.TryValidateDnsName("café.example.com", out _));
            Assert.False(DnsWireFormat.TryValidateDnsName(string.Empty, out _));
        }

        /// <summary>
        /// Asserts that a golden TXT response yields the challenge bytes and string, and that a different token does not match.
        /// </summary>
        [Fact]
        public void TxtParser_GoldenAcmeTxtResponse_ParsesAndMatches()
        {
            const string recordName = "_acme-challenge.example.com";
            const string challenge = "abc123-challenge-token";
            byte[] response = BuildGoldenTxtResponse(0x1234, recordName, challenge);
            byte[] expectedBytes = Encoding.ASCII.GetBytes(challenge);

            List<byte[]> parsed = [];
            Assert.True(DnsTxtParser.TryParseTxtRecords(response, 0x1234, parsed));
            Assert.Single(parsed);
            Assert.Equal(expectedBytes, parsed[0]);
            Assert.True(DnsTxtParser.ResponseContainsTxt(response, 0x1234, expectedBytes));
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x1234, "wrong"u8));

            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x1234);
            Assert.Equal([challenge], strings);
        }

        /// <summary>
        /// Asserts that two TXT character-strings in one RDATA are concatenated into a single parsed string.
        /// </summary>
        [Fact]
        public void TxtParser_MultiStringRdata_ConcatenatesSegments()
        {
            const string recordName = "txt.example.com";
            byte[] response = BuildMultiStringTxtResponse(0x42, recordName, "hello", "world");
            List<string> strings = DnsTxtParser.ParseTxtStrings(response, 0x42);
            Assert.Equal(["helloworld"], strings);
        }

        /// <summary>
        /// Asserts that a response with a different transaction id parses no TXT strings and does not match the token.
        /// </summary>
        [Fact]
        public void TxtParser_TransactionIdMismatch_ReturnsEmpty()
        {
            byte[] response = BuildGoldenTxtResponse(0x1111, "example.com", "token");
            Assert.Empty(DnsTxtParser.ParseTxtStrings(response, 0x2222));
            Assert.False(DnsTxtParser.ResponseContainsTxt(response, 0x2222, "token"u8));
        }

        /// <summary>
        /// Asserts that a response forced to NXDOMAIN, with QR still set, parses no TXT strings.
        /// </summary>
        [Fact]
        public void TxtParser_RcodeFailure_ReturnsEmpty()
        {
            byte[] response = BuildGoldenTxtResponse(0x99, "example.com", "token");
            // Force RCODE=3 (NXDOMAIN) while keeping QR.
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8003);
            Assert.Empty(DnsTxtParser.ParseTxtStrings(response, 0x99));
        }

        /// <summary>
        /// Asserts that a truncated header parses no TXT records and that the try-parse API returns <see langword="false"/> instead of throwing.
        /// </summary>
        [Fact]
        public void TxtParser_MalformedPacket_DoesNotThrow()
        {
            byte[] truncated = [0x00, 0x01, 0x80, 0x00];
            Assert.Empty(DnsTxtParser.ParseTxtStrings(truncated, 1));
            Assert.False(DnsTxtParser.TryParseTxtRecords(truncated, 1, []));
        }

        /// <summary>
        /// Asserts that skipping the question name of an <c>example.com</c> TXT query advances 13 bytes past the header.
        /// </summary>
        [Fact]
        public void NameCodec_SkipsQuestionName()
        {
            byte[] query = DnsQueryBuilder.Build("example.com", DnsRecordType.Txt, out _);
            int offset = DnsWireFormat.HeaderSize;
            Assert.True(DnsNameCodec.TrySkipName(query, ref offset));
            Assert.Equal(DnsWireFormat.HeaderSize + 13, offset);
        }

        /// <summary>
        /// Asserts that skipping a compression pointer advances two bytes and does not follow the pointer.
        /// </summary>
        [Fact]
        public void NameCodec_CompressionPointer_AdvancesTwoBytesWithoutFollowing()
        {
            byte[] packet = new byte[DnsWireFormat.HeaderSize + 2];
            packet[12] = 0xC0;
            packet[13] = 0;
            int offset = DnsWireFormat.HeaderSize;
            Assert.True(DnsNameCodec.TrySkipName(packet, ref offset));
            Assert.Equal(DnsWireFormat.HeaderSize + 2, offset);
        }

        /// <summary>
        /// Asserts that reading a compression pointer that targets itself fails instead of looping.
        /// </summary>
        [Fact]
        public void NameCodec_SelfReferentialCompressionPointer_FailsBounded()
        {
            byte[] packet = new byte[DnsWireFormat.HeaderSize + 2];
            packet[12] = 0xC0;
            packet[13] = 12; // pointer to self
            int offset = DnsWireFormat.HeaderSize;
            Assert.False(DnsNameCodec.TryReadDomainName(packet, ref offset, out _));
        }

        /// <summary>
        /// Asserts that TXT lookup keeps only values present in every successful authoritative answer.
        /// </summary>
        /// <returns>A task that completes when the intersection assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_IntersectionSemantics_RequiresAllSuccessfulAnswers()
        {
            var a = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 53);
            var b = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 53);
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([a, b]),
                (address, _, _) =>
                {
                    IReadOnlyList<string> values = address.Equals(a.Address)
                        ? ["shared", "only-a"]
                        : ["shared", "only-b"];
                    return Task.FromResult(values);
                });

            IReadOnlyList<string> values = await resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None);
            Assert.Equal(["shared"], values.OrderBy(static s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// Asserts that lookup returns no TXT strings when every injectable authoritative query throws <see cref="IOException"/>.
        /// </summary>
        /// <returns>A task that completes when the empty-result assertion finishes.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_InjectableEndpoints_EmptyWhenAllFail()
        {
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>(
                    [new IPEndPoint(IPAddress.Parse("203.0.113.1"), 53)]),
                (_, _, _) => throw new IOException("simulated authoritative query failure"));

            IReadOnlyList<string> values = await resolver.LookupTxtAsync(
                "_acme-challenge.example.com",
                CancellationToken.None);
            Assert.Empty(values);
        }

        /// <summary>
        /// Asserts that an empty nameserver list throws <see cref="AcmeChallengeException"/> with category <c>ns_discovery_failed</c>.
        /// </summary>
        /// <returns>A task that completes when the exception category is asserted.</returns>
        [Fact]
        public async Task AuthoritativeTxtResolver_NoNameservers_ThrowsNsDiscoveryFailed()
        {
            var resolver = new AuthoritativeTxtResolver(
                "example.com",
                NullLogger<AuthoritativeTxtResolver>.Instance,
                _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([]));

            var ex = await Assert.ThrowsAsync<AcmeChallengeException>(() =>
                resolver.LookupTxtAsync("_acme-challenge.example.com", CancellationToken.None));
            Assert.Equal("ns_discovery_failed", ex.Category);
        }

        /// <summary>
        /// Builds a QR response with one TXT question and one TXT answer whose RDATA is a single character-string.
        /// </summary>
        /// <param name="queryId">Transaction id written into the header.</param>
        /// <param name="recordName">Uncompressed name copied into the question and answer.</param>
        /// <param name="txtValue">ASCII TXT character-string.</param>
        /// <returns>The encoded DNS response.</returns>
        private static byte[] BuildGoldenTxtResponse(ushort queryId, string recordName, string txtValue)
        {
            byte[] query = DnsQueryBuilder.Build(recordName, DnsRecordType.Txt, out _);
            int qnameLength = query.Length - DnsWireFormat.HeaderSize - DnsWireFormat.QuestionSuffixSize;
            ReadOnlySpan<byte> qname = query.AsSpan(DnsWireFormat.HeaderSize, qnameLength);

            byte[] rdata = new byte[1 + txtValue.Length];
            rdata[0] = (byte)txtValue.Length;
            Encoding.ASCII.GetBytes(txtValue, rdata.AsSpan(1));

            int responseLength = DnsWireFormat.HeaderSize
                + qnameLength
                + DnsWireFormat.QuestionSuffixSize
                + qnameLength
                + 10
                + rdata.Length;

            byte[] response = new byte[responseLength];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), queryId);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8400);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);

            int offset = DnsWireFormat.HeaderSize;
            qname.CopyTo(response.AsSpan(offset));
            offset += qnameLength;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            offset += DnsWireFormat.QuestionSuffixSize;

            qname.CopyTo(response.AsSpan(offset));
            offset += qnameLength;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8), (ushort)rdata.Length);
            offset += 10;
            rdata.CopyTo(response.AsSpan(offset));
            return response;
        }

        /// <summary>
        /// Builds a QR TXT response whose answer RDATA contains two character-strings.
        /// </summary>
        /// <param name="queryId">Transaction id written into the header.</param>
        /// <param name="recordName">Uncompressed name copied into the question and answer.</param>
        /// <param name="part1">First ASCII TXT character-string.</param>
        /// <param name="part2">Second ASCII TXT character-string.</param>
        /// <returns>The encoded DNS response.</returns>
        private static byte[] BuildMultiStringTxtResponse(ushort queryId, string recordName, string part1, string part2)
        {
            byte[] query = DnsQueryBuilder.Build(recordName, DnsRecordType.Txt, out _);
            int qnameLength = query.Length - DnsWireFormat.HeaderSize - DnsWireFormat.QuestionSuffixSize;
            ReadOnlySpan<byte> qname = query.AsSpan(DnsWireFormat.HeaderSize, qnameLength);

            byte[] rdata = new byte[2 + part1.Length + part2.Length];
            rdata[0] = (byte)part1.Length;
            Encoding.ASCII.GetBytes(part1, rdata.AsSpan(1));
            rdata[1 + part1.Length] = (byte)part2.Length;
            Encoding.ASCII.GetBytes(part2, rdata.AsSpan(2 + part1.Length));

            int responseLength = DnsWireFormat.HeaderSize
                + qnameLength
                + DnsWireFormat.QuestionSuffixSize
                + qnameLength
                + 10
                + rdata.Length;
            byte[] response = new byte[responseLength];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), queryId);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8400);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);

            int offset = DnsWireFormat.HeaderSize;
            qname.CopyTo(response.AsSpan(offset));
            offset += qnameLength;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            offset += DnsWireFormat.QuestionSuffixSize;

            qname.CopyTo(response.AsSpan(offset));
            offset += qnameLength;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), DnsRecordType.Txt);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), DnsRecordType.ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8), (ushort)rdata.Length);
            offset += 10;
            rdata.CopyTo(response.AsSpan(offset));
            return response;
        }
    }
}
