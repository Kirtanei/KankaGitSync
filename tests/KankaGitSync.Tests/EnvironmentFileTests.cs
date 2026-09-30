namespace KankaGitSync.Tests;

public sealed class EnvironmentFileTests
{
    [Fact]
    public async Task CreatesIgnoredRootFileBeforeCampaignSetupAndPreservesItOnRepeat()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        File.Delete(fixture.Git.SafePath(".kanka/config.yml"));
        var child = fixture.Git.SafePath("nested");
        Directory.CreateDirectory(child);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["init-env"], child, output, error, readToken: () => "pasted-secret"));
        Assert.Equal("pasted-secret", TokenConfiguration.Read(fixture.Git, _ => null));
        Assert.DoesNotContain("pasted-secret", output.ToString() + error);
        Assert.False(File.Exists(Path.Combine(child, ".env")));
        Assert.Equal(0, (await fixture.Git.RunAsync(["check-ignore", "-q", ".env"])).ExitCode);
        var ignore = File.ReadAllText(fixture.Git.SafePath(".gitignore"));
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_API_TOKEN=secret-marker");
        Assert.Equal(0, await CommandLine.RunAsync(["init-env"], child, output, error, readToken: () => throw new InvalidOperationException("Must not prompt")));
        Assert.Equal("KANKA_API_TOKEN=secret-marker", File.ReadAllText(fixture.Git.SafePath(".env")));
        Assert.Equal(ignore, File.ReadAllText(fixture.Git.SafePath(".gitignore")));
        Assert.DoesNotContain("secret-marker", output.ToString() + error);
    }

    [Fact]
    public async Task ProtectsExistingUnignoredFileWithoutReadingCredentials()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        File.WriteAllText(fixture.Git.SafePath(".env"), "existing-secret");
        File.WriteAllText(fixture.Git.SafePath(".gitignore"), "# existing rules\n!.env\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["init-env"], fixture.Git.Root, output, error));
        Assert.Equal("existing-secret", File.ReadAllText(fixture.Git.SafePath(".env")));
        Assert.StartsWith("# existing rules\n!.env\n", File.ReadAllText(fixture.Git.SafePath(".gitignore")));
        Assert.Equal(0, (await fixture.Git.RunAsync(["check-ignore", "-q", ".env"])).ExitCode);
        Assert.DoesNotContain("existing-secret", output.ToString() + error);
    }

    [Fact]
    public async Task RefusesTrackedCredentialsWithoutModifyingFiles()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        File.WriteAllText(fixture.Git.SafePath(".env"), "tracked-test-secret");
        await fixture.Git.RequireAsync(["add", ".env"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["init-env"], fixture.Git.Root, output, error));
        Assert.Contains("tracked by Git", error.ToString());
        Assert.DoesNotContain("tracked-test-secret", output.ToString() + error);
        Assert.False(File.Exists(fixture.Git.SafePath(".gitignore")));
        Assert.Equal("tracked-test-secret", File.ReadAllText(fixture.Git.SafePath(".env")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("secret\nmarker")]
    [InlineData("secret\"'marker")]
    public async Task InvalidInputDoesNotCreateFilesOrExposeToken(string? token)
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["init-env"], fixture.Git.Root, output, error, readToken: () => token));
        Assert.False(File.Exists(fixture.Git.SafePath(".env")));
        Assert.False(File.Exists(fixture.Git.SafePath(".gitignore")));
        Assert.DoesNotContain("secret", output.ToString() + error);
    }

    [Theory]
    [InlineData("hash#value")]
    [InlineData("quote\"value")]
    public async Task PastedTokensRoundTripLiterally(string token)
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["init-env"], fixture.Git.Root, output, error, readToken: () => token));
        Assert.Equal(token, TokenConfiguration.Read(fixture.Git, _ => null));
        Assert.DoesNotContain(token, output.ToString() + error);
    }

    [Theory]
    [InlineData("init-env extra")]
    [InlineData("init-env --token secret-marker")]
    public async Task RejectsArguments(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(command.Split(' '), "unused", output, error));
        Assert.DoesNotContain("secret-marker", error.ToString());
    }

    [Fact]
    public async Task CancelledPromptDoesNotCreateFiles()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(130, await CommandLine.RunAsync(["init-env"], fixture.Git.Root, output, error,
            readToken: () => throw new OperationCanceledException()));
        Assert.False(File.Exists(fixture.Git.SafePath(".env")));
        Assert.False(File.Exists(fixture.Git.SafePath(".gitignore")));
    }
}
