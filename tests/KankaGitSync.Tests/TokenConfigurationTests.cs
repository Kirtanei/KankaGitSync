namespace KankaGitSync.Tests;

public sealed class TokenConfigurationTests
{
    [Theory]
    [InlineData("KANKA_TOKEN=legacy", "legacy")]
    [InlineData("KANKA_API_TOKEN=plugin", "plugin")]
    [InlineData("KANKA_TOKEN=legacy\nKANKA_API_TOKEN=plugin", "legacy")]
    [InlineData("KANKA_TOKEN=  \nKANKA_API_TOKEN=plugin", "plugin")]
    [InlineData("# comment\n export\tKANKA_API_TOKEN = 'quoted#value' # comment", "quoted#value")]
    [InlineData("KANKA_TOKEN=\"quoted\" # comment", "quoted")]
    [InlineData("KANKA_TOKEN=plain # comment", "plain")]
    [InlineData("UNRELATED=\"unfinished\nKANKA_TOKEN=$(literal)${VALUE}", "$(literal)${VALUE}")]
    [InlineData("KANKA_TOKEN=first\nKANKA_TOKEN=last", "last")]
    public void ReadsSupportedFileSyntax(string contents, string expected)
    {
        using var fixture = new TestRepository();
        File.WriteAllText(fixture.Git.SafePath(".env"), contents);
        Assert.Equal(expected, TokenConfiguration.Read(fixture.Git, _ => null));
        Assert.Equal(contents, File.ReadAllText(fixture.Git.SafePath(".env")));
    }

    [Theory]
    [InlineData("shell-legacy", "shell-plugin", "shell-legacy")]
    [InlineData("", "shell-plugin", "shell-plugin")]
    [InlineData(" ", null, "file-legacy")]
    public void ShellValuesOverrideBothFileNames(string legacy, string? plugin, string expected)
    {
        using var fixture = new TestRepository();
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_TOKEN=file-legacy\nKANKA_API_TOKEN=file-plugin");
        Assert.Equal(expected, TokenConfiguration.Read(fixture.Git, name => name == "KANKA_TOKEN" ? legacy : plugin));
    }

    [Theory]
    [InlineData("KANKA_TOKEN secret-marker")]
    [InlineData("KANKA_TOKEN='secret-marker")]
    [InlineData("KANKA_API_TOKEN=\"secret-marker\" trailing")]
    [InlineData("KANKA_TOKEN=secret-marker\tvalue")]
    public void MalformedEntriesNeverExposeContents(string contents)
    {
        using var fixture = new TestRepository();
        File.WriteAllText(fixture.Git.SafePath(".env"), contents);
        var exception = Assert.Throws<SyncException>(() => TokenConfiguration.Read(fixture.Git, _ => null));
        Assert.DoesNotContain("secret-marker", exception.Message);
        Assert.Equal("shell", TokenConfiguration.Read(fixture.Git, _ => "shell"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("KANKA_TOKEN=\nKANKA_API_TOKEN=' '")]
    public void MissingCredentialsExplainPersistentSetup(string? contents)
    {
        using var fixture = new TestRepository();
        if (contents != null) File.WriteAllText(fixture.Git.SafePath(".env"), contents);
        var exception = Assert.Throws<SyncException>(() => TokenConfiguration.Read(fixture.Git, _ => null));
        Assert.Contains(".env", exception.Message);
    }

    [Fact]
    public async Task InitPreservesCredentialsAndIgnoresFileWhileAllowingExample()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        File.Delete(fixture.Git.SafePath(".kanka/config.yml"));
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_API_TOKEN=preserved");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["init", "--campaign", "123"], fixture.Git.Root, output, error));
        Assert.Equal("KANKA_API_TOKEN=preserved", File.ReadAllText(fixture.Git.SafePath(".env")));
        Assert.Equal(0, (await fixture.Git.RunAsync(["check-ignore", "-q", ".env"])).ExitCode);
        Assert.Equal(1, (await fixture.Git.RunAsync(["check-ignore", "-q", ".env.example"])).ExitCode);
    }

    [Fact]
    public async Task SubfolderResolvesRootCredentialsAndOfflineCommandsIgnoreMalformedFile()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        var child = fixture.Git.SafePath("nested");
        Directory.CreateDirectory(child);
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_API_TOKEN=root-token");
        File.WriteAllText(Path.Combine(child, ".env"), "KANKA_API_TOKEN=wrong-token");
        var root = await new GitRepository(child).RequireAsync(["rev-parse", "--show-toplevel"]);
        Assert.Equal("root-token", TokenConfiguration.Read(new GitRepository(root), _ => null));
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_TOKEN='secret-marker");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["doctor"], child, output, error));
        Assert.DoesNotContain("secret-marker", output.ToString() + error);
    }

    [Fact]
    public async Task GitSubprocessCannotReceiveEitherTokenName()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        var environment = new Dictionary<string, string>
        {
            ["KANKA_TOKEN"] = "legacy-secret",
            ["KANKA_API_TOKEN"] = "plugin-secret"
        };
        var result = await fixture.Git.RunAsync(["-c", "alias.check-credentials=!test -z \"$KANKA_TOKEN$KANKA_API_TOKEN\"", "check-credentials"], environment: environment);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task OnlineCommandReadsRootFileFromSubfolderWithoutExposingMalformedToken()
    {
        using var fixture = new TestRepository();
        await fixture.InitializeAsync(false);
        var child = fixture.Git.SafePath("nested");
        Directory.CreateDirectory(child);
        File.WriteAllText(fixture.Git.SafePath(".env"), "KANKA_TOKEN='secret-marker");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = child,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CommandLine).Assembly.Location);
        start.ArgumentList.Add("fetch");
        start.ArgumentList.Add("--full");
        start.Environment.Remove("KANKA_TOKEN");
        start.Environment.Remove("KANKA_API_TOKEN");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Invalid token setting", await error);
        Assert.DoesNotContain("secret-marker", await output + await error);
    }
}
