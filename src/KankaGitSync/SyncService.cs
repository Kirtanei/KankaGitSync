using System.Text.Json.Nodes;

namespace KankaGitSync;

public sealed class SyncService(GitRepository repository, IKankaClient client, OperationLedger ledger)
{
    public async Task<Snapshot> FetchAsync(TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false);
        var previousFiles = await repository.ReadTreeAsync(live).ConfigureAwait(false);
        var previous = WorldFiles.Read(previousFiles);
        var mainFiles = await repository.ReadTreeAsync(await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false)).ConfigureAwait(false);
        if (mainFiles.TryGetValue(".kanka/users.yml", out var users)) previous.Users = YamlCodec.Read(users);
        ledger.RestoreMappings(previous);
        var progress = output == null ? null : new FetchProgressWriter(output);
        if (progress != null) await progress.BeginAsync().ConfigureAwait(false);
        Snapshot current;
        try
        {
            current = await new KankaAdapter(client).FetchAsync(previous, cancellationToken,
                progress == null ? null : progress.ReportAsync).ConfigureAwait(false);
        }
        finally
        {
            if (progress != null) await progress.CompleteAsync().ConfigureAwait(false);
        }
        var files = WorldFiles.Write(current);
        if (mainFiles.TryGetValue(".kanka/config.yml", out var configuration)) files[".kanka/config.yml"] = configuration;
        var message = ImportMessage(previous, current);
        await repository.CommitLiveAsync(files, message).ConfigureAwait(false);
        return current;
    }

    public async Task<Snapshot> FetchQueuedAsync(IReadOnlyList<QueueEvent> events, CancellationToken cancellationToken = default)
    {
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false);
        var previous = WorldFiles.Read(await repository.ReadTreeAsync(live).ConfigureAwait(false));
        var mainFiles = await repository.ReadTreeAsync(await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false)).ConfigureAwait(false);
        if (mainFiles.TryGetValue(".kanka/users.yml", out var users)) previous.Users = YamlCodec.Read(users);
        ledger.RestoreMappings(previous);
        var current = await new KankaAdapter(client).FetchQueuedAsync(previous, events, cancellationToken).ConfigureAwait(false);
        var files = WorldFiles.Write(current);
        if (mainFiles.TryGetValue(".kanka/config.yml", out var configuration)) files[".kanka/config.yml"] = configuration;
        var message = ImportMessage(previous, current) + string.Concat(events.Select(value => "\nKanka-Queue-Issue: " + value.IssueNumber));
        await repository.CommitLiveAsync(files, message).ConfigureAwait(false);
        return current;
    }

    private static string ImportMessage(Snapshot previous, Snapshot current)
    {
        var changed = current.Resources.Values.Where(resource => !previous.Resources.TryGetValue(resource.Id, out var old) || resource.Signature != old.Signature);
        var trailers = changed.Select(resource => "Kanka-Resource: " + resource.Id + "\nKanka-Entity-ID: " + current.Mappings[resource.Id].EntityId);
        return "Fetch Kanka state\n\nKanka-Source: polling\nKanka-Attribution: ambiguous; last editor is not necessarily sole author\n" + string.Join('\n', trailers);
    }

    public async Task EnsureIntegratedAsync()
    {
        var main = await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false) ?? throw new SyncException("Import a campaign first.");
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false) ?? throw new SyncException("Fetch a campaign first.");
        if (await repository.IsAncestorAsync(live, main).ConfigureAwait(false)) return;
        var verified = await repository.ResolveAsync(GitRepository.Verified).ConfigureAwait(false);
        var published = await repository.ResolveAsync(GitRepository.Published).ConfigureAwait(false);
        if (live == verified && published != null && await repository.IsAncestorAsync(published, main).ConfigureAwait(false)) return;
        throw new SyncException("Push blocked: kanka/live contains unintegrated remote changes. Review and merge or explicitly reject them with a Git merge commit.");
    }

    public async Task<IReadOnlyList<Operation>> PlanAsync(bool fetch, TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        var remote = fetch ? await FetchAsync(output, cancellationToken).ConfigureAwait(false) :
            WorldFiles.Read(await repository.ReadTreeAsync(await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false)).ConfigureAwait(false));
        await EnsureIntegratedAsync().ConfigureAwait(false);
        var local = WorldFiles.Read(await repository.ReadTreeAsync(GitRepository.Main).ConfigureAwait(false));
        return Planner.Build(local, remote);
    }

    public Task PushAsync(bool approvePrivacy, TextWriter output, CancellationToken cancellationToken = default) =>
        PushAsync(approvePrivacy, false, output, cancellationToken);

    public async Task PushAsync(bool approvePrivacy, bool allowDelete, TextWriter output, CancellationToken cancellationToken = default)
    {
        await repository.EnsureCleanMainAsync().ConfigureAwait(false);
        var main = await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false) ?? throw new SyncException("Import a campaign first.");
        var remote = await FetchAsync(output, cancellationToken).ConfigureAwait(false);
        await EnsureIntegratedAsync().ConfigureAwait(false);
        ledger.RequireSettled();
        var local = WorldFiles.Read(await repository.ReadTreeAsync(main).ConfigureAwait(false));
        var operations = Planner.Build(local, remote);
        await output.WriteLineAsync(Planner.Describe(operations)).ConfigureAwait(false);
        if (!allowDelete && operations.Any(operation => operation.Action == "delete"))
            throw new SyncException("Review deletion tombstones, then use --allow-delete to apply this plan.");
        if (!approvePrivacy && operations.Any(operation => operation.PrivacyChange))
            throw new SyncException("Review publication/privacy changes, then use --approve-privacy to apply this plan.");
        if (operations.Count == 0) return;
        await ApplyAndVerifyAsync(main, operations, remote, output, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAndVerifyAsync(string main, IReadOnlyList<Operation> operations, Snapshot remote, TextWriter output,
        CancellationToken cancellationToken)
    {
        Exception? applyFailure = null;
        try
        {
            await new PushExecutor(client, ledger).ApplyAsync(operations, remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            applyFailure = exception;
        }
        try
        {
            // A cancelled or failed mutation may have reached Kanka; recovery must not inherit cancellation.
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await FetchAsync(output, recovery.Token).ConfigureAwait(false);
        }
        catch (Exception recoveryFailure)
        {
            throw new SyncException("Recovery fetch failed. Preserve the ledger and fetch again before retrying any writes.",
                applyFailure == null ? recoveryFailure : new AggregateException(applyFailure, recoveryFailure));
        }
        if (applyFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(applyFailure).Throw();
        var actual = WorldFiles.Read(await repository.ReadTreeAsync(GitRepository.Live).ConfigureAwait(false));
        Verify(operations, actual);
        VerifyUnplanned(remote, actual, operations);
        if (await repository.ResolveAsync(GitRepository.Main).ConfigureAwait(false) != main)
            throw new SyncException("main moved during publication; review before the next push.");
        var live = await repository.ResolveAsync(GitRepository.Live).ConfigureAwait(false) ?? throw new SyncException("Missing verification ref.");
        await repository.UpdateRefAsync(GitRepository.Published, main, await repository.ResolveAsync(GitRepository.Published).ConfigureAwait(false)).ConfigureAwait(false);
        await repository.UpdateRefAsync(GitRepository.Verified, live, await repository.ResolveAsync(GitRepository.Verified).ConfigureAwait(false)).ConfigureAwait(false);
        ledger.Append(new JsonObject { ["phase"] = "verified", ["source_commit"] = main, ["live_commit"] = live });
    }

    private static void Verify(IReadOnlyList<Operation> operations, Snapshot actual)
    {
        foreach (var resource in operations.Select(operation => operation.Resource).DistinctBy(resource => resource.Id))
        {
            if (operations.Any(operation => operation.Resource.Id == resource.Id && operation.Action == "delete"))
            {
                if (actual.Resources.ContainsKey(resource.Id)) throw new SyncException("Post-push verification failed: deleted resource remains in Kanka.");
                continue;
            }
            if (!actual.Resources.TryGetValue(resource.Id, out var imported)) throw new SyncException("Post-push verification failed: resource missing.");
            var expected = KankaAdapter.Payload(resource, actual.Mappings);
            var observed = KankaAdapter.Payload(imported, actual.Mappings);
            if (expected.Any(field => !Planner.Equivalent(field.Key, field.Value, observed[field.Key])))
                throw new SyncException("Post-push verification failed. Actual Kanka state is on kanka/live; review before retrying.");
        }
    }

    private static void VerifyUnplanned(Snapshot expected, Snapshot actual, IReadOnlyList<Operation> operations)
    {
        var plannedResources = operations.Select(operation => operation.Resource.Id).ToHashSet(StringComparer.Ordinal);
        var expectedResources = expected.Raw.Keys.Where(identifier => !plannedResources.Contains(identifier)).ToHashSet(StringComparer.Ordinal);
        var actualResources = actual.Raw.Keys.Where(identifier => !plannedResources.Contains(identifier)).ToHashSet(StringComparer.Ordinal);
        if (!expectedResources.SetEquals(actualResources))
            throw new SyncException("Remote resources appeared or disappeared during publication; review kanka/live.");
        foreach (var identifier in expectedResources)
        {
            var before = StableState(expected.Raw[identifier]);
            var after = StableState(actual.Raw[identifier]);
            if (Canonical.Hash(before) != Canonical.Hash(after))
                throw new SyncException("Unexpected remote change during publication; review kanka/live before another push.");
        }
    }

    private static JsonObject StableState(JsonObject value)
    {
        var result = PushExecutor.ConcurrencyState(value);
        result.Remove("updated_at");
        result.Remove("updated_by");
        return result;
    }
}
