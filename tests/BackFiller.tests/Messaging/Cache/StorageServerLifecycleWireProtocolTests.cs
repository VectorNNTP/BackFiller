using System.Text.Json;
using VectorNNTP.BackFiller.Messaging.Cache;

namespace VectorNNTP.BackFiller.Tests.Messaging.Cache
{
    /// <summary>
    /// Storage server lifecycle announcement JSON v1: timestamp rules stricter than advertisements and parse contracts.
    /// </summary>

    public sealed class StorageServerLifecycleWireProtocolTests
    {
        /// <summary>
        /// Fixed UTC instant used as the canonical timestamp expectation.
        /// </summary>

        private static readonly DateTimeOffset SampleUtc = new(2026, 10, 15, 17, 51, 4, 125, TimeSpan.Zero);

        /// <summary>
        /// Fixed offset instant that normalizes to UTC in accepted cases.
        /// </summary>

        private static readonly DateTimeOffset SampleOffset = new(2026, 10, 15, 19, 51, 4, 125, TimeSpan.FromHours(2));

        /// <summary>
        /// SerializeV1 emits Zulu timestamps; TryParseV1 normalizes and sets recognized=true.
        /// </summary>

        [Fact]

        public void A_timestamp_written_by_SerializeV1_parses_as_utc()
        {
            var announcement = ValidAnnouncement(SampleOffset.AddTicks(1));
            var payload = StorageServerLifecycleWireProtocol.SerializeV1(announcement);
            var json = Encoding.UTF8.GetString(payload);

            Assert.Contains("\"timestamp\":\"2026-10-15T17:51:04.1250001Z\"", json, StringComparison.Ordinal);
            Assert.True(StorageServerLifecycleWireProtocol.TryParseV1(payload, out var parsed, out var reason, out var recognized));
            Assert.True(recognized);
            Assert.Equal(string.Empty, reason);
            Assert.NotNull(parsed);
            Assert.Equal(announcement.Timestamp.ToUniversalTime(), parsed.Timestamp);
            Assert.Equal(TimeSpan.Zero, parsed.Timestamp.Offset);
            Assert.Equal(announcement.Version, parsed.Version);
            Assert.Equal(announcement.ServerId, parsed.ServerId);
            Assert.Equal(announcement.Fqdn, parsed.Fqdn);
            Assert.Equal(StorageServerLifecycleState.Draining, parsed.State);
            Assert.Equal(announcement.ListenerPort, parsed.ListenerPort);
        }

        /// <summary>
        /// Strict canonical Zulu timestamp parses with Draining state preserved.
        /// </summary>

        [Fact]

        public void A_canonical_utc_z_timestamp_parses_to_the_same_instant()
        {
            var parsed = ParseAccepted("2026-10-15T17:51:04.1250000Z");

            Assert.Equal(SampleUtc, parsed.Timestamp);
            Assert.Equal(TimeSpan.Zero, parsed.Timestamp.Offset);
            Assert.Equal(StorageServerLifecycleState.Draining, parsed.State);
            Assert.Equal(1190, parsed.ListenerPort);
        }

        /// <summary>
        /// Non-canonical timestamp variants are rejected even when advertisements might accept them.
        /// </summary>

        [Theory]
        [InlineData("2026-10-15T17:51:04.1250000+00:00")]
        [InlineData("2026-10-15T19:51:04.1250000+02:00")]
        [InlineData("2026-10-15T17:51:00Z")]
        [InlineData("2026-10-15T17:51:00.000Z")]
        [InlineData("2026-10-15T17:51:04.1250000z")]
        [InlineData("2026-10-15 17:51:04.1250000Z")]
        [InlineData("2026/10/15T17:51:04.1250000Z")]
        [InlineData("2026-10-15T17:51:04,1250000Z")]

        public void A_timestamp_outside_the_serializer_pattern_is_rejected(string timestamp)
        {
            AssertRejected(Payload(Quoted(timestamp)), "Lifecycle announcement 'timestamp' was not a valid ISO-8601 timestamp.");
        }

        /// <summary>
        /// Missing timestamp is rejected with recognized=true.
        /// </summary>

        [Fact]

        public void A_missing_timestamp_is_rejected()
        {
            const string json = """
                {"version":1,"serverId":7,"fqdn":"cache01.usenet.ninja","state":"Draining","listenerPort":1190}
                """;

            AssertRejected(Encoding.UTF8.GetBytes(json), "Lifecycle announcement is missing 'timestamp'.");
        }

        /// <summary>
        /// Non-string timestamp values are rejected.
        /// </summary>

        [Theory]
        [InlineData("null")]
        [InlineData("123")]

        public void A_non_string_timestamp_is_rejected(string timestampJson)
        {
            AssertRejected(Payload(timestampJson), "Lifecycle announcement 'timestamp' must be an ISO-8601 string.");
        }

        /// <summary>
        /// Malformed timestamp strings are rejected.
        /// </summary>

        [Theory]
        [InlineData("\"\"")]
        [InlineData("\" \"")]
        [InlineData("\"not-a-date\"")]

        public void A_malformed_timestamp_string_is_rejected(string timestampJson)
        {
            AssertRejected(Payload(timestampJson), "Lifecycle announcement 'timestamp' was not a valid ISO-8601 timestamp.");
        }

        /// <summary>
        /// Invalid JSON is rejected with recognized=false.
        /// </summary>

        [Fact]

        public void Malformed_json_does_not_produce_an_announcement()
        {
            var payload = "{"u8.ToArray();
            Assert.False(StorageServerLifecycleWireProtocol.TryParseV1(
                payload,
                out var announcement,
                out var reason,
                out var recognized));
            Assert.False(recognized);
            Assert.Null(announcement);
            Assert.Equal("Lifecycle announcement payload was not valid JSON.", reason);
        }

        /// <summary>
        /// Asserts TryParseV1 accepts a timestamp and returns the announcement.
        /// </summary>

        private static StorageServerLifecycleAnnouncement ParseAccepted(string timestamp)
        {
            var payload = Payload(Quoted(timestamp));
            var retained = payload.ToArray();
            Assert.True(StorageServerLifecycleWireProtocol.TryParseV1(payload, out var announcement, out var reason, out var recognized));
            Assert.True(recognized);
            Assert.Equal(string.Empty, reason);
            Assert.NotNull(announcement);
            Assert.Equal(retained, payload);
            return announcement;
        }

        /// <summary>
        /// Asserts TryParseV1 rejects with expected reason and preserves bytes.
        /// </summary>

        private static void AssertRejected(byte[] payload, string expectedReason)
        {
            var retained = payload.ToArray();
            Assert.False(StorageServerLifecycleWireProtocol.TryParseV1(payload, out var announcement, out var reason, out var recognized));
            Assert.True(recognized);
            Assert.Null(announcement);
            Assert.Equal(expectedReason, reason);
            Assert.Equal(retained, payload);
        }

        /// <summary>
        /// Builds a valid lifecycle announcement for serialization tests.
        /// </summary>

        private static StorageServerLifecycleAnnouncement ValidAnnouncement(DateTimeOffset timestamp) =>
            new(1, 7, "cache01.usenet.ninja", StorageServerLifecycleState.Draining, timestamp, 1190);

        /// <summary>
        /// Builds UTF-8 JSON lifecycle payload with the given timestamp JSON fragment.
        /// </summary>

        private static byte[] Payload(string timestampJson)
        {
            var json = "{\"version\":1,\"serverId\":7,\"fqdn\":\"cache01.usenet.ninja\",\"state\":\"Draining\",\"totalBytes\":100,\"timestamp\":"
                + timestampJson
                + ",\"listenerPort\":1190}";
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// JSON-serializes a timestamp string for test payloads.
        /// </summary>

        private static string Quoted(string timestamp) => JsonSerializer.Serialize(timestamp);
    }
}
