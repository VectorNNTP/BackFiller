using System.Text.Json;
using VectorNNTPD.BackFillerService.Messaging.Cache;

namespace VectorNNTPD.BackFillerService.Tests.Messaging.Cache
{
    /// <summary>
    /// Storage server advertisement JSON v1: UTC timestamp normalization and strict rejection of non-canonical input.
    /// </summary>

    public sealed class StorageServerAdvertisementWireProtocolTests
    {
        /// <summary>
        /// Fixed UTC instant used as the canonical timestamp expectation in round-trip tests.
        /// </summary>

        private static readonly DateTimeOffset SampleUtc = new(2026, 10, 15, 17, 51, 4, 125, TimeSpan.Zero);

        /// <summary>
        /// Fixed offset instant that must normalize to SampleUtc when parsed.
        /// </summary>

        private static readonly DateTimeOffset SampleOffset = new(2026, 10, 15, 19, 51, 4, 125, TimeSpan.FromHours(2));

        /// <summary>
        /// SerializeV1 emits Zulu timestamps and TryParseV1 normalizes offsets to UTC.
        /// </summary>

        [Fact]

        public void A_timestamp_written_by_SerializeV1_parses_as_utc()
        {
            var advertisement = ValidAdvertisement(SampleOffset);
            var payload = StorageServerAdvertisementWireProtocol.SerializeV1(advertisement);
            var json = Encoding.UTF8.GetString(payload);

            Assert.Contains("\"timestamp\":\"2026-10-15T17:51:04.1250000Z\"", json, StringComparison.Ordinal);
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(payload, out var parsed, out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.NotNull(parsed);
            Assert.Equal(SampleOffset.ToUniversalTime(), parsed.Timestamp);
            Assert.Equal(TimeSpan.Zero, parsed.Timestamp.Offset);
            Assert.Equal(advertisement.ServerId, parsed.ServerId);
            Assert.Equal(advertisement.Fqdn, parsed.Fqdn);
            Assert.Equal(advertisement.TotalBytes, parsed.TotalBytes);
            Assert.Equal(advertisement.UsedBytes, parsed.UsedBytes);
            Assert.Equal(advertisement.AvailableBytes, parsed.AvailableBytes);
            Assert.Equal(advertisement.ListenerPort, parsed.ListenerPort);
        }

        /// <summary>
        /// Canonical Zulu timestamp strings parse to the same UTC instant and fields.
        /// </summary>

        [Fact]

        public void A_round_trip_utc_designator_parses_to_the_same_instant()
        {
            var parsed = ParseAccepted("2026-10-15T17:51:04.1250000Z");

            Assert.Equal(SampleUtc, parsed.Timestamp);
            Assert.Equal(TimeSpan.Zero, parsed.Timestamp.Offset);
            Assert.Equal(1190, parsed.ListenerPort);
        }

        /// <summary>
        /// Numeric offset timestamps parse to UTC with zero offset in the model.
        /// </summary>

        [Fact]

        public void A_round_trip_numeric_offset_parses_to_utc()
        {
            var parsed = ParseAccepted("2026-10-15T19:51:04.1250000+02:00");

            Assert.Equal(SampleOffset.ToUniversalTime(), parsed.Timestamp);
            Assert.Equal(TimeSpan.Zero, parsed.Timestamp.Offset);
        }

        /// <summary>
        /// Timestamps outside the accepted ISO-8601 pattern are rejected with a stable reason.
        /// </summary>

        [Theory]
        [InlineData("2026-10-15T17:51:00Z")]
        [InlineData("2026-10-15T17:51:00.000Z")]
        [InlineData("2026-10-15T17:51:00.0000000z")]
        [InlineData("2026-10-15 17:51:00.0000000Z")]
        [InlineData("2026/10/15T17:51:00.0000000Z")]
        [InlineData("2026-10-15T17:51:00,0000000Z")]

        public void A_noncanonical_timestamp_is_rejected(string timestamp)
        {
            AssertRejected(Payload(Quoted(timestamp)), "Advertisement 'timestamp' was not a valid ISO-8601 timestamp.");
        }

        /// <summary>
        /// Payload without timestamp is rejected without mutating input bytes.
        /// </summary>

        [Fact]

        public void A_missing_timestamp_is_rejected()
        {
            const string json = """
                {"version":1,"serverId":7,"fqdn":"cache01.usenet.ninja","totalBytes":100,"usedBytes":40,"availableBytes":60,"listenerPort":1190}
                """;

            AssertRejected(Encoding.UTF8.GetBytes(json), "Advertisement is missing 'timestamp'.");
        }

        /// <summary>
        /// Non-string timestamp JSON values are rejected.
        /// </summary>

        [Theory]
        [InlineData("null")]
        [InlineData("123")]

        public void A_non_string_timestamp_is_rejected(string timestampJson)
        {
            AssertRejected(Payload(timestampJson), "Advertisement 'timestamp' must be an ISO-8601 string.");
        }

        /// <summary>
        /// Malformed timestamp strings are rejected with the ISO-8601 reason.
        /// </summary>

        [Theory]
        [InlineData("\"\"")]
        [InlineData("\" \"")]
        [InlineData("\"not-a-date\"")]

        public void A_malformed_timestamp_string_is_rejected(string timestampJson)
        {
            AssertRejected(Payload(timestampJson), "Advertisement 'timestamp' was not a valid ISO-8601 timestamp.");
        }

        /// <summary>
        /// Invalid JSON is rejected without producing a model instance.
        /// </summary>

        [Fact]

        public void Malformed_json_does_not_produce_an_advertisement()
        {
            AssertRejected("{"u8.ToArray(), "Advertisement payload was not valid JSON.");
        }

        /// <summary>
        /// Asserts TryParseV1 accepts a timestamp variant and returns the advertisement.
        /// </summary>

        private static StorageServerAdvertisement ParseAccepted(string timestamp)
        {
            var payload = Payload(Quoted(timestamp));
            var retained = payload.ToArray();
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(payload, out var advertisement, out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.NotNull(advertisement);
            Assert.Equal(retained, payload);
            return advertisement;
        }

        /// <summary>
        /// Asserts TryParseV1 rejects payload with expected reason and preserves bytes.
        /// </summary>

        private static void AssertRejected(byte[] payload, string expectedReason)
        {
            var retained = payload.ToArray();
            Assert.False(StorageServerAdvertisementWireProtocol.TryParseV1(payload, out var advertisement, out var reason));
            Assert.Null(advertisement);
            Assert.Equal(expectedReason, reason);
            Assert.Equal(retained, payload);
        }

        /// <summary>
        /// Builds a valid advertisement record for serialization tests.
        /// </summary>

        private static StorageServerAdvertisement ValidAdvertisement(DateTimeOffset timestamp) =>
            new(1, 7, "cache01.usenet.ninja", 100, 40, 60, timestamp, 1190);

        /// <summary>
        /// Builds UTF-8 JSON payload bytes with the given timestamp JSON fragment.
        /// </summary>

        private static byte[] Payload(string timestampJson)
        {
            var json = "{\"version\":1,\"serverId\":7,\"fqdn\":\"cache01.usenet.ninja\",\"totalBytes\":100,\"usedBytes\":40,\"availableBytes\":60,\"timestamp\":"
                + timestampJson
                + ",\"listenerPort\":1190}";
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// JSON-serializes a timestamp string for embedding in test payloads.
        /// </summary>

        private static string Quoted(string timestamp) => JsonSerializer.Serialize(timestamp);
    }
}
