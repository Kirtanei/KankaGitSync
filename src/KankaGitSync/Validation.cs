using System.Text.Json.Nodes;

namespace KankaGitSync;

public static class Validation
{
    public static IReadOnlyList<string> Check(Snapshot snapshot)
    {
        var errors = new List<string>();
        foreach (var resource in snapshot.Resources.Values)
        {
            CheckMetadata(resource, errors);
            CheckReferences(resource, snapshot, errors);
        }
        return errors;
    }

    public static void Require(Snapshot snapshot)
    {
        var errors = Check(snapshot);
        if (errors.Count != 0) throw new SyncException("Validation failed:\n" + string.Join('\n', errors));
    }

    public static IEnumerable<string> Warnings(Snapshot snapshot) => snapshot.Resources.Values
        .Where(resource => resource.Body.Contains("[entity:", StringComparison.Ordinal) ||
            resource.Kind == "property" && resource.Metadata.Text("value").Contains("[entity:", StringComparison.Ordinal))
        .Select(resource => resource.Id + ": preserved Kanka numeric mention; prefer a local reference where the syntax is supported.");

    private static void CheckMetadata(Resource resource, List<string> errors)
    {
        var fields = resource.Metadata;
        var allowed = resource.Kind switch
        {
            "entity" => new[] { "id", "name", "category", "type", "publish", "deleted", "visibility", "tags", "fields" },
            "property" => ["id", "name", "type", "value", "private", "publish", "deleted"],
            "post" => ["id", "name", "visibility", "publish", "deleted"],
            "relation" => ["id", "target", "relation", "attitude", "visibility", "publish", "deleted"],
            _ => []
        };
        if (fields.Any(field => !allowed.Contains(field.Key))) errors.Add(resource.Id + ": unknown or unmanaged metadata field.");
        foreach (var field in new[] { "publish", "deleted", "private" }.Where(fields.ContainsKey))
            if (fields[field] is not JsonValue value || !value.TryGetValue<bool>(out _)) errors.Add(resource.Id + ": " + field + " must be boolean.");
        CheckKind(resource, errors);
    }

    private static void CheckKind(Resource resource, List<string> errors)
    {
        var fields = resource.Metadata;
        if (resource.Kind == "entity") CheckEntity(resource, errors);
        if (resource.Kind != "relation" && string.IsNullOrWhiteSpace(resource.Name)) errors.Add(resource.Id + ": name required.");
        if (resource.Kind is "post" or "relation" && !Categories.Visibility.Contains(fields.Text("visibility")))
            errors.Add(resource.Id + ": invalid visibility.");
        if (resource.Kind == "property" && !Categories.PropertyTypes.Contains(fields.Text("type"))) errors.Add(resource.Id + ": invalid property type.");
        if (resource.Kind == "relation" && (fields.Number("attitude") is < -100 or > 100 || fields.Text("relation").Length is 0 or > 255))
            errors.Add(resource.Id + ": invalid relation description or attitude.");
    }

    private static void CheckEntity(Resource resource, List<string> errors)
    {
        if (!Categories.Endpoints.ContainsKey(resource.Category)) errors.Add(resource.Id + ": unknown category.");
        if (resource.Metadata["visibility"] is not JsonObject visibility || visibility.Count != 1 ||
            visibility["private"] is not JsonValue privacy || !privacy.TryGetValue<bool>(out _))
            errors.Add(resource.Id + ": visibility.private must be explicitly true or false.");
        if (resource.Metadata["fields"] is JsonObject fields)
        {
            var allowed = resource.Category == "character" ? new[] { "title", "age", "sex", "pronouns" } : [];
            if (fields.Any(field => !allowed.Contains(field.Key) || field.Value is not JsonValue value || !value.TryGetValue<string>(out _)))
                errors.Add(resource.Id + ": unsupported typed field; preserve it in the adapter snapshot.");
        }
    }

    private static void CheckReferences(Resource resource, Snapshot snapshot, List<string> errors)
    {
        var text = resource.Kind == "property" ? resource.Metadata.Text("value") : resource.Body;
        foreach (System.Text.RegularExpressions.Match match in ContentCodec.LocalReference().Matches(text))
            CheckTarget(resource, match.Groups[1].Value, snapshot, errors);
        if (resource.Kind == "relation")
        {
            CheckTarget(resource, resource.Metadata.Text("target"), snapshot, errors);
            if (resource.Owner == resource.Metadata.Text("target")) errors.Add(resource.Id + ": relation cannot target its owner.");
        }
        if (resource.Owner != null && resource.Publish && snapshot.Resources.TryGetValue(resource.Owner, out var owner) &&
            !owner.Publish && !snapshot.Mappings.ContainsKey(resource.Owner)) errors.Add(resource.Id + ": owner is unpublished.");
        if (resource.Metadata["tags"] is JsonArray tags)
            foreach (var tag in tags)
            {
                var identifier = tag?.GetValue<string>() ?? "";
                CheckTarget(resource, identifier, snapshot, errors);
                if (snapshot.Resources.TryGetValue(identifier, out var target) && target.Category != "tag")
                    errors.Add(resource.Id + ": tag reference must target a tag entity.");
            }
    }

    private static void CheckTarget(Resource source, string identifier, Snapshot snapshot, List<string> errors)
    {
        if (!snapshot.Resources.TryGetValue(identifier, out var target) || target.Kind != "entity" || target.Deleted)
            errors.Add(source.Id + ": broken or deleted reference " + identifier + ".");
        else if (source.Publish && !target.Publish && !snapshot.Mappings.ContainsKey(identifier))
            errors.Add(source.Id + ": publication references unpublished content.");
    }
}
