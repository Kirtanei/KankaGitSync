using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;

namespace KankaGitSync;

public static class CommandLine
{
    private const string Help = """
        Kanka Git Sync 0.1 — Git is permanent history; Kanka edits require review.
        git kanka init --campaign <positive-id>
        git kanka init-env
        git kanka import | fetch [--full] | status | diff | pull | validate | plan | update
        git kanka push [--approve-privacy]
        git kanka publish <local-id> | delete <local-id>
        git kanka doctor [--acknowledge-recovery]

        Commit init configuration before import. Commit local edits before plan/push.
        fetch reads the GitHub webhook queue; fetch --full scans Kanka. pull merges clean queued changes and closes their queue issues.
        delete records a tombstone; deletion execution is disabled in v0.1.
        KANKA_API_TOKEN or KANKA_TOKEN is read from the environment or world-root .env. Use a disposable campaign first.
        update checks the latest private GitHub Release and requires machine-level KANKA_GITHUB_TOKEN.
        """;

    public static async Task<int> RunAsync(string[] arguments, string directory, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default, Func<string?>? readToken = null, Func<ToolUpdater>? createUpdater = null,
        Func<string, string?>? readEnvironment = null, Func<GitRepository, GitHubIssuesClient>? createGitHub = null)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] is "--help" or "help")
            {
                await output.WriteLineAsync(Help).ConfigureAwait(false);
                return 0;
            }
            ValidateArguments(arguments);
            if (arguments[0] == "update")
            {
                await UpdateAsync(output, cancellationToken, createUpdater, readEnvironment).ConfigureAwait(false);
                return 0;
            }
            var repository = new GitRepository(directory);
            var root = await repository.RequireAsync(["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false);
            repository = new GitRepository(root);
            using var repositoryLock = await repository.LockAsync().ConfigureAwait(false);
            await DispatchAsync(arguments, repository, output, cancellationToken, readToken ?? TokenPrompt.Read, createGitHub ?? GitHub).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled. If a write was in progress, review kanka/live and run doctor before retrying.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // Parser, filesystem and HTTP exceptions can contain content or credentials.
            await error.WriteLineAsync(exception is SyncException ? exception.Message : $"Operation failed ({exception.GetType().Name}){FailureLocation(exception)}. Check input, access, or network connectivity. No sensitive details logged.").ConfigureAwait(false);
            return 1;
        }
    }

    private static string FailureLocation(Exception exception)
    {
        // Only compiled application method names are printed; exception messages and data can contain secrets.
        var method = new System.Diagnostics.StackTrace(exception).GetFrames()
            .Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.DeclaringType?.Assembly == typeof(CommandLine).Assembly);
        return method == null ? "" : $" at {method.DeclaringType?.Name}.{method.Name}";
    }

    private static bool IsExpected(Exception exception) => exception is SyncException or IOException or UnauthorizedAccessException
        or HttpRequestException or JsonException or YamlException or FormatException or InvalidOperationException or ArgumentException;

    private static void ValidateArguments(string[] arguments)
    {
        var valid = arguments[0] switch
        {
            "init" => arguments.Length == 3 && arguments[1] == "--campaign",
            "publish" or "delete" => arguments.Length == 2 && Canonical.ValidId(arguments[1]),
            "push" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--approve-privacy",
            "doctor" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--acknowledge-recovery",
            "fetch" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--full",
            "init-env" or "import" or "status" or "diff" or "pull" or "validate" or "plan" or "update" => arguments.Length == 1,
            _ => false
        };
        if (!valid) throw new SyncException("Unknown command or arguments. Run git kanka help.");
    }

    private static async Task DispatchAsync(string[] arguments, GitRepository repository, TextWriter output, CancellationToken cancellationToken,
        Func<string?> readToken, Func<GitRepository, GitHubIssuesClient> createGitHub)
    {
        if (arguments[0] == "init") { Initialize(repository, arguments[2]); return; }
        if (arguments[0] == "init-env")
        {
            await EnvironmentFile.InitializeAsync(repository, output, readToken, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (arguments[0] == "fetch" && arguments.Length == 1 || arguments[0] == "pull")
        {
            await RunQueuedAsync(arguments[0], repository, output, cancellationToken, createGitHub).ConfigureAwait(false);
            return;
        }
        var configuration = Configuration.Read(await File.ReadAllTextAsync(repository.SafePath(".kanka/config.yml"), cancellationToken).ConfigureAwait(false));
        if (await RunOfflineAsync(arguments, repository, output).ConfigureAwait(false)) return;
        var token = TokenConfiguration.Read(repository);
        using var client = new KankaClient(configuration.CampaignId, token, configuration.RequestsPerMinute);
        var ledger = await LedgerAsync(repository).ConfigureAwait(false);
        var service = new SyncService(repository, client, ledger);
        await RunOnlineAsync(arguments, repository, service, ledger, output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateAsync(TextWriter output, CancellationToken cancellationToken, Func<ToolUpdater>? createUpdater,
        Func<string, string?>? readEnvironment)
    {
        using var updater = createUpdater?.Invoke() ?? new ToolUpdater(ToolUpdater.CurrentVersion());
        var result = await updater.UpdateAsync(TokenConfiguration.ReadGitHubEnvironment(readEnvironment), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(result.Updated
            ? $"Updated Git Kanka to {result.Version}. Open a new terminal before running it again."
            : $"Git Kanka is already up to date ({result.Version}).").ConfigureAwait(false);
    }

    private static async Task<bool> RunOfflineAsync(string[] arguments, GitRepository repository, TextWriter output)
    {
        switch (arguments[0])
        {
            case "validate":
                var snapshot = WorldFiles.Read(WorldFiles.ReadWorkingTree(repository));
                Validation.Require(snapshot);
                foreach (var warning in Validation.Warnings(snapshot)) await output.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
                await output.WriteLineAsync($"Valid: {snapshot.Resources.Count} resources. Unrecognized Kanka mentions are preserved; inspect adapter snapshots for unmanaged fields.").ConfigureAwait(false);
                return true;
            case "status": await StatusAsync(repository, output).ConfigureAwait(false); return true;
            case "diff":
                await output.WriteLineAsync(await repository.RequireAsync(["diff", "--no-ext-diff", "--no-textconv", "main..kanka/live", "--", "world", ".kanka"]).ConfigureAwait(false)).ConfigureAwait(false);
                return true;
            case "publish": case "delete": EditFlag(repository, arguments[1], arguments[0] == "publish" ? "publish" : "deleted"); return true;
            case "doctor" when arguments.Length == 1:
                var ledger = await LedgerAsync(repository).ConfigureAwait(false);
                ledger.RequireSettled();
                await output.WriteLineAsync("Git and schema are available; ledger has no unresolved intents. Authentication and API access are checked by fetch. Live campaign round-trip acceptance is still required.").ConfigureAwait(false);
                return true;
            default: return false;
        }
    }

    private static async Task RunOnlineAsync(string[] arguments, GitRepository repository, SyncService service,
        OperationLedger ledger, TextWriter output, CancellationToken cancellationToken)
    {
        switch (arguments[0])
        {
            case "import": await ImportAsync(repository, service, output, cancellationToken).ConfigureAwait(false); break;
            case "fetch":
                var snapshot = await service.FetchAsync(output, cancellationToken).ConfigureAwait(false);
                foreach (var warning in Validation.Warnings(snapshot)) await output.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
                await output.WriteLineAsync($"Fetched {snapshot.Resources.Count} resources onto kanka/live. main is unchanged.").ConfigureAwait(false);
                break;
            case "plan":
                await repository.EnsureCleanMainAsync().ConfigureAwait(false);
                await output.WriteLineAsync(Planner.Describe(await service.PlanAsync(true, output, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
                break;
            case "push": await service.PushAsync(arguments.Length == 2, output, cancellationToken).ConfigureAwait(false); break;
            case "doctor":
                await service.FetchAsync(output, cancellationToken).ConfigureAwait(false);
                ledger.AcknowledgeRecovery();
                await output.WriteLineAsync("Unknown operation outcomes acknowledged after refetch. Review and merge kanka/live before another push; existing remote resources will not be recreated automatically.").ConfigureAwait(false);
                break;
            default: throw new SyncException("Unsupported online command.");
        }
    }

    private static void Initialize(GitRepository repository, string campaignText)
    {
        if (!long.TryParse(campaignText, NumberStyles.None, CultureInfo.InvariantCulture, out var campaign) || campaign <= 0)
            throw new SyncException("Campaign ID must be a positive integer.");
        var path = repository.SafePath(".kanka/config.yml");
        if (File.Exists(path)) throw new SyncException("Configuration already exists.");
        Directory.CreateDirectory(repository.SafePath(".kanka"));
        File.WriteAllText(path, new Configuration(campaign, 30).Write());
        var users = repository.SafePath(".kanka/users.yml");
        if (!File.Exists(users)) File.WriteAllText(users, "{}\n");
        File.AppendAllText(repository.SafePath(".gitignore"), "\n.env\n.env.*\n!.env.example\n.kanka/runtime/\n");
    }

    private static async Task ImportAsync(GitRepository repository, SyncService service, TextWriter output, CancellationToken cancellationToken)
    {
        await repository.EnsureCleanMainAsync().ConfigureAwait(false);
        var files = await repository.ReadTreeAsync(GitRepository.Main).ConfigureAwait(false);
        if (files.Keys.Any(path => path.StartsWith("world/", StringComparison.Ordinal)) || await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false) != null)
            throw new SyncException("Initial import requires no existing world or kanka/live branch. Use fetch for subsequent imports.");
        var snapshot = await service.FetchAsync(output, cancellationToken).ConfigureAwait(false);
        Validation.Require(snapshot);
        if (Planner.Build(snapshot, snapshot).Count != 0) throw new SyncException("Initial zero-change plan failed.");
        await repository.RequireAsync(["merge", "--ff-only", "kanka/live"], cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var warning in Validation.Warnings(snapshot)) await output.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
        await output.WriteLineAsync($"Imported campaign: {snapshot.Resources.Count} resources. main and kanka/live are identical; zero writes planned.").ConfigureAwait(false);
    }

    private static void EditFlag(GitRepository repository, string identifier, string flag)
    {
        var files = WorldFiles.ReadWorkingTree(repository);
        var snapshot = WorldFiles.Read(files);
        if (!snapshot.Resources.TryGetValue(identifier, out var resource)) throw new SyncException("Unknown local ID.");
        resource.Metadata[flag] = true;
        if (flag == "deleted") resource.Metadata["publish"] = true;
        var updated = WorldFiles.Write(snapshot);
        foreach (var pair in updated.Where(pair => pair.Key.StartsWith("world/", StringComparison.Ordinal)))
            if (!files.TryGetValue(pair.Key, out var previous) || pair.Value != previous)
                File.WriteAllText(repository.SafePath(pair.Key), pair.Value);
    }

    private static async Task<IReadOnlyList<QueueEvent>> QueueAsync(GitRepository repository, CancellationToken cancellationToken,
        Func<GitRepository, GitHubIssuesClient> createGitHub)
    {
        using var github = createGitHub(repository);
        return await github.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GitHubIssuesClient GitHub(GitRepository repository)
    {
        var origin = repository.GitHubOriginAsync().GetAwaiter().GetResult();
        return new GitHubIssuesClient(origin.Owner, origin.Repository, TokenConfiguration.ReadGitHub(repository));
    }

    private static async Task RunQueuedAsync(string command, GitRepository repository, TextWriter output, CancellationToken cancellationToken,
        Func<GitRepository, GitHubIssuesClient> createGitHub)
    {
        var issues = await QueueAsync(repository, cancellationToken, createGitHub).ConfigureAwait(false);
        if (issues.Count == 0)
        {
            if (command == "pull") await PullAsync(repository, output, []).ConfigureAwait(false);
            else await output.WriteLineAsync("GitHub webhook queue is empty; kanka/live is unchanged. Use git kanka fetch --full for a complete campaign scan.").ConfigureAwait(false);
            return;
        }
        var configuration = Configuration.Read(await File.ReadAllTextAsync(repository.SafePath(".kanka/config.yml"), cancellationToken).ConfigureAwait(false));
        using var client = new KankaClient(configuration.CampaignId, TokenConfiguration.Read(repository), configuration.RequestsPerMinute);
        var service = new SyncService(repository, client, await LedgerAsync(repository).ConfigureAwait(false));
        var snapshot = await service.FetchQueuedAsync(issues, cancellationToken).ConfigureAwait(false);
        if (command == "fetch")
        {
            foreach (var warning in Validation.Warnings(snapshot)) await output.WriteLineAsync("Warning: " + warning).ConfigureAwait(false);
            await output.WriteLineAsync($"Fetched {snapshot.Resources.Count} resources onto kanka/live. main is unchanged.").ConfigureAwait(false);
            return;
        }
        await PullAsync(repository, output, issues.Select(issue => issue.IssueNumber)).ConfigureAwait(false);
        using var github = createGitHub(repository);
        await github.CloseAsync(issues.Select(issue => issue.IssueNumber), cancellationToken).ConfigureAwait(false);
    }

    private static async Task PullAsync(GitRepository repository, TextWriter output, IEnumerable<long> issueNumbers)
    {
        await repository.EnsureCleanMainAsync().ConfigureAwait(false);
        var main = await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false) ?? throw new SyncException("No main branch.");
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false) ?? throw new SyncException("Fetch first.");
        if (await repository.IsAncestorAsync(live, main).ConfigureAwait(false))
        {
            await output.WriteLineAsync("Remote state is already integrated.").ConfigureAwait(false);
            return;
        }
        var basis = await repository.RequireAsync(["merge-base", main, live]).ConfigureAwait(false);
        SemanticMerge.CheckSafety(WorldFiles.Read(await repository.ReadTreeAsync(basis).ConfigureAwait(false)),
            WorldFiles.Read(await repository.ReadTreeAsync(main).ConfigureAwait(false)), WorldFiles.Read(await repository.ReadTreeAsync(live).ConfigureAwait(false)));
        var result = await repository.RunAsync(["merge", "--no-ff", "--no-commit", "kanka/live"]).ConfigureAwait(false);
        if (result.ExitCode == 1) await SemanticMerge.ResolveStructuredAsync(repository, basis, main, live).ConfigureAwait(false);
        var unresolved = await repository.RequireAsync(["diff", "--name-only", "--diff-filter=U"]).ConfigureAwait(false);
        var clean = result.ExitCode is 0 or 1 && unresolved.Length == 0;
        await output.WriteLineAsync(clean ? "Remote merge committed." : "Git merge needs attention. Inspect git status and resolve conflicts.").ConfigureAwait(false);
        if (!clean) throw new SyncException("Pull has unresolved conflicts; review the Git index.");
        var trailers = string.Concat(issueNumbers.Distinct().Order().Select(number => "\nKanka-Queue-Issue: " + number));
        await repository.RequireAsync(["commit", "-m", "Merge queued Kanka changes" + trailers], environment: GitRepository.TechnicalIdentity()).ConfigureAwait(false);
    }

    private static async Task StatusAsync(GitRepository repository, TextWriter output)
    {
        var main = await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false);
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false);
        if (main == null || live == null) throw new SyncException("Import a campaign first.");
        var basis = await repository.RequireAsync(["merge-base", main, live]).ConfigureAwait(false);
        await output.WriteLineAsync("REMOTE CHANGES (polling attribution is ambiguous; shown user is last observed editor)").ConfigureAwait(false);
        var remote = WorldFiles.Read(await repository.ReadTreeAsync(live).ConfigureAwait(false));
        var baseline = WorldFiles.Read(await repository.ReadTreeAsync(basis).ConfigureAwait(false));
        foreach (var identifier in remote.Resources.Values.Where(resource => !baseline.Resources.TryGetValue(resource.Id, out var old) || old.Signature != resource.Signature).Select(resource => resource.Id))
        {
            var userId = remote.Raw[identifier]["updated_by"]?.ToString() ?? "unknown";
            var user = remote.Users[userId] as JsonObject;
            await output.WriteLineAsync($"  {user?.Text("role", "unknown") ?? "unknown"}: {user?.Text("name", "unknown") ?? "unknown"} — {identifier} (possible attribution)").ConfigureAwait(false);
        }
        await output.WriteLineAsync("LOCAL CHANGES\n" + await repository.RequireAsync(["diff", "--stat", basis, main, "--", "world"]).ConfigureAwait(false)).ConfigureAwait(false);
        await output.WriteLineAsync("REMOTE DIFF\n" + await repository.RequireAsync(["diff", "--stat", basis, live, "--", "world", ".kanka/remote"]).ConfigureAwait(false)).ConfigureAwait(false);
        var verified = await repository.ResolveAsync(GitRepository.Verified).ConfigureAwait(false);
        await output.WriteLineAsync(await repository.IsAncestorAsync(live, main).ConfigureAwait(false) || live == verified
            ? "Remote state integrated or verified as a synchronization echo; push will fetch again."
            : "Push blocked: remote changes require integration.").ConfigureAwait(false);
    }

    public static async Task<OperationLedger> LedgerAsync(GitRepository repository)
    {
        var common = await repository.RequireAsync(["rev-parse", "--git-common-dir"]).ConfigureAwait(false);
        return new OperationLedger(Path.Combine(Path.GetFullPath(common, repository.Root), "kanka-ledger.jsonl"));
    }
}
