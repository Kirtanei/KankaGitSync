using System.Diagnostics;
using System.Globalization;

namespace KankaGitSync;

public sealed record SetupRequest(string WorldDirectory, string CampaignText, string Token);

public static class CampaignSetup
{
    public static string DefaultWorldDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kanka Worlds", "Kanka World");

    public static async Task<GitRepository> PrepareAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(request.CampaignText, NumberStyles.None, CultureInfo.InvariantCulture, out var campaignId) || campaignId <= 0)
            throw new SyncException("Campaign ID must be a positive integer.");
        var worldDirectory = ValidateWorldDirectory(request.WorldDirectory);
        await EnsureRepositoryAsync(worldDirectory, cancellationToken).ConfigureAwait(false);
        var repository = new GitRepository(worldDirectory);
        await InitializeAsync(repository, campaignId, cancellationToken).ConfigureAwait(false);
        await EnvironmentFile.SaveAsync(repository, request.Token, cancellationToken).ConfigureAwait(false);
        return repository;
    }

    public static async Task InitializeAsync(GitRepository repository, long campaignId, CancellationToken cancellationToken = default)
    {
        if (campaignId <= 0) throw new SyncException("Campaign ID must be a positive integer.");
        var configurationPath = repository.SafePath(".kanka/config.yml");
        if (File.Exists(configurationPath)) throw new SyncException("Configuration already exists.");
        Directory.CreateDirectory(repository.SafePath(".kanka"));
        await File.WriteAllTextAsync(configurationPath, new Configuration(campaignId, 30).Write(), cancellationToken).ConfigureAwait(false);
        var usersPath = repository.SafePath(".kanka/users.yml");
        if (!File.Exists(usersPath)) await File.WriteAllTextAsync(usersPath, "{}\n", cancellationToken).ConfigureAwait(false);
        await File.AppendAllTextAsync(repository.SafePath(".gitignore"), "\n.env\n.env.*\n!.env.example\n.kanka/runtime/\n", cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateWorldDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new SyncException("Choose a world folder.");
        var path = Path.GetFullPath(value);
        if (Path.GetPathRoot(path) == path) throw new SyncException("Choose a folder below a drive root.");
        return path;
    }

    private static async Task EnsureRepositoryAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        if (Directory.Exists(Path.Combine(directory, ".git"))) return;
        var executable = GitLocator.Find();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("-b");
        start.ArgumentList.Add("main");
        using var process = Process.Start(start) ?? throw new SyncException("Could not start Git.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new SyncException("Git could not initialize the selected world folder.");
    }
}
