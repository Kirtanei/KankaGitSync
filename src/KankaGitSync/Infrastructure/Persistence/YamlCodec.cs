using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace KankaGitSync.Infrastructure.Persistence;

public static class YamlCodec
{
    private const int MaximumDocumentLength = 16 * 1024 * 1024;

    public static JsonObject Read(string text)
    {
        if (text.Length > MaximumDocumentLength) throw new SyncException("YAML document is too large.");
        RejectAliases(text);
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);
        if (stream.Documents.Count != 1) throw new SyncException("Expected one YAML document.");
        return ConvertNode(stream.Documents[0].RootNode, 0) as JsonObject
            ?? throw new SyncException("Expected a YAML mapping.");
    }

    private static void RejectAliases(string text)
    {
        using var reader = new StringReader(text);
        var parser = new Parser(reader);
        while (parser.MoveNext())
            if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && !node.Anchor.IsEmpty)
                throw new SyncException("YAML anchors and aliases are not supported.");
    }

    private static JsonNode? ConvertNode(YamlNode node, int depth)
    {
        if (depth > 40) throw new SyncException("YAML nesting is too deep.");
        return node switch
        {
            YamlMappingNode mapping => Mapping(mapping, depth),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(child => ConvertNode(child, depth + 1)).ToArray()),
            YamlScalarNode scalar => Scalar(scalar),
            _ => throw new SyncException("Unsupported YAML node.")
        };
    }

    private static JsonObject Mapping(YamlMappingNode mapping, int depth)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: not null } key || result.ContainsKey(key.Value))
                throw new SyncException("YAML keys must be unique strings.");
            result.Add(key.Value, ConvertNode(pair.Value, depth + 1));
        }
        return result;
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);
        if (value is "null" or "~" or "") return null;
        if (bool.TryParse(value, out var boolean)) return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
        return JsonValue.Create(value);
    }

    public static string Write(JsonObject value) => new SerializerBuilder().WithQuotingNecessaryStrings().Build()
        .Serialize(Plain(Canonical.Sort(value))).ReplaceLineEndings("\n");

    private static object? Plain(JsonNode? value) => value switch
    {
        JsonObject fields => fields.ToDictionary(field => field.Key, field => Plain(field.Value)),
        JsonArray items => items.Select(Plain).ToArray(),
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue scalar when scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number => JsonFields.Integer(scalar),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        null => null,
        _ => value.ToString()
    };

    public static (JsonObject Metadata, string Body) ReadMarkdown(string text)
    {
        text = text.ReplaceLineEndings("\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new SyncException("Markdown requires YAML front matter.");
        var boundary = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (boundary < 0) throw new SyncException("Unclosed YAML front matter.");
        return (Read(text[4..boundary]), text[(boundary + 5)..].Trim('\n'));
    }

    public static string WriteMarkdown(Resource resource) => $"---\n{Write(resource.Metadata)}---\n\n{resource.Body.TrimEnd()}\n";
}
