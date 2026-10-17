using System.Text.Json;
using System.Text.Json.Serialization;

namespace VectorNNTPD.BackFillerService.Acme.Protocol
{
    /// <summary>
    /// STJ source-generation context for ACME wire DTOs.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSourceGenerationMode.Metadata"/> source-generates the contract.
    /// The fast-path emitter is not used: it casts every property read to the declaring type,
    /// which is redundant when the member is declared on the serialized type.
    /// </remarks>
    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(AcmeDirectoryResource))]
    [JsonSerializable(typeof(AcmeAccountResource))]
    [JsonSerializable(typeof(AcmeOrderResource))]
    [JsonSerializable(typeof(AcmeAuthorizationResource))]
    [JsonSerializable(typeof(AcmeChallengeResource))]
    [JsonSerializable(typeof(AcmeProblem))]
    [JsonSerializable(typeof(AcmeEmptyResource))]
    internal sealed partial class AcmeJsonContext : JsonSerializerContext;
}
