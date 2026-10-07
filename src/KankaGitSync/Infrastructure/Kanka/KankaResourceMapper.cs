using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Kanka;

internal static class KankaResourceMapper
{
    public static Resource Import(string identifier, Mapping mapping, JsonObject raw, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var metadata = new JsonObject { ["id"] = identifier, ["publish"] = true };
        switch (mapping.Kind)
        {
            case "entity": ImportEntity(metadata, mapping, raw, mappings); break;
            case "property": ImportProperty(metadata, raw); metadata["value"] = ContentCodec.ImportReferences(metadata.Text("value"), mappings); break;
            case "post": metadata["name"] = raw.Text("name"); metadata["visibility"] = VisibilityName(raw); break;
            case "relation": ImportRelation(metadata, raw, mappings); break;
            default: throw new SyncException("Unknown resource kind.");
        }
        var body = mapping.Kind is "entity" or "post" ? ContentCodec.Import(raw.Text("entry"), mappings) : "";
        return new Resource(identifier, mapping.Kind, mapping.Owner, metadata, body);
    }

    public static JsonObject Payload(Resource resource, IReadOnlyDictionary<string, Mapping> mappings) => resource.Kind switch
    {
        "entity" => EntityPayload(resource, mappings),
        "post" => new JsonObject { ["name"] = resource.Name, ["entry"] = ContentCodec.Export(resource.Body, mappings), ["visibility_id"] = Array.IndexOf(Categories.Visibility, resource.Metadata.Text("visibility")) + 1 },
        "property" => new JsonObject { ["name"] = resource.Name, ["value"] = PropertyValue(resource, mappings), ["type_id"] = Array.IndexOf(Categories.PropertyTypes, resource.Metadata.Text("type")) + 1, ["is_private"] = resource.Metadata.Flag("private", true) },
        "relation" => new JsonObject { ["relation"] = resource.Metadata.Text("relation"), ["attitude"] = resource.Metadata.Number("attitude"), ["target_id"] = mappings[resource.Metadata.Text("target")].EntityId, ["visibility_id"] = Array.IndexOf(Categories.Visibility, resource.Metadata.Text("visibility")) + 1 },
        _ => throw new SyncException("Unknown resource kind.")
    };

    public static string ResourcePath(Mapping mapping) => mapping.Kind == "entity" ? $"{Categories.Endpoint(mapping.Category)}/{mapping.ChildId}" : $"entities/{mapping.EntityId}/{KankaAdapter.AttachmentEndpoint(mapping.Kind)}/{mapping.ChildId}";

    private static void ImportEntity(JsonObject metadata, Mapping mapping, JsonObject raw, IReadOnlyDictionary<string, Mapping> mappings)
    {
        metadata["name"] = raw.Text("name"); metadata["category"] = mapping.Category; metadata["type"] = raw.Text("type"); metadata["visibility"] = new JsonObject { ["private"] = raw.Flag("is_private", true) };
        var tags = (raw["tags"] as JsonArray ?? []).Select(tag => tag is JsonObject value ? value.Number("id") : JsonFields.Integer(tag!));
        var localTags = tags.Select(tagId => mappings.SingleOrDefault(pair => pair.Value.Category == "tag" && pair.Value.Kind == "entity" && pair.Value.ChildId == tagId).Key ?? throw new SyncException("Tag is outside the imported campaign; cannot safely map it."));
        metadata["tags"] = new JsonArray(localTags.Order(StringComparer.Ordinal).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
        var fields = new JsonObject();
        if (mapping.Category == "character") foreach (var field in new[] { "title", "age", "sex", "pronouns" }) fields[field] = raw.Text(field);
        metadata["fields"] = fields;
    }

    private static void ImportProperty(JsonObject metadata, JsonObject raw)
    {
        metadata["name"] = raw.Text("name"); var type = raw.Number("type_id", 1);
        if (type < 1 || type > Categories.PropertyTypes.Length) throw new SyncException("Unsupported property type; import stopped without changing Git.");
        metadata["type"] = Categories.PropertyTypes[type - 1]; metadata["value"] = PropertyText(raw["value"]); metadata["private"] = raw.Flag("is_private", true);
    }

    private static string PropertyText(JsonNode? value) => value?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => "",
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Number => value.ToJsonString(),
        _ => throw new SyncException("API property value must be a scalar; fetch stopped without changing Git.")
    };

    private static JsonNode PropertyValue(Resource resource, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var value = ContentCodec.ExportReferences(resource.Metadata.Text("value"), mappings);
        if (resource.Metadata.Text("type") != "checkbox") return JsonValue.Create(value);
        return value.ToLowerInvariant() switch { "true" or "1" => JsonValue.Create(true), "false" or "0" or "" => JsonValue.Create(false), _ => throw new SyncException("Checkbox value must be true, false, 1, or 0.") };
    }

    private static void ImportRelation(JsonObject metadata, JsonObject raw, IReadOnlyDictionary<string, Mapping> mappings)
    {
        metadata["relation"] = raw.Text("relation"); metadata["target"] = mappings.SingleOrDefault(pair => pair.Value.Kind == "entity" && pair.Value.EntityId == raw.Number("target_id")).Key ?? throw new SyncException("Relation target is not visible or unsupported; import stopped without changing Git.");
        metadata["attitude"] = raw.Number("attitude"); metadata["visibility"] = VisibilityName(raw);
    }

    private static string VisibilityName(JsonObject raw)
    {
        var visibility = raw.Number("visibility_id");
        return visibility is < 1 or > 5 ? throw new SyncException("Unknown remote visibility.") : Categories.Visibility[visibility - 1];
    }

    private static JsonObject EntityPayload(Resource resource, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var metadata = resource.Metadata;
        var result = new JsonObject { ["name"] = resource.Name, ["type"] = metadata.Text("type"), ["entry"] = ContentCodec.Export(resource.Body, mappings), ["is_private"] = (metadata["visibility"] as JsonObject)?.Flag("private", true) ?? true };
        result["tags"] = new JsonArray((metadata["tags"] as JsonArray ?? []).Select(tag => (JsonNode?)JsonValue.Create(mappings[tag!.GetValue<string>()].ChildId)).ToArray());
        if (metadata["fields"] is JsonObject fields) foreach (var field in fields) result[field.Key] = field.Value?.DeepClone();
        return result;
    }
}
