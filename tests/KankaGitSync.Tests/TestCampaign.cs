using System.Text.Json.Nodes;

namespace KankaGitSync.Tests;

internal sealed class TestCampaign : IKankaClient
{
    public Dictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);
    public List<(string Path, JsonObject Body, bool Create)> Writes { get; } = [];
    public List<string> Deletes { get; } = [];
    public int? FailWrite { get; set; }
    public Action<string>? BeforeGet { get; set; }
    public Action<string>? AfterWrite { get; set; }
    public Action<JsonObject>? CustomizeEntity { get; set; }
    public bool ReturnIncompleteEntityCreateResponse { get; set; }
    private long nextIdentifier = 100;

    public TestCampaign()
    {
        Records["characters/1"] = Entity(1, 11, "Maximilian");
        Records["tags/2"] = Entity(2, 22, "Nobility");
        Records["locations/3"] = Entity(3, 33, "Tarant");
        Records["characters/1"]["tags"] = new JsonArray(2);
        Records["characters/1"]["entry"] = "<p>Visit [entity:33|Tarant].</p>";
        Records["characters/1"]["unmanaged"] = new JsonObject { ["secret"] = "preserve me" };
        Records["entities/11/attributes/4"] = new JsonObject
        {
            ["id"] = 4,
            ["entity_id"] = 11,
            ["name"] = "Population",
            ["value"] = "850000",
            ["type_id"] = 6,
            ["is_private"] = false,
            ["updated_by"] = 7
        };
        Records["entities/11/posts/5"] = new JsonObject
        {
            ["id"] = 5,
            ["entity_id"] = 11,
            ["name"] = "Journal",
            ["entry"] = "<p>Player notes.</p>",
            ["visibility_id"] = 2,
            ["updated_by"] = 7,
            ["permissions"] = new JsonArray(new JsonObject { ["user_id"] = 7, ["permission"] = 1 })
        };
        Records["entities/11/relations/6"] = new JsonObject
        {
            ["id"] = 6,
            ["owner_id"] = 11,
            ["target_id"] = 33,
            ["relation"] = "lives in",
            ["visibility_id"] = 1,
            ["attitude"] = 40,
            ["updated_by"] = 9
        };
    }

    public static JsonObject Entity(long child, long entity, string name) => new()
    {
        ["id"] = child,
        ["entity_id"] = entity,
        ["name"] = name,
        ["entry"] = "<p>History.</p>",
        ["type"] = "",
        ["is_private"] = false,
        ["tags"] = new JsonArray(),
        ["updated_by"] = 9,
        ["title"] = "",
        ["age"] = "",
        ["sex"] = "",
        ["pronouns"] = ""
    };

    public Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var includesRelated = path.Contains("?related=1", StringComparison.Ordinal);
        path = path.Split('?')[0];
        BeforeGet?.Invoke(path);
        if (!Records.TryGetValue(path, out var value)) throw new SyncException("Fake resource missing.");
        var result = value.Copy();
        if (includesRelated) AddRelatedResources(result);
        return Task.FromResult(result);
    }

    private void AddRelatedResources(JsonObject entity)
    {
        var entityId = entity.Number("entity_id").ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var endpoint in new[] { "attributes", "posts", "relations" })
            entity[endpoint] = new JsonArray(Records.Where(pair => pair.Key.StartsWith($"entities/{entityId}/{endpoint}/", StringComparison.Ordinal))
                .Select(pair => (JsonNode?)pair.Value.Copy()).ToArray());
    }

    public Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path == "users") return Task.FromResult<IReadOnlyList<JsonObject>>([
            new JsonObject { ["id"] = 7, ["name"] = "Alice" }, new JsonObject { ["id"] = 9, ["name"] = "Jesper" }]);
        if (path != "entities")
            return Task.FromResult<IReadOnlyList<JsonObject>>(Records.Where(pair => pair.Key.StartsWith(path + "/", StringComparison.Ordinal)).Select(pair => pair.Value.Copy()).ToArray());
        var result = Records.Where(pair => !pair.Key.StartsWith("entities/", StringComparison.Ordinal)).Select(pair =>
        {
            var category = Categories.Endpoints.Single(category => category.Value == pair.Key.Split('/')[0]).Key;
            var entity = new JsonObject
            {
                ["id"] = pair.Value.Number("entity_id"),
                ["child_id"] = pair.Value.Number("id"),
                ["module"] = new JsonObject { ["code"] = category },
                ["type"] = category,
                ["type_field"] = pair.Value.Text("type"),
                ["name"] = pair.Value.Text("name")
            };
            CustomizeEntity?.Invoke(entity);
            return entity;
        }).ToArray();
        return Task.FromResult<IReadOnlyList<JsonObject>>(result);
    }

    public Task<JsonObject> WriteAsync(string path, JsonObject body, bool create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add((path, body.Copy(), create));
        if (Writes.Count == FailWrite) throw new SyncException("Simulated network failure.");
        if (create)
        {
            var identifier = nextIdentifier++;
            path += "/" + identifier;
            Records[path] = path.StartsWith("entities/", StringComparison.Ordinal)
                ? new JsonObject { ["id"] = identifier }
                : Entity(identifier, identifier + 1000, body.Text("name"));
        }
        foreach (var pair in body) Records[path][pair.Key] = pair.Value?.DeepClone();
        Records[path]["updated_by"] = 9;
        var result = Records[path].Copy();
        if (create && ReturnIncompleteEntityCreateResponse && !path.StartsWith("entities/", StringComparison.Ordinal))
            result.Remove("entry");
        AfterWrite?.Invoke(path);
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Records.Remove(path)) throw new SyncException("Fake resource missing.");
        Deletes.Add(path);
        return Task.CompletedTask;
    }
}
