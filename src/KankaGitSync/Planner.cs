using System.Text.Json.Nodes;

namespace KankaGitSync;

public sealed record Operation(Resource Resource, string Action, string[] Fields, bool PrivacyChange);

public static class Planner
{
    public static IReadOnlyList<Operation> Build(Snapshot local, Snapshot remote)
    {
        Validation.Require(local);
        var operations = new List<Operation>();
        var mappings = PlaceholderMappings(local, remote);
        foreach (var resource in local.Resources.Values.Where(resource => resource.Publish))
            PlanResource(resource, remote, mappings, operations);
        return operations.OrderBy(operation => Priority(operation)).ThenBy(operation => operation.Resource.Id, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, Mapping> PlaceholderMappings(Snapshot local, Snapshot remote)
    {
        var mappings = remote.Mappings.ToDictionary();
        long placeholder = long.MaxValue;
        foreach (var resource in local.Resources.Values.Where(resource => resource.Kind == "entity" && !mappings.ContainsKey(resource.Id)))
        {
            mappings[resource.Id] = new Mapping(placeholder, placeholder, resource.Category, "entity", null);
            placeholder--;
        }
        return mappings;
    }

    private static void PlanResource(Resource resource, Snapshot remote, Dictionary<string, Mapping> mappings, List<Operation> operations)
    {
        if (resource.Deleted)
        {
            if (remote.Resources.ContainsKey(resource.Id)) operations.Add(new Operation(resource, "delete", [], false));
            return;
        }
        if (!remote.Resources.TryGetValue(resource.Id, out var previous))
        {
            if (remote.Mappings.ContainsKey(resource.Id)) throw new SyncException("A previously mapped resource is missing remotely. Review the deletion; use a new local ID for recreation.");
            operations.Add(new Operation(resource, "create", [], true));
            if (resource.Kind == "entity") operations.Add(new Operation(resource, "complete", [], true));
            return;
        }
        if (resource.Kind != previous.Kind || resource.Owner != previous.Owner || resource.Category != previous.Category)
            throw new SyncException("Resource identity, owner, and category cannot be changed.");
        var before = KankaAdapter.Payload(previous, mappings);
        var after = KankaAdapter.Payload(resource, mappings);
        var changed = after.Where(pair => !Equivalent(pair.Key, pair.Value, before[pair.Key])).Select(pair => pair.Key).ToArray();
        if (changed.Length != 0)
            operations.Add(new Operation(resource, "update", changed, changed.Contains("is_private") || changed.Contains("visibility_id")));
    }

    public static bool Equivalent(string field, JsonNode? left, JsonNode? right) => field == "entry"
        ? ContentCodec.NormalizeHtml(left?.GetValue<string>() ?? "") == ContentCodec.NormalizeHtml(right?.GetValue<string>() ?? "")
        : JsonNode.DeepEquals(Canonical.Sort(left), Canonical.Sort(right));

    private static int Priority(Operation operation) => operation.Action switch
    {
        "create" when operation.Resource.Kind == "entity" => 0,
        "complete" => 1,
        "delete" => operation.Resource.Kind switch { "relation" => 0, "post" => 1, "property" => 2, "entity" => 3, _ => 4 },
        _ => operation.Resource.Kind switch { "entity" => 2, "property" => 3, "post" => 4, "relation" => 5, _ => 6 }
    };

    public static string Describe(IReadOnlyList<Operation> operations) =>
        "PUSH PLAN\n" + string.Join('\n', operations.Select(operation =>
            $"{operation.Action.ToUpperInvariant()} {operation.Resource.Id} [{string.Join(", ", operation.Fields)}]" +
            (operation.PrivacyChange ? " PRIVACY/PUBLICATION REVIEW" : ""))) +
        $"\n{operations.Count} API operations planned.";
}
