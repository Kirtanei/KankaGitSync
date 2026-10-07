using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Persistence;

public static class WorldFiles
{
    public static SortedDictionary<string, string> Write(Snapshot snapshot)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var mappings = new JsonObject();
        foreach (var pair in snapshot.Mappings)
            mappings[pair.Key] = IdMappingDocument.FromMapping(pair.Value).ToJson();
        files[".kanka/ids.yml"] = YamlCodec.Write(mappings);
        files[".kanka/users.yml"] = YamlCodec.Write(snapshot.Users);
        foreach (var resource in snapshot.Resources.Values)
            WriteResource(files, resource, snapshot.Resources);
        foreach (var pair in snapshot.Raw)
            files[$".kanka/remote/{pair.Key}.json"] = Canonical.Json(pair.Value) + "\n";
        return files;
    }

    public static string EntityDirectory(Resource entity) => $"world/{Categories.Endpoint(entity.Category)}/{entity.Id}";

    private static void WriteResource(SortedDictionary<string, string> files, Resource resource, IReadOnlyDictionary<string, Resource> resources)
    {
        if (resource.Kind == "entity")
        {
            files[EntityDirectory(resource) + "/index.md"] = YamlCodec.WriteMarkdown(resource);
            return;
        }
        var directory = EntityDirectory(resources[resource.Owner ?? throw new SyncException("Missing resource owner.")]);
        if (resource.Kind == "post")
        {
            files[$"{directory}/posts/{resource.Id}.md"] = YamlCodec.WriteMarkdown(resource);
            return;
        }
        var path = directory + (resource.Kind == "property" ? "/properties.yml" : "/relations.yml");
        var entries = files.TryGetValue(path, out var text) ? YamlCodec.Read(text) : new JsonObject();
        entries[resource.Id] = resource.Metadata.DeepClone();
        files[path] = YamlCodec.Write(entries);
    }

    public static Snapshot Read(IReadOnlyDictionary<string, string> files)
    {
        var snapshot = new Snapshot();
        ReadAdapter(files, snapshot);
        var directories = ReadEntities(files, snapshot);
        foreach (var pair in files.Where(pair => pair.Key.StartsWith("world/", StringComparison.Ordinal) && !pair.Key.EndsWith("/index.md", StringComparison.Ordinal)))
            ReadAttachment(pair, directories, snapshot);
        return snapshot;
    }

    private static Dictionary<string, string> ReadEntities(IReadOnlyDictionary<string, string> files, Snapshot snapshot)
    {
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in files.Where(pair => pair.Key.StartsWith("world/", StringComparison.Ordinal) && pair.Key.EndsWith("/index.md", StringComparison.Ordinal)))
        {
            var (metadata, body) = YamlCodec.ReadMarkdown(pair.Value);
            var identifier = metadata.Text("id");
            Add(snapshot, new Resource(identifier, "entity", null, metadata, body));
            directories[pair.Key[..^9]] = identifier;
        }
        return directories;
    }

    private static void ReadAttachment(KeyValuePair<string, string> pair, Dictionary<string, string> directories, Snapshot snapshot)
    {
        var parent = directories.Keys.SingleOrDefault(directory => pair.Key.StartsWith(directory + "/", StringComparison.Ordinal));
        if (parent == null) throw new SyncException("World file has no owning index.md.");
        var relative = pair.Key[(parent.Length + 1)..];
        if (relative.StartsWith("posts/", StringComparison.Ordinal) && relative.EndsWith(".md", StringComparison.Ordinal))
        {
            var (metadata, body) = YamlCodec.ReadMarkdown(pair.Value);
            Add(snapshot, new Resource(metadata.Text("id"), "post", directories[parent], metadata, body));
            return;
        }
        if (relative is not ("properties.yml" or "relations.yml")) throw new SyncException("Unknown world file; move local-only material to gm/ or drafts/.");
        foreach (var field in YamlCodec.Read(pair.Value))
        {
            var metadata = field.Value as JsonObject ?? throw new SyncException("Resource must be a YAML mapping.");
            if (metadata.ContainsKey("id") && metadata.Text("id") != field.Key) throw new SyncException("Resource key and id disagree.");
            metadata["id"] = field.Key;
            if (relative == "properties.yml" && metadata["value"] is JsonValue value && value.GetValueKind() != System.Text.Json.JsonValueKind.String)
                metadata["value"] = value.ToJsonString();
            Add(snapshot, new Resource(field.Key, relative == "properties.yml" ? "property" : "relation", directories[parent], metadata));
        }
    }

    private static void ReadAdapter(IReadOnlyDictionary<string, string> files, Snapshot snapshot)
    {
        if (files.TryGetValue(".kanka/ids.yml", out var mappingText))
            foreach (var pair in YamlCodec.Read(mappingText))
            {
                var value = pair.Value as JsonObject ?? throw new SyncException("Invalid ID mapping.");
                if (!Canonical.ValidId(pair.Key)) throw new SyncException("Invalid local ID mapping.");
                snapshot.Mappings[pair.Key] = IdMappingDocument.FromJson(value).ToMapping();
            }
        if (files.TryGetValue(".kanka/users.yml", out var users)) snapshot.Users = YamlCodec.Read(users);
        foreach (var pair in files.Where(pair => pair.Key.StartsWith(".kanka/remote/", StringComparison.Ordinal)))
            snapshot.Raw[Path.GetFileNameWithoutExtension(pair.Key)] = JsonNode.Parse(pair.Value) as JsonObject
                ?? throw new SyncException("Invalid remote snapshot.");
    }

    private static void Add(Snapshot snapshot, Resource resource)
    {
        if (!Canonical.ValidId(resource.Id)) throw new SyncException("Resource IDs must be lowercase words separated by hyphens, at most 100 characters.");
        if (!snapshot.Resources.TryAdd(resource.Id, resource)) throw new SyncException("Duplicate local resource ID.");
    }

    public static SortedDictionary<string, string> ReadWorkingTree(GitRepository repository)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in new[] { "world", ".kanka" })
        {
            var path = repository.SafePath(directory);
            if (Directory.Exists(path)) Walk(repository, path, files);
        }
        return files;
    }

    private static void Walk(GitRepository repository, string directory, SortedDictionary<string, string> files)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var relative = Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
            if (relative == ".kanka/runtime") continue;
            repository.SafePath(relative);
            if (Directory.Exists(path)) Walk(repository, path, files);
            else files[relative] = File.ReadAllText(path);
        }
    }
}
