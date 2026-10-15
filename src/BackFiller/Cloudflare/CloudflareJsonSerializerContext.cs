using System.Text.Json.Serialization;

namespace VectorNNTP.BackFiller.Cloudflare
{
    /// <summary>
    /// Source-generated JSON metadata for Cloudflare DNS API request/response envelopes.
    /// </summary>
    /// <remarks>
    /// Mirrors the historical <see cref="CloudflareDnsClient"/> options:
    /// case-insensitive property names on read, and omit nulls when writing request bodies.
    /// Closed generic response types cover every <c>T</c> used by the client.
    /// <see cref="JsonSourceGenerationMode.Metadata"/> keeps that contract source-generated.
    /// The fast-path emitter is not used: it casts every property read to the declaring type,
    /// which is redundant when the member is declared on the serialized type.
    /// </remarks>
    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareDnsRecord>>))]
    [JsonSerializable(typeof(CloudflareApiResponse<CloudflareDnsRecord>))]
    [JsonSerializable(typeof(CloudflareApiResponse<CloudflareDeleteResult>))]
    [JsonSerializable(typeof(CloudflareDnsRecordWriteRequest))]
    internal partial class CloudflareJsonSerializerContext : JsonSerializerContext;

    /// <summary>Minimal delete-result payload (<c>result.id</c>) when Cloudflare returns one.</summary>
    internal sealed class CloudflareDeleteResult
    {
        /// <summary>Gets or sets <c>result.id</c> when Cloudflare returns it. Null when the field is absent. Delete success does not require this value.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
