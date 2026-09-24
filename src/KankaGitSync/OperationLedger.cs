using System.Text;
using System.Text.Json.Nodes;

namespace KankaGitSync;

public sealed class OperationLedger(string path)
{
    public void Append(JsonObject entry)
    {
        entry["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        var bytes = Encoding.UTF8.GetBytes(Canonical.Json(entry) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public IEnumerable<JsonObject> Read()
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
            yield return JsonNode.Parse(line) as JsonObject ?? throw new SyncException("Invalid operation ledger; review recovery state.");
    }

    public void RestoreMappings(Snapshot snapshot)
    {
        foreach (var entry in Read().Where(entry => entry.Text("phase") == "applied" && entry["mapping"] is JsonObject))
        {
            var mapping = (JsonObject)entry["mapping"]!;
            snapshot.Mappings[entry.Text("resource")] = new Mapping(mapping.Number("entity_id"), mapping.Number("child_id"),
                mapping.Text("category"), mapping.Text("kind"), mapping["owner"]?.GetValue<string>());
        }
    }

    public void RequireSettled()
    {
        var records = Read().ToArray();
        var starts = records.Where(entry => entry.Text("phase") == "intent").Select(entry => entry.Text("operation_id"));
        var applied = records.Where(entry => entry.Text("phase") is "applied" or "reviewed").Select(entry => entry.Text("operation_id")).ToHashSet();
        if (starts.Any(identifier => !applied.Contains(identifier)))
            throw new SyncException("An interrupted write has an unknown outcome. Fetch and review Kanka, then run doctor --acknowledge-recovery. No operation will be replayed automatically.");
    }

    public void AcknowledgeRecovery()
    {
        var entries = Read().ToArray();
        var applied = entries.Where(entry => entry.Text("phase") is "applied" or "reviewed").Select(entry => entry.Text("operation_id")).ToHashSet();
        foreach (var entry in entries.Where(entry => entry.Text("phase") == "intent" && !applied.Contains(entry.Text("operation_id"))))
            Append(new JsonObject { ["phase"] = "reviewed", ["operation_id"] = entry.Text("operation_id") });
    }
}
