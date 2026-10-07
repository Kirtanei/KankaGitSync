using System.Text.Json.Nodes;

namespace KankaGitSync.Application;

public interface ISynchronizationRepository
{
    Task<string?> ResolveAsync(string reference);
    Task<SortedDictionary<string, string>> ReadTreeAsync(string? reference);
    Task<string> CommitLiveAsync(SortedDictionary<string, string> files, string message);
    Task<bool> IsAncestorAsync(string ancestor, string descendant);
    Task EnsureCleanMainAsync();
    Task<string> UpdateRefAsync(string reference, string commit, string? expected);
}

public interface IOperationJournal
{
    void Append(JsonObject entry);
    void RestoreMappings(Snapshot snapshot);
    void RequireSettled();
    void AcknowledgeRecovery();
}
