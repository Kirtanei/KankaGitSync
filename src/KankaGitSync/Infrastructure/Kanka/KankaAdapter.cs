using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Kanka;

public sealed class KankaAdapter(IKankaClient client)
{
    private const int RequestsPerManagedEntity = 1;

    public async Task<Snapshot> FetchAsync(Snapshot previous, CancellationToken cancellationToken,
        Func<FetchProgress, Task>? reportProgress = null)
    {
        var snapshot = new Snapshot { Users = previous.Users.Copy() };
        foreach (var mapping in previous.Mappings) snapshot.Mappings[mapping.Key] = mapping.Value;
        var entities = await client.ListAsync("entities", cancellationToken).ConfigureAwait(false);
        foreach (var entity in entities) RegisterEntity(snapshot, entity);
        var managedEntityIds = snapshot.Mappings.Values.Where(mapping => mapping.Kind == "entity")
            .Select(mapping => mapping.EntityId).ToHashSet();
        var managedEntities = entities.Where(entity => managedEntityIds.Contains(entity.Number("id"))).ToArray();
        var total = 2 + managedEntities.Length * RequestsPerManagedEntity;
        var completed = 1;
        await ReportAsync(reportProgress, completed, total).ConfigureAwait(false);
        foreach (var entity in managedEntities)
            await FetchEntityAsync(snapshot, entity, cancellationToken,
                () => ReportAsync(reportProgress, ++completed, total)).ConfigureAwait(false);
        await ReadMembersAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await ReportAsync(reportProgress, ++completed, total).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<Snapshot> FetchQueuedAsync(Snapshot previous, IReadOnlyList<QueueEvent> events, CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot { Users = previous.Users.Copy() };
        foreach (var pair in previous.Mappings) snapshot.Mappings[pair.Key] = pair.Value;
        foreach (var pair in previous.Resources) snapshot.Resources[pair.Key] = pair.Value;
        foreach (var pair in previous.Raw) snapshot.Raw[pair.Key] = pair.Value.Copy();
        foreach (var group in events.GroupBy(value => (value.Endpoint, value.ChildId)))
        {
            if (group.All(value => value.Event == "deleted")) RemoveEntity(snapshot, group.Key.Endpoint, group.Key.ChildId);
            else await FetchQueuedEntityAsync(snapshot, group.Key.Endpoint, group.Key.ChildId, cancellationToken).ConfigureAwait(false);
        }
        return snapshot;
    }

    private async Task FetchQueuedEntityAsync(Snapshot snapshot, string endpoint, long childId, CancellationToken cancellationToken)
    {
        var category = Categories.Endpoints.SingleOrDefault(pair => pair.Value == endpoint).Key
            ?? throw new SyncException("GitHub webhook queue has an unsupported Kanka endpoint.");
        var raw = await client.GetAsync($"{endpoint}/{childId}?related=1", cancellationToken).ConfigureAwait(false);
        if (PositiveId(raw, "id") != childId || PositiveId(raw, "entity_id") <= 0) throw new SyncException("API entity identity mismatch.");
        var pair = snapshot.Mappings.SingleOrDefault(value => value.Value.Kind == "entity" && value.Value.Category == category && value.Value.ChildId == childId);
        var identifier = pair.Key ?? Canonical.NewId(raw.Text("name"), snapshot.Mappings.Keys);
        var mapping = new Mapping(raw.Number("entity_id"), childId, category, "entity", null);
        RemoveEntity(snapshot, endpoint, childId);
        snapshot.Mappings[identifier] = mapping;
        snapshot.Raw[identifier] = SnapshotRaw(raw);
        snapshot.Resources[identifier] = Import(identifier, mapping, raw, snapshot.Mappings);
        foreach (var kind in new[] { "property", "post", "relation" })
            foreach (var child in RelatedChildren(raw, AttachmentEndpoint(kind)))
                RegisterAttachment(snapshot, identifier, mapping, kind, child);
    }

    private static void RemoveEntity(Snapshot snapshot, string endpoint, long childId)
    {
        var category = Categories.Endpoints.SingleOrDefault(pair => pair.Value == endpoint).Key;
        if (category == null) throw new SyncException("GitHub webhook queue has an unsupported Kanka endpoint.");
        var identifier = snapshot.Mappings.SingleOrDefault(pair => pair.Value.Kind == "entity" && pair.Value.Category == category && pair.Value.ChildId == childId).Key;
        if (identifier == null) return;
        foreach (var resource in snapshot.Mappings.Where(pair => pair.Key == identifier || pair.Value.Owner == identifier).Select(pair => pair.Key).ToArray())
        {
            snapshot.Resources.Remove(resource);
            snapshot.Raw.Remove(resource);
        }
    }

    private static Task ReportAsync(Func<FetchProgress, Task>? reportProgress, int completed, int total) =>
        reportProgress?.Invoke(new FetchProgress(completed, total)) ?? Task.CompletedTask;

    private static void RegisterEntity(Snapshot snapshot, JsonObject entity)
    {
        var entityId = PositiveId(entity, "id");
        var category = EntityCategory(entity);
        if (!Categories.Endpoints.ContainsKey(category))
        {
            snapshot.Raw["unmanaged-" + entityId] = SnapshotRaw(entity);
            return;
        }
        var existing = snapshot.Mappings.SingleOrDefault(pair => pair.Value.Kind == "entity" && pair.Value.EntityId == entityId);
        var identifier = existing.Key ?? Canonical.NewId(entity.Text("name"), snapshot.Mappings.Keys);
        snapshot.Mappings[identifier] = new Mapping(entityId, PositiveId(entity, "child_id"), category, "entity", null);
    }

    private async Task FetchEntityAsync(Snapshot snapshot, JsonObject entity, CancellationToken cancellationToken, Func<Task> completeRequest)
    {
        if (snapshot.Raw.ContainsKey("unmanaged-" + entity.Number("id"))) return;
        var pair = snapshot.Mappings.SingleOrDefault(pair => pair.Value.Kind == "entity" && pair.Value.EntityId == entity.Number("id"));
        if (pair.Key == null) return;
        var mapping = pair.Value;
        var raw = await client.GetAsync($"{Categories.Endpoint(mapping.Category)}/{mapping.ChildId}?related=1", cancellationToken).ConfigureAwait(false);
        await completeRequest().ConfigureAwait(false);
        if (PositiveId(raw, "entity_id") != mapping.EntityId || PositiveId(raw, "id") != mapping.ChildId)
            throw new SyncException("API entity identity mismatch.");
        snapshot.Raw[pair.Key] = SnapshotRaw(raw);
        snapshot.Resources[pair.Key] = Import(pair.Key, mapping, raw, snapshot.Mappings);
        foreach (var kind in new[] { "property", "post", "relation" })
            foreach (var child in RelatedChildren(raw, AttachmentEndpoint(kind)))
                RegisterAttachment(snapshot, pair.Key, mapping, kind, child);
    }

    private static IEnumerable<JsonObject> RelatedChildren(JsonObject raw, string endpoint) =>
        (raw[endpoint] as JsonArray ?? throw new SyncException("API related response is incomplete; fetch stopped without changing Git."))
        .Select(child => child as JsonObject ?? throw new SyncException("API related response contains an invalid resource."));

    private static string EntityCategory(JsonObject entity)
    {
        // Generic entities expose module identity; typed resources use type for freeform text.
        var candidates = new[] { (entity["module"] as JsonObject)?.Text("code"), entity.Text("entity_type_code"),
            entity.Text("entity_type"), entity.Text("type") };
        return candidates.FirstOrDefault(category => !string.IsNullOrWhiteSpace(category))
            ?? throw new SyncException("API entity has no module code; fetch stopped without changing Git. Check API compatibility.");
    }

    private static void RegisterAttachment(Snapshot snapshot, string owner, Mapping parent, string kind, JsonObject raw)
    {
        var childId = PositiveId(raw, "id");
        var existing = snapshot.Mappings.SingleOrDefault(pair => pair.Value.Kind == kind && pair.Value.Owner == owner && pair.Value.ChildId == childId);
        var identifier = existing.Key ?? Canonical.NewId(owner + "-" + raw.Text("name", kind), snapshot.Mappings.Keys);
        var mapping = new Mapping(parent.EntityId, childId, parent.Category, kind, owner);
        snapshot.Mappings[identifier] = mapping;
        snapshot.Raw[identifier] = SnapshotRaw(raw);
        snapshot.Resources[identifier] = Import(identifier, mapping, raw, snapshot.Mappings);
    }

    private async Task ReadMembersAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var member in await client.ListAsync("users", cancellationToken).ConfigureAwait(false))
        {
            var identifier = PositiveId(member, "id").ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!snapshot.Users.ContainsKey(identifier))
                snapshot.Users[identifier] = new JsonObject { ["name"] = member.Text("name"), ["role"] = "player" };
        }
    }

    public static string AttachmentEndpoint(string kind) => kind switch
    {
        "property" => "attributes",
        "post" => "posts",
        "relation" => "relations",
        _ => throw new SyncException("Unknown attachment kind.")
    };

    public static long PositiveId(JsonObject value, string key)
    {
        var identifier = value.Number(key);
        return identifier > 0 ? identifier : throw new SyncException("API resource has no valid identity.");
    }

    internal static JsonObject SnapshotRaw(JsonObject raw)
    {
        var snapshot = raw.Copy();
        RemoveRelationshipSyncMarkers(snapshot);
        return snapshot;
    }

    private static void RemoveRelationshipSyncMarkers(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject fields:
                foreach (var child in fields.Select(pair => pair.Value).ToArray()) RemoveRelationshipSyncMarkers(child);
                if (fields.ContainsKey("data")) fields.Remove("sync");
                break;
            case JsonArray items:
                foreach (var child in items) RemoveRelationshipSyncMarkers(child);
                break;
        }
    }

    public static Resource Import(string identifier, Mapping mapping, JsonObject raw, IReadOnlyDictionary<string, Mapping> mappings)
        => KankaResourceMapper.Import(identifier, mapping, raw, mappings);

    public static JsonObject Payload(Resource resource, IReadOnlyDictionary<string, Mapping> mappings)
        => KankaResourceMapper.Payload(resource, mappings);

    public static string ResourcePath(Mapping mapping) => KankaResourceMapper.ResourcePath(mapping);
}
