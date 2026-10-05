namespace KankaGitSync.Tests;

public sealed class CommandAndGitTests
{
    [Fact]
    public async Task InvalidJsonTypesReportCodeLocationWithoutContent()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var resource = fixture.Working().Resources["maximilian"];
        resource.Metadata["name"] = new System.Text.Json.Nodes.JsonObject { ["sensitive-value"] = true };
        File.WriteAllText(fixture.Git.SafePath(WorldFiles.EntityDirectory(resource) + "/index.md"), YamlCodec.WriteMarkdown(resource));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["validate"], fixture.Git.Root, output, errors));
        Assert.Contains(" at JsonFields.Text", errors.ToString());
        Assert.DoesNotContain("sensitive-value", errors.ToString());
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("unknown", 1)]
    [InlineData("push --force", 1)]
    [InlineData("publish ../secret", 1)]
    public async Task CommandArgumentsFailClosed(string arguments, int expected)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(expected, await CommandLine.RunAsync(arguments.Split(' '), Path.GetTempPath(), output, errors));
    }

    [Fact]
    public async Task InitValidatesInputAndWritesOnlyNonsecretConfiguration()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        File.Delete(fixture.Git.SafePath(".kanka/config.yml"));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["init", "--campaign", "bad"], fixture.Git.Root, output, errors));
        Assert.Equal(0, await CommandLine.RunAsync(["init", "--campaign", "123"], fixture.Git.Root, output, errors));
        Assert.Equal(new Configuration(123, 30), Configuration.Read(File.ReadAllText(fixture.Git.SafePath(".kanka/config.yml"))));
        Assert.Contains(".env", File.ReadAllText(fixture.Git.SafePath(".gitignore")));
        Assert.Equal(1, await CommandLine.RunAsync(["init", "--campaign", "123"], fixture.Git.Root, output, errors));
        Assert.Equal(0, await CommandLine.RunAsync(["doctor"], fixture.Git.Root, output, errors));
        Assert.Equal(0, await CommandLine.RunAsync(["validate"], fixture.Git.Root, output, errors));
        Assert.Equal(1, await CommandLine.RunAsync(["status"], fixture.Git.Root, output, errors));
    }

    [Fact]
    public async Task OfflineCommandsExposeChangesAndStageReview()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        fixture.Campaign.Records["entities/11/posts/5"]["entry"] = "<p>New player contribution.</p>";
        await fixture.Service.FetchAsync();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        foreach (var command in new[] { "validate", "doctor", "status", "diff" })
            Assert.Equal(0, await CommandLine.RunAsync([command], fixture.Git.Root, output, errors));
        Assert.Equal(0, await CommandLine.RunAsync(["pull"], fixture.Git.Root, output, errors, createGitHub: EmptyQueue));
        Assert.Contains("Alice", output.ToString());
        Assert.Contains("Push blocked", output.ToString());
        Assert.Null(await fixture.Git.ResolveAsync("MERGE_HEAD"));
        Assert.Equal(0, await CommandLine.RunAsync(["publish", "maximilian"], fixture.Git.Root, output, errors));
        Assert.Equal(0, await CommandLine.RunAsync(["delete", "maximilian-journal"], fixture.Git.Root, output, errors));
        Assert.True(fixture.Working().Resources["maximilian-journal"].Deleted);
        Assert.Equal(1, await CommandLine.RunAsync(["publish", "missing"], fixture.Git.Root, output, errors));
    }

    [Fact]
    public async Task GitRejectsUnsafePathsDirtyBranchesAndConcurrentLocks()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        Assert.Throws<SyncException>(() => fixture.Git.SafePath("../escape"));
        Assert.Throws<SyncException>(() => fixture.Git.SafePath("world\\escape"));
        Assert.Throws<SyncException>(() => fixture.Git.SafePath("/absolute"));
        using (await fixture.Git.LockAsync())
            await Assert.ThrowsAsync<IOException>(() => fixture.Git.LockAsync());
        await File.WriteAllTextAsync(fixture.Git.SafePath("dirty.txt"), "local edit");
        await Assert.ThrowsAsync<SyncException>(() => fixture.Git.EnsureCleanMainAsync());
        File.Delete(fixture.Git.SafePath("dirty.txt"));
        await fixture.Git.RequireAsync(["switch", "-c", "feature"]);
        await Assert.ThrowsAsync<SyncException>(() => fixture.Git.EnsureCleanMainAsync());
        Assert.Null(await fixture.Git.ResolveAsync("refs/heads/missing"));
    }

    [Fact]
    public async Task FetchLeavesUncommittedWorkingTreeUntouchedAndImportsRemoteDeletion()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var path = fixture.Git.SafePath("world/characters/maximilian/index.md");
        await File.AppendAllTextAsync(path, "\nUncommitted GM edit.\n");
        fixture.Campaign.Records.Remove("entities/11/posts/5");
        await fixture.Service.FetchAsync();
        Assert.Contains("Uncommitted", await File.ReadAllTextAsync(path));
        var live = WorldFiles.Read(await fixture.Git.ReadTreeAsync(GitRepository.Live));
        Assert.DoesNotContain("maximilian-journal", live.Resources.Keys);
        Assert.Contains("maximilian-journal", live.Mappings.Keys);
    }

    [Fact]
    public async Task FetchReportsProgressWithElapsedAndRemainingTime()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        using var output = new StringWriter();

        await fixture.Service.FetchAsync(output);

        Assert.Contains("Discovering Kanka resources", output.ToString());
        Assert.Contains("Fetching Kanka [####################] 5/5 (100%)", output.ToString());
        Assert.Contains("elapsed", output.ToString());
        Assert.Contains("remaining", output.ToString());
    }

    [Fact]
    public async Task StructuredPullResolvesAdjacentIndependentYamlEdits()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian-population"].Metadata["name"] = "Residents";
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        fixture.Campaign.Records["entities/11/attributes/4"]["value"] = "900000";
        await fixture.Service.FetchAsync();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["pull"], fixture.Git.Root, output, errors, createGitHub: EmptyQueue));
        var merged = fixture.Working().Resources["maximilian-population"];
        Assert.Equal("Residents", merged.Name);
        Assert.Equal("900000", merged.Metadata.Text("value"));
        Assert.Null(await fixture.Git.ResolveAsync("MERGE_HEAD"));
    }

    [Fact]
    public async Task ProseConflictsRemainInGitForReview()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync();
        var snapshot = fixture.Working();
        snapshot.Resources["maximilian-journal"] = snapshot.Resources["maximilian-journal"] with { Body = "Local prose." };
        fixture.Save(snapshot);
        await fixture.CommitAsync();
        fixture.Campaign.Records["entities/11/posts/5"]["entry"] = "<p>Remote prose.</p>";
        await fixture.Service.FetchAsync();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["pull"], fixture.Git.Root, output, errors, createGitHub: EmptyQueue));
        Assert.Contains("posts/maximilian-journal.md", await fixture.Git.RequireAsync(["diff", "--name-only", "--diff-filter=U"]));
    }

    private static GitHubIssuesClient EmptyQueue(GitRepository _) => new("owner", "repository", "test-token", new EmptyQueueHandler());

    private sealed class EmptyQueueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            });
    }
}
