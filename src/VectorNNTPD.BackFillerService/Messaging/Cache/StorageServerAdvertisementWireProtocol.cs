using System.Text.Encodings.Web;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Messaging.Cache
{
    /// <summary>
    /// Canonical compact System.Text.Json contract for StorageServer cache-fleet advertisement v1 bodies.
    /// </summary>
    /// <remarks>
    /// Property names are <c>version</c>, <c>serverId</c>, <c>fqdn</c>, <c>totalBytes</c>,
    /// <c>usedBytes</c>, <c>availableBytes</c>, and <c>timestamp</c> (ISO-8601 round-trip format <c>O</c>, stored as UTC).
    /// Optional <c>listenerPort</c> (<c>1</c>–<c>65535</c>) is written when present. Unknown
    /// properties are ignored. A missing <c>listenerPort</c> parses as no placement port.
    /// AMQP <c>Expiration</c>, <c>AppId</c>, and <c>MessageId</c> are never JSON fields.
    /// Serialization writes compact UTF-8 with no indentation.
    /// </remarks>
    internal static class StorageServerAdvertisementWireProtocol
    {
        /// <summary>Current application protocol version.</summary>
        private const int CurrentVersion = 1;

        /// <summary>AMQP <c>ContentType</c> for advertisement bodies.</summary>
        private const string JsonContentType = "application/json";

        /// <summary>Serializes a version-1 advertisement into compact UTF-8 JSON.</summary>
        /// <param name="advertisement">Concrete valid v1 advertisement.</param>
        /// <returns>
        /// Compact UTF-8 JSON for the required advertisement properties, plus <c>listenerPort</c> when
        /// <see cref="StorageServerAdvertisement.ListenerPort"/> has a value.
        /// </returns>
        internal static byte[] SerializeV1(StorageServerAdvertisement advertisement)
        {
            ArgumentNullException.ThrowIfNull(advertisement);
            if (advertisement.Version != CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported StorageServer advertisement version '{advertisement.Version}'.");
            }

            if (advertisement.ServerId <= 0)
            {
                throw new InvalidOperationException("StorageServer advertisement requires a positive serverId.");
            }

            if (string.IsNullOrWhiteSpace(advertisement.Fqdn))
            {
                throw new InvalidOperationException("StorageServer advertisement requires a non-empty fqdn.");
            }

            if (advertisement.TotalBytes < 0
                || advertisement.UsedBytes < 0
                || advertisement.AvailableBytes < 0)
            {
                throw new InvalidOperationException("StorageServer advertisement byte counts must be non-negative.");
            }

            if (advertisement.ListenerPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("StorageServer advertisement listenerPort must be in the range 1–65535.");
            }

            var writer = new ArrayBufferWriter<byte>();
            using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", advertisement.Version);
            jsonWriter.WriteNumber("serverId", advertisement.ServerId);
            jsonWriter.WriteString("fqdn", advertisement.Fqdn);
            jsonWriter.WriteNumber("totalBytes", advertisement.TotalBytes);
            jsonWriter.WriteNumber("usedBytes", advertisement.UsedBytes);
            jsonWriter.WriteNumber("availableBytes", advertisement.AvailableBytes);
            jsonWriter.WriteString("timestamp", advertisement.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            if (advertisement.ListenerPort is int listenerPort)
            {
                jsonWriter.WriteNumber("listenerPort", listenerPort);
            }

            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Attempts to parse and validate a version-1 advertisement payload.</summary>
        /// <param name="payload">UTF-8 JSON body.</param>
        /// <param name="advertisement">Parsed advertisement when validation succeeds.</param>
        /// <param name="reason">Rejection reason when parsing fails.</param>
        /// <returns><see langword="true"/> when the payload is a valid v1 advertisement.</returns>
        internal static bool TryParseV1(
            ReadOnlySpan<byte> payload,
            out StorageServerAdvertisement? advertisement,
            out string reason)
        {
            advertisement = null;
            reason = "Advertisement payload is empty.";
            if (payload.IsEmpty)
            {
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.ToArray());
            }
            catch (JsonException)
            {
                reason = "Advertisement payload was not valid JSON.";
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    reason = "Advertisement payload root must be a JSON object.";
                    return false;
                }

                var root = document.RootElement;
                if (!TryReadInt32(root, "version", out var version, out reason)
                    || version != CurrentVersion)
                {
                    reason = version == 0 && reason.Length > 0
                        ? reason
                        : $"Unsupported StorageServer advertisement version '{version}'.";
                    return false;
                }

                if (!TryReadInt32(root, "serverId", out var serverId, out reason) || serverId <= 0)
                {
                    reason = serverId <= 0 && string.IsNullOrEmpty(reason)
                        ? "Advertisement serverId must be a positive integer."
                        : reason;
                    return false;
                }

                if (!TryReadString(root, "fqdn", out var fqdn, out reason) || string.IsNullOrWhiteSpace(fqdn))
                {
                    reason = "Advertisement fqdn is required.";
                    return false;
                }

                if (!TryReadInt64(root, "totalBytes", out var totalBytes, out reason) || totalBytes < 0
                    || !TryReadInt64(root, "usedBytes", out var usedBytes, out reason) || usedBytes < 0
                    || !TryReadInt64(root, "availableBytes", out var availableBytes, out reason) || availableBytes < 0)
                {
                    reason = "Advertisement byte counts must be non-negative integers.";
                    return false;
                }

                if (!TryReadTimestamp(root, "timestamp", out var timestamp, out reason))
                {
                    return false;
                }

                int? listenerPort = null;
                if (root.TryGetProperty("listenerPort", out var portProperty))
                {
                    if (portProperty.ValueKind != JsonValueKind.Number || !portProperty.TryGetInt32(out var parsedPort))
                    {
                        reason = "Advertisement listenerPort must be an integer.";
                        return false;
                    }

                    if (parsedPort is < 1 or > 65535)
                    {
                        reason = "Advertisement listenerPort must be in the range 1–65535.";
                        return false;
                    }

                    listenerPort = parsedPort;
                }

                advertisement = new StorageServerAdvertisement(
                    version,
                    serverId,
                    fqdn.Trim(),
                    totalBytes,
                    usedBytes,
                    availableBytes,
                    timestamp,
                    listenerPort);
                reason = string.Empty;
                return true;
            }
        }

        /// <summary>Reads <paramref name="name"/> as a JSON number that fits in <see cref="int"/>.</summary>
        /// <param name="root">Advertisement object.</param>
        /// <param name="name">Required property name.</param>
        /// <param name="value">Parsed integer on success; 0 on failure.</param>
        /// <param name="reason">Empty on success; otherwise an advertisement missing-property or type error.</param>
        /// <returns><see langword="false"/> when the property is absent or not an <see cref="int"/> JSON number.</returns>
        private static bool TryReadInt32(JsonElement root, string name, out int value, out string reason)
        {
            value = 0;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Advertisement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
            {
                reason = $"Advertisement '{name}' must be an integer.";
                return false;
            }

            return true;
        }

        /// <summary>Reads a byte-count property as a JSON number that fits in <see cref="long"/>.</summary>
        /// <param name="root">Advertisement object.</param>
        /// <param name="name">Required property name, such as <c>totalBytes</c>.</param>
        /// <param name="value">Parsed integer on success; 0 on failure.</param>
        /// <param name="reason">Empty on success; otherwise an advertisement missing-property or type error.</param>
        /// <returns><see langword="false"/> when the property is absent or not a <see cref="long"/> JSON number.</returns>
        private static bool TryReadInt64(JsonElement root, string name, out long value, out string reason)
        {
            value = 0;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Advertisement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out value))
            {
                reason = $"Advertisement '{name}' must be an integer.";
                return false;
            }

            return true;
        }

        /// <summary>Reads <paramref name="name"/> as a JSON string. A JSON null string becomes empty.</summary>
        /// <param name="root">Advertisement object.</param>
        /// <param name="name">Required property name.</param>
        /// <param name="value">Property text on success; empty on failure.</param>
        /// <param name="reason">Empty on success; otherwise an advertisement missing-property or type error.</param>
        /// <returns><see langword="false"/> when the property is absent or not a string.</returns>
        private static bool TryReadString(JsonElement root, string name, out string value, out string reason)
        {
            value = string.Empty;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Advertisement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Advertisement '{name}' must be a string.";
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }

        /// <summary>Reads <paramref name="name"/> as an exact round-trip (<c>O</c>) ISO-8601 timestamp and converts it to UTC.</summary>
        /// <param name="root">Advertisement object.</param>
        /// <param name="name">Required property name, normally <c>timestamp</c>.</param>
        /// <param name="value">UTC timestamp on success; otherwise the default.</param>
        /// <param name="reason">Empty on success; otherwise a missing, non-string, or unparseable timestamp error.</param>
        /// <returns>
        /// <see langword="true"/> when <see cref="DateTimeOffset.TryParseExact(string?, string?, IFormatProvider?, DateTimeStyles, out DateTimeOffset)"/>
        /// accepts <paramref name="name"/> with format <c>O</c>, <see cref="CultureInfo.InvariantCulture"/>, and <see cref="DateTimeStyles.RoundtripKind"/>.
        /// </returns>
        private static bool TryReadTimestamp(
            JsonElement root,
            string name,
            out DateTimeOffset value,
            out string reason)
        {
            value = default;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Advertisement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Advertisement '{name}' must be an ISO-8601 string.";
                return false;
            }

            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text)
                || !DateTimeOffset.TryParseExact(
                    text,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out value))
            {
                value = default;
                reason = $"Advertisement '{name}' was not a valid ISO-8601 timestamp.";
                return false;
            }

            value = value.ToUniversalTime();
            return true;
        }
    }
}
