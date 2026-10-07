using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Kanka;

/// <summary>Validated Kanka API envelope that keeps resource fields losslessly JSON-backed.</summary>
internal sealed record KankaResponseEnvelope(JsonNode Data, string? NextPage)
{
    public static KankaResponseEnvelope Parse(JsonObject response) => new(
        response["data"] ?? throw new SyncException("API returned no data."),
        response["links"]?["next"]?.GetValue<string>());
}

internal sealed record KankaWriteRequest(JsonObject Payload)
{
    public JsonObject ToJson() => Payload.DeepClone() as JsonObject
        ?? throw new SyncException("Kanka write payload must be an object.");
}
