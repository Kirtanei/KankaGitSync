using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KankaGitSync.Domain;

public sealed class SyncException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record Resource(string Id, string Kind, string? Owner, JsonObject Metadata, string Body = "")
{
    public bool Publish => Metadata.Flag("publish");
    public bool Deleted => Metadata.Flag("deleted");
    public string Name => Metadata.Text("name");
    public string Category => Metadata.Text("category");
    public string Signature => Canonical.Hash(new JsonObject
    {
        ["metadata"] = Metadata.DeepClone(),
        ["body"] = Body.ReplaceLineEndings("\n").TrimEnd()
    });
}

public sealed record Mapping(long EntityId, long ChildId, string Category, string Kind, string? Owner);

public sealed class Snapshot
{
    public SortedDictionary<string, Resource> Resources { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, Mapping> Mappings { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, JsonObject> Raw { get; } = new(StringComparer.Ordinal);
    public JsonObject Users { get; set; } = [];
}

public static class JsonFields
{
    public static string Text(this JsonObject value, string key, string fallback = "") =>
        value[key]?.GetValue<string>() ?? fallback;
    public static bool Flag(this JsonObject value, string key, bool fallback = false) =>
        value[key]?.GetValue<bool>() ?? fallback;
    public static long Number(this JsonObject value, string key, long fallback = 0) =>
        value[key] is { } number ? Integer(number) : fallback;
    public static long Integer(JsonNode value) => value.Deserialize<long>();
    public static JsonObject Copy(this JsonObject value) => (JsonObject)value.DeepClone();
}

public static partial class Canonical
{
    public static string Json(JsonNode? value) => Sort(value)?.ToJsonString() ?? "null";
    public static string Hash(JsonNode? value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value))));
    public static JsonNode? Sort(JsonNode? value) => value switch
    {
        JsonObject fields => new JsonObject(fields.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => KeyValuePair.Create(field.Key, Sort(field.Value)))),
        JsonArray items => new JsonArray(items.Select(Sort).ToArray()),
        _ => value?.DeepClone()
    };

    public static bool ValidId(string value) => IdPattern().IsMatch(value) && value.Length <= 100;
    public static string NewId(string name, IEnumerable<string> existing)
    {
        var reserved = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slug = SlugPattern().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 70) slug = slug[..70].TrimEnd('-');
        if (slug.Length == 0) slug = "entry";
        var candidate = slug;
        var suffix = 2;
        while (reserved.Contains(candidate))
        {
            candidate = slug + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }
        return candidate;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdPattern();
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SlugPattern();
}

public static class Categories
{
    public static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>
    {
        ["character"] = "characters",
        ["location"] = "locations",
        ["organisation"] = "organisations",
        ["family"] = "families",
        ["item"] = "items",
        ["note"] = "notes",
        ["event"] = "events",
        ["creature"] = "creatures",
        ["race"] = "races",
        ["quest"] = "quests",
        ["journal"] = "journals",
        ["tag"] = "tags",
        ["calendar"] = "calendars",
        ["map"] = "maps",
        ["ability"] = "abilities",
        ["timeline"] = "timelines"
    };
    public static string Endpoint(string category) => Endpoints.TryGetValue(category, out var endpoint)
        ? endpoint : throw new SyncException("Unsupported entity category.");
    public static readonly string[] Visibility = ["all", "self", "admin", "self-admin", "members"];
    public static readonly string[] PropertyTypes = ["text", "paragraph", "checkbox", "section", "random", "number", "choice"];
}
