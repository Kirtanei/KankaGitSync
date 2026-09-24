namespace KankaGitSync.Tests;

internal sealed class TestRepository : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kanka-test-" + Guid.NewGuid().ToString("N"));
    public GitRepository Git { get; }
    public OperationLedger Ledger { get; }
    public TestCampaign Campaign { get; } = new();
    public SyncService Service { get; }

    public TestRepository()
    {
        Directory.CreateDirectory(directory);
        Git = new GitRepository(directory);
        Ledger = new OperationLedger(Path.Combine(directory, ".git", "kanka-ledger.jsonl"));
        Service = new SyncService(Git, Campaign, Ledger);
    }

    public async Task InitializeAsync(bool import = true)
    {
        await Git.RequireAsync(["init", "-b", "main"]);
        await Git.RequireAsync(["config", "user.name", "Test GM"]);
        await Git.RequireAsync(["config", "user.email", "test@localhost"]);
        await Git.RequireAsync(["config", "commit.gpgsign", "false"]);
        await Git.RequireAsync(["config", "core.autocrlf", "false"]);
        await Git.RequireAsync(["config", "core.hooksPath", Path.Combine(directory, "empty-hooks")]);
        Directory.CreateDirectory(Path.Combine(directory, ".kanka"));
        await File.WriteAllTextAsync(Path.Combine(directory, ".kanka/config.yml"), new Configuration(123, 30).Write());
        await File.WriteAllTextAsync(Path.Combine(directory, ".kanka/users.yml"), "'9':\n  name: Jesper\n  role: owner\n");
        await CommitAsync();
        if (!import) return;
        await Service.FetchAsync();
        await Git.RequireAsync(["merge", "--ff-only", "kanka/live"]);
    }

    public async Task CommitAsync()
    {
        await Git.RequireAsync(["add", "--all"]);
        await Git.RequireAsync(["commit", "-m", "Test authoring"]);
    }

    public Snapshot Working() => WorldFiles.Read(WorldFiles.ReadWorkingTree(Git));

    public void Save(Snapshot snapshot)
    {
        foreach (var pair in WorldFiles.Write(snapshot))
        {
            var path = Git.SafePath(pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value);
        }
    }

    public void Dispose()
    {
        // This unique directory is created and owned by this fixture; Git objects can be read-only.
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
