using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Persistence;

internal sealed record IdMappingDocument(long EntityId, long ChildId, string Category, string Kind, string? Owner)
{
    public static IdMappingDocument FromMapping(Mapping mapping) => new(mapping.EntityId, mapping.ChildId, mapping.Category, mapping.Kind, mapping.Owner);

    public Mapping ToMapping() => new(EntityId, ChildId, Category, Kind, Owner);

    public JsonObject ToJson() => new()
    {
        ["entity_id"] = EntityId,
        ["child_id"] = ChildId,
        ["entity_type"] = Category,
        ["kind"] = Kind,
        ["owner"] = Owner
    };

    public static IdMappingDocument FromJson(JsonObject value) => new(
        value.Number("entity_id"),
        value.Number("child_id"),
        value.Text("entity_type", value.Text("category")),
        value.Text("kind"),
        value["owner"]?.GetValue<string>());
}
