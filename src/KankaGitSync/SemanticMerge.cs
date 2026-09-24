using System.Text.Json.Nodes;

namespace KankaGitSync;

public static class SemanticMerge
{
    public static JsonNode? Merge(JsonNode? basis, JsonNode? local, JsonNode? remote, string field = "")
    {
        var localChanged = !JsonNode.DeepEquals(basis, local);
        var remoteChanged = !JsonNode.DeepEquals(basis, remote);
        if (localChanged && remoteChanged && field is "visibility" or "private" or "permissions")
            throw new SyncException("Both sides changed visibility or permissions; explicit review required.");
        if (!localChanged) return remote?.DeepClone();
        if (!remoteChanged) return local?.DeepClone();
        if (basis is JsonObject original && local is JsonObject left && remote is JsonObject right)
            return MergeObject(original, left, right);
        if (JsonNode.DeepEquals(local, remote)) return local?.DeepClone();
        throw new SyncException("Conflicting edits to the same semantic value; explicit review required.");
    }

    private static JsonObject MergeObject(JsonObject basis, JsonObject local, JsonObject remote)
    {
        var result = new JsonObject();
        foreach (var key in basis.Select(pair => pair.Key).Union(local.Select(pair => pair.Key)).Union(remote.Select(pair => pair.Key)))
        {
            var value = Merge(basis[key], local[key], remote[key], key);
            if (value != null || local.ContainsKey(key) && remote.ContainsKey(key)) result[key] = value;
        }
        return result;
    }

    public static void CheckSafety(Snapshot basis, Snapshot local, Snapshot remote)
    {
        foreach (var resource in basis.Resources.Values)
        {
            local.Resources.TryGetValue(resource.Id, out var left);
            remote.Resources.TryGetValue(resource.Id, out var right);
            if (left == null && right != null && right.Signature != resource.Signature ||
                right == null && left != null && left.Signature != resource.Signature)
                throw new SyncException("Delete/edit conflict for " + resource.Id + ". Review before merging.");
            if (left == null || right == null) continue;
            foreach (var field in new[] { "visibility", "private", "permissions" })
                Merge(resource.Metadata[field], left.Metadata[field], right.Metadata[field], field);
        }
    }

    public static async Task ResolveStructuredAsync(GitRepository repository, string basis, string main, string live)
    {
        var original = await repository.ReadTreeAsync(basis).ConfigureAwait(false);
        var local = await repository.ReadTreeAsync(main).ConfigureAwait(false);
        var remote = await repository.ReadTreeAsync(live).ConfigureAwait(false);
        var unresolved = await repository.RequireAsync(["diff", "--name-only", "--diff-filter=U", "-z"]).ConfigureAwait(false);
        foreach (var path in unresolved.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            await TryResolveAsync(repository, path, original, local, remote).ConfigureAwait(false);
    }

    private static async Task TryResolveAsync(GitRepository repository, string path, SortedDictionary<string, string> basis,
        SortedDictionary<string, string> local, SortedDictionary<string, string> remote)
    {
        if (!path.StartsWith("world/", StringComparison.Ordinal) || !basis.TryGetValue(path, out var original) ||
            !local.TryGetValue(path, out var left) || !remote.TryGetValue(path, out var right)) return;
        string merged;
        try { merged = MergeFile(path, original, left, right); }
        catch (SyncException) { return; } // The unresolved Git index retains both versions for human review.
        await File.WriteAllTextAsync(repository.SafePath(path), merged).ConfigureAwait(false);
        await repository.RequireAsync(["add", "--", path]).ConfigureAwait(false);
    }

    public static string MergeFile(string path, string basis, string local, string remote)
    {
        if (path.EndsWith(".yml", StringComparison.Ordinal))
            return YamlCodec.Write((JsonObject)Merge(YamlCodec.Read(basis), YamlCodec.Read(local), YamlCodec.Read(remote))!);
        var original = YamlCodec.ReadMarkdown(basis);
        var left = YamlCodec.ReadMarkdown(local);
        var right = YamlCodec.ReadMarkdown(remote);
        var metadata = (JsonObject)Merge(original.Metadata, left.Metadata, right.Metadata)!;
        var body = Merge(JsonValue.Create(original.Body), JsonValue.Create(left.Body), JsonValue.Create(right.Body))!.GetValue<string>();
        return YamlCodec.WriteMarkdown(new Resource(metadata.Text("id"), "entity", null, metadata, body));
    }
}
