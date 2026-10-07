using System.Text.Json.Nodes;

namespace KankaGitSync.Tests;

public sealed class SynchronizationTests
{
    [Fact]
    public async Task ImportIsReadableStableAndZeroWrite()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        Assert.Equal(6, snapshot.Resources.Count);
        Assert.Contains("[[tarant|Tarant]]", snapshot.Resources["maximilian"].Body);
        Assert.Equal("owner", ((JsonObject)snapshot.Users["9"]!).Text("role"));
        Assert.Equal("self", snapshot.Resources["maximilian-journal"].Metadata.Text("visibility"));
        var first = await fixture.Git.ResolveAsync(GitRepository.Live);
        await fixture.Service.FetchAsync();
        Assert.Equal(first, await fixture.Git.ResolveAsync(GitRepository.Live));
        Assert.Empty(await fixture.Service.PlanAsync(false));
        await fixture.Service.PushAsync(false, TextWriter.Null);
        Assert.Empty(fixture.Campaign.Writes);
        Assert.Equal("", await fixture.Git.RequireAsync(["status", "--porcelain"]));
        Assert.Equal(snapshot.Resources.Keys, WorldFiles.Read(WorldFiles.Write(snapshot)).Resources.Keys);
    }

    [Fact]
    public async Task PlayerEditIsPreservedAndBlocksPush()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var main = await fixture.Git.ResolveAsync(GitRepository.Main);
        fixture.Campaign.Records["entities/11/posts/5"]["entry"] = "<p>Alice's new notes.</p>";
        var exception = await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(true, TextWriter.Null));
        Assert.Contains("unintegrated", exception.Message);
        Assert.Equal(main, await fixture.Git.ResolveAsync(GitRepository.Main));
        Assert.Contains("Alice's", WorldFiles.Read(await fixture.Git.ReadTreeAsync(GitRepository.Live)).Resources["maximilian-journal"].Body);
        Assert.Empty(fixture.Campaign.Writes);
        await fixture.Git.RequireAsync(["merge", "--ff-only", "kanka/live"]);
        Assert.Empty(await fixture.Service.PlanAsync(false));
    }

    [Fact]
    public async Task LocalEditPublishesAsMinimalPatchAndSecondPushHasNoWrites()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"] = snapshot.Resources["maximilian"] with { Body = "Updated **history**." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        await fixture.Service.PushAsync(false, TextWriter.Null);
        var write = Assert.Single(fixture.Campaign.Writes);
        Assert.Equal("characters/1", write.Path);
        Assert.Equal(["entry"], write.Body.Select(pair => pair.Key));
        Assert.Equal("preserve me", fixture.Campaign.Records["characters/1"]["unmanaged"]!["secret"]!.GetValue<string>());
        await fixture.Service.PushAsync(false, TextWriter.Null);
        Assert.Single(fixture.Campaign.Writes);
        Assert.Contains(fixture.Ledger.Read(), entry => entry.Text("phase") == "verified");
        fixture.Campaign.Records["characters/1"]["entry"] = "<p>Manual owner edit.</p>";
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Single(fixture.Campaign.Writes);
    }

    [Fact]
    public async Task VolatileRelationshipSyncMarkerDoesNotBlockPublication()
    {
        using var fixture = new TestRepository();
        fixture.Campaign.Records["characters/1"]["organisations"] = new JsonObject { ["data"] = new JsonArray(), ["sync"] = "before" };
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"] = snapshot.Resources["maximilian"] with { Body = "Updated history." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        var characterReads = 0;
        fixture.Campaign.BeforeGet = path =>
        {
            if (path == "characters/1" && ++characterReads == 2)
                fixture.Campaign.Records[path]["organisations"]!["sync"] = "after";
        };
        await fixture.Service.PushAsync(false, TextWriter.Null);
        Assert.Single(fixture.Campaign.Writes);
    }

    internal static Resource NewEntity(string identifier, string body = "History.", bool publish = true) => new(identifier, "entity", null,
        new JsonObject
        {
            ["id"] = identifier,
            ["name"] = identifier,
            ["category"] = "location",
            ["type"] = "",
            ["publish"] = publish,
            ["visibility"] = new JsonObject { ["private"] = false },
            ["tags"] = new JsonArray(),
            ["fields"] = new JsonObject()
        }, body);
}

public sealed class SynchronizationCreationTests
{
    [Fact]
    public async Task MissingFilesNeverDeleteRemoteObjectsAndTombstonesRequireExplicitAcknowledgement()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var local = fixture.Working();
        var remote = fixture.Working();
        local.Resources.Remove("maximilian-journal");
        Assert.Empty(Planner.Build(local, remote));
        local = fixture.Working();
        local.Resources["maximilian-journal"].Metadata["deleted"] = true;
        Assert.Contains(Planner.Build(local, remote), operation => operation.Action == "delete");
        fixture.Save(local);
        await fixture.CommitAsync();
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(true, false, TextWriter.Null));
        Assert.Empty(fixture.Campaign.Writes);
        await fixture.Service.PushAsync(true, true, TextWriter.Null);
        Assert.Equal(["entities/11/posts/5"], fixture.Campaign.Deletes);
    }

    [Fact]
    public async Task NewEntitiesArePrivateInitiallyAndCrossReferencesResolveAfterCreation()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        fixture.Campaign.ReturnIncompleteEntityCreateResponse = true;
        var snapshot = fixture.Working();
        var first = SynchronizationTests.NewEntity("chapel", "[[keeper]]");
        var second = SynchronizationTests.NewEntity("keeper", "[[chapel]]");
        snapshot.Resources[first.Id] = first;
        snapshot.Resources[second.Id] = second;
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Empty(fixture.Campaign.Writes);
        await fixture.Service.PushAsync(true, TextWriter.Null);
        Assert.Equal(4, fixture.Campaign.Writes.Count);
        Assert.All(fixture.Campaign.Writes.Take(2), write => Assert.True(write.Body.Flag("is_private")));
        Assert.All(fixture.Campaign.Writes.Skip(2), write => Assert.Contains("[entity:", write.Body.Text("entry")));
        await fixture.Service.PushAsync(true, TextWriter.Null);
        Assert.Equal(4, fixture.Campaign.Writes.Count);
    }

    [Fact]
    public async Task PropertiesPostsAndRelationsUpdateIndependentlyAndPreservePermissions()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian-population"].Metadata["value"] = "900000";
        snapshot.Resources["maximilian-journal"] = snapshot.Resources["maximilian-journal"] with { Body = "More history." };
        snapshot.Resources["maximilian-relation"].Metadata["attitude"] = 60;
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        await fixture.Service.PushAsync(false, TextWriter.Null);
        Assert.Equal(3, fixture.Campaign.Writes.Count);
        Assert.DoesNotContain(fixture.Campaign.Writes, write => write.Body.ContainsKey("permissions") || write.Body.ContainsKey("visibility_id"));
        Assert.NotNull(fixture.Campaign.Records["entities/11/posts/5"]["permissions"]);
        await fixture.Service.PushAsync(false, TextWriter.Null);
        Assert.Equal(3, fixture.Campaign.Writes.Count);
    }
}

public sealed class SynchronizationRecoveryTests
{
    [Fact]
    public async Task PartialFailureRefetchesAndNeverBlindlyReplays()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian-population"].Metadata["value"] = "900000";
        snapshot.Resources["maximilian-journal"] = snapshot.Resources["maximilian-journal"] with { Body = "More history." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        fixture.Campaign.FailWrite = 2;
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        var live = WorldFiles.Read(await fixture.Git.ReadTreeAsync(GitRepository.Live));
        Assert.Equal("900000", live.Resources["maximilian-population"].Metadata.Text("value"));
        Assert.Throws<SyncException>(fixture.Ledger.RequireSettled);
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Equal(2, fixture.Campaign.Writes.Count);
        fixture.Ledger.AcknowledgeRecovery();
        fixture.Ledger.RequireSettled();
    }

    [Fact]
    public async Task RaceBeforeMutationStopsAndPreservesRemoteEdit()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"] = snapshot.Resources["maximilian"] with { Body = "GM update." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        var reads = 0;
        fixture.Campaign.BeforeGet = path =>
        {
            if (path == "characters/1" && ++reads == 2) fixture.Campaign.Records[path]["entry"] = "<p>Concurrent edit.</p>";
        };
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Empty(fixture.Campaign.Writes);
        Assert.Contains("Concurrent", WorldFiles.Read(await fixture.Git.ReadTreeAsync(GitRepository.Live)).Resources["maximilian"].Body);
    }

    [Fact]
    public async Task UnrelatedConcurrentChangeCannotBeClassifiedAsSyncEcho()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"] = snapshot.Resources["maximilian"] with { Body = "GM update." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        fixture.Campaign.AfterWrite = _ => fixture.Campaign.Records["locations/3"]["entry"] = "<p>Another user edited this.</p>";
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Null(await fixture.Git.ResolveAsync(GitRepository.Verified));
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.EnsureIntegratedAsync());
    }
}

public sealed class SynchronizationPublicationTests
{
    [Fact]
    public async Task NewAttachmentsAndTagChangesRoundTrip()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"].Metadata["tags"] = new JsonArray();
        snapshot.Resources["new-property"] = new Resource("new-property", "property", "maximilian",
            new JsonObject { ["id"] = "new-property", ["name"] = "Home", ["type"] = "text", ["value"] = "[[tarant]]", ["private"] = true, ["publish"] = true });
        snapshot.Resources["new-post"] = new Resource("new-post", "post", "maximilian",
            new JsonObject { ["id"] = "new-post", ["name"] = "Secret", ["visibility"] = "admin", ["publish"] = true }, "**Secret** notes.");
        snapshot.Resources["new-relation"] = new Resource("new-relation", "relation", "maximilian",
            new JsonObject { ["id"] = "new-relation", ["target"] = "nobility", ["relation"] = "member", ["attitude"] = 10, ["visibility"] = "admin", ["publish"] = true });
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        await fixture.Service.PushAsync(true, TextWriter.Null);
        Assert.Equal(4, fixture.Campaign.Writes.Count);
        Assert.Contains(fixture.Campaign.Writes, write => write.Body.Text("value") == "[entity:33]");
        await fixture.Service.PushAsync(true, TextWriter.Null);
        Assert.Equal(4, fixture.Campaign.Writes.Count);
    }

    [Fact]
    public async Task PrivacyChangesRequireExplicitPlanAcknowledgement()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian-journal"].Metadata["visibility"] = "all";
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Empty(fixture.Campaign.Writes);
        await fixture.Service.PushAsync(true, TextWriter.Null);
        Assert.Equal(1, Assert.Single(fixture.Campaign.Writes).Body.Number("visibility_id"));
    }

    [Fact]
    public async Task FailedVerificationNeverMarksAnEchoAccepted()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian"] = snapshot.Resources["maximilian"] with { Body = "New history." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        fixture.Campaign.AfterWrite = path => fixture.Campaign.Records[path]["entry"] = "<p>Unexpected server content.</p>";
        await Assert.ThrowsAsync<SyncException>(() => fixture.Service.PushAsync(false, TextWriter.Null));
        Assert.Null(await fixture.Git.ResolveAsync(GitRepository.Verified));
    }

}
