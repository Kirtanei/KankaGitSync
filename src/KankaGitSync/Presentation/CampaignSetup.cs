using System.Diagnostics;
using System.Globalization;

namespace KankaGitSync.Presentation;

public sealed record SetupRequest(string WorldDirectory, string CampaignText, string Token);

public static class CampaignSetup
{
    private static readonly string[] gitIgnoreRules = [".env", ".env.*", "!.env.example", ".kanka/runtime/"];

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
        await EnsureConfigurationAsync(configurationPath, campaignId, cancellationToken).ConfigureAwait(false);
        var usersPath = repository.SafePath(".kanka/users.yml");
        if (!File.Exists(usersPath)) await File.WriteAllTextAsync(usersPath, "{}\n", cancellationToken).ConfigureAwait(false);
        await EnsureGitIgnoreRulesAsync(repository.SafePath(".gitignore"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureConfigurationAsync(string configurationPath, long campaignId, CancellationToken cancellationToken)
    {
        if (File.Exists(configurationPath))
        {
            var existing = Configuration.Read(await File.ReadAllTextAsync(configurationPath, cancellationToken).ConfigureAwait(false));
            if (existing.CampaignId != campaignId)
                throw new SyncException($"This world is already configured for campaign {existing.CampaignId}.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath) ?? throw new SyncException("Invalid configuration path."));
        await File.WriteAllTextAsync(configurationPath, new Configuration(campaignId, 30).Write(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureGitIgnoreRulesAsync(string gitIgnorePath, CancellationToken cancellationToken)
    {
        var existingRules = File.Exists(gitIgnorePath)
            ? await File.ReadAllLinesAsync(gitIgnorePath, cancellationToken).ConfigureAwait(false)
            : [];
        var missingRules = gitIgnoreRules.Where(rule => !existingRules.Contains(rule, StringComparer.Ordinal)).ToArray();
        if (missingRules.Length == 0) return;
        var prefix = existingRules.Length == 0 ? "" : "\n";
        await File.AppendAllTextAsync(gitIgnorePath, prefix + string.Join('\n', missingRules) + "\n", cancellationToken).ConfigureAwait(false);
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
