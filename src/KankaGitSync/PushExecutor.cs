using System.Text.Json.Nodes;

namespace KankaGitSync;

public sealed class PushExecutor(IKankaClient client, OperationLedger ledger)
{
    public async Task ApplyAsync(IReadOnlyList<Operation> operations, Snapshot remote, CancellationToken cancellationToken)
    {
        foreach (var operation in operations)
            await ApplyOneAsync(operation, remote, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyOneAsync(Operation operation, Snapshot remote, CancellationToken cancellationToken)
    {
        var resource = operation.Resource;
        if (operation.Action == "delete")
        {
            await DeleteAsync(operation, remote, cancellationToken).ConfigureAwait(false);
            return;
        }
        var create = operation.Action == "create";
        var before = create ? null : await GuardAsync(resource, operation.Action, remote, cancellationToken).ConfigureAwait(false);
        var payload = BuildPayload(operation, remote.Mappings);
        var identifier = Guid.NewGuid().ToString("N");
        ledger.Append(new JsonObject
        {
            ["phase"] = "intent",
            ["operation_id"] = identifier,
            ["action"] = operation.Action,
            ["resource"] = resource.Id,
            ["before_hash"] = Canonical.Hash(before),
            ["planned_hash"] = Canonical.Hash(payload)
        });
        var path = create ? CreatePath(resource, remote.Mappings) : KankaAdapter.ResourcePath(remote.Mappings[resource.Id]);
        var response = await client.WriteAsync(path, payload, create, cancellationToken).ConfigureAwait(false);
        var mapping = create ? CreatedMapping(resource, response, remote.Mappings) : remote.Mappings[resource.Id];
        remote.Mappings[resource.Id] = mapping;
        ledger.Append(new JsonObject
        {
            ["phase"] = "applied",
            ["operation_id"] = identifier,
            ["action"] = operation.Action,
            ["resource"] = resource.Id,
            ["after_hash"] = Canonical.Hash(response),
            ["response_id"] = response.Number("id"),
            ["mapping"] = new JsonObject
            {
                ["entity_id"] = mapping.EntityId,
                ["child_id"] = mapping.ChildId,
                ["category"] = mapping.Category,
                ["kind"] = mapping.Kind,
                ["owner"] = mapping.Owner
            }
        });
        remote.Raw[resource.Id] = KankaAdapter.SnapshotRaw(response);
    }

    private async Task DeleteAsync(Operation operation, Snapshot remote, CancellationToken cancellationToken)
    {
        var resource = operation.Resource;
        var before = await GuardAsync(resource, "delete", remote, cancellationToken).ConfigureAwait(false);
        var identifier = Guid.NewGuid().ToString("N");
        var path = KankaAdapter.ResourcePath(remote.Mappings[resource.Id]);
        ledger.Append(new JsonObject
        {
            ["phase"] = "intent",
            ["operation_id"] = identifier,
            ["action"] = "delete",
            ["resource"] = resource.Id,
            ["before_hash"] = Canonical.Hash(before),
            ["planned_hash"] = Canonical.Hash(null)
        });
        await client.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        ledger.Append(new JsonObject
        {
            ["phase"] = "applied",
            ["operation_id"] = identifier,
            ["action"] = "delete",
            ["resource"] = resource.Id,
            ["before_hash"] = Canonical.Hash(before)
        });
        remote.Resources.Remove(resource.Id);
        remote.Raw.Remove(resource.Id);
    }

    private async Task<JsonObject> GuardAsync(Resource resource, string action, Snapshot remote, CancellationToken cancellationToken)
    {
        var current = await client.GetAsync(KankaAdapter.ResourcePath(remote.Mappings[resource.Id]), cancellationToken).ConfigureAwait(false);
        if (action == "complete")
        {
            RequireUnchangedCreatedShell(resource, current);
            return current;
        }
        if (!remote.Raw.TryGetValue(resource.Id, out var expected) || Canonical.Hash(ConcurrencyState(current)) != Canonical.Hash(ConcurrencyState(expected)))
            throw new SyncException("Kanka changed after planning. Push stopped; refetch and review the remote edit.");
        return current;
    }

    private static void RequireUnchangedCreatedShell(Resource resource, JsonObject current)
    {
        // The create endpoint accepts only these shell fields; GET can supply server defaults for the remaining fields.
        var shellIsUnchanged = current.Text("name") == resource.Name &&
            current.Text("type") == resource.Metadata.Text("type") &&
            current.Flag("is_private", true);
        if (!shellIsUnchanged)
            throw new SyncException("Kanka changed after planning. Push stopped; refetch and review the remote edit.");
    }

    public static JsonObject ConcurrencyState(JsonObject raw)
    {
        var result = raw.Copy();
        // Related collections have their own guards; parsed HTML and image URLs are generated views.
        foreach (var field in new[] { "attributes", "posts", "relations", "entity_events", "entity_files", "entity_abilities", "entity_links",
                     "entry_parsed", "image_full", "image_thumb", "urls" }) result.Remove(field);
        return result;
    }

    private static string CreatePath(Resource resource, IReadOnlyDictionary<string, Mapping> mappings) => resource.Kind == "entity"
        ? Categories.Endpoint(resource.Category)
        : $"entities/{mappings[resource.Owner!].EntityId}/{KankaAdapter.AttachmentEndpoint(resource.Kind)}";

    private static Mapping CreatedMapping(Resource resource, JsonObject response, IReadOnlyDictionary<string, Mapping> mappings) =>
        resource.Kind == "entity"
            ? new Mapping(KankaAdapter.PositiveId(response, "entity_id"), KankaAdapter.PositiveId(response, "id"), resource.Category, "entity", null)
            : new Mapping(mappings[resource.Owner!].EntityId, KankaAdapter.PositiveId(response, "id"), mappings[resource.Owner!].Category, resource.Kind, resource.Owner);

    private static JsonObject BuildPayload(Operation operation, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var resource = operation.Resource;
        if (operation.Action == "create" && resource.Kind == "entity")
            return new JsonObject { ["name"] = resource.Name, ["type"] = resource.Metadata.Text("type"), ["is_private"] = true };
        var payload = KankaAdapter.Payload(resource, mappings);
        if (operation.Action == "update")
            foreach (var key in payload.Select(pair => pair.Key).Except(operation.Fields).ToArray()) payload.Remove(key);
        if (resource.Kind == "property") payload["name"] = resource.Name;
        if (operation.Action == "create" && resource.Owner != null)
            payload[resource.Kind == "relation" ? "owner_id" : "entity_id"] = mappings[resource.Owner].EntityId;
        return payload;
    }
}
