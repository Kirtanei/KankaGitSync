using System.IO.Compression;
using System.Net;
using System.Text;

namespace KankaGitSync.Tests;

public sealed class ToolUpdaterTests
{
    private const string Token = "test-update-token";

    [Fact]
    public async Task UpdateDownloadsValidPackageAndInstallsExactVersion()
    {
        var processArguments = new List<string>();
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            }
            else Assert.Null(request.Headers.Authorization);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/repos/Kirtanei/KankaGitSync/releases/latest" => JsonResponse(Release("0.1.5")),
                "/repos/Kirtanei/KankaGitSync/releases/assets/42" => Redirect("https://release-assets.githubusercontent.com/download/test"),
                _ when request.RequestUri.Host == "release-assets.githubusercontent.com" => PackageResponse(Package("0.1.5")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        });
        using var updater = new ToolUpdater("0.1.4", handler, (start, _) =>
        {
            processArguments.AddRange(start.ArgumentList);
            return Task.FromResult(0);
        });

        var result = await updater.UpdateAsync(Token, default);

        Assert.True(result.Updated);
        Assert.Equal("0.1.5", result.Version);
        Assert.Equal(["tool", "update", "--global", "--add-source", processArguments[4], "--version", "0.1.5", "KankaGitSync"], processArguments);
        Assert.False(Directory.Exists(processArguments[4]));
    }

    [Fact]
    public async Task AlreadyCurrentReleaseDoesNotDownloadOrInstall()
    {
        var requests = 0;
        using var handler = new Handler(_ =>
        {
            requests++;
            return Task.FromResult(JsonResponse(Release("0.1.5")));
        });
        using var updater = new ToolUpdater("0.1.5", handler, (_, _) => throw new InvalidOperationException("Must not install"));

        var result = await updater.UpdateAsync(Token, default);

        Assert.False(result.Updated);
        Assert.Equal("0.1.5", result.Version);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task UnsafeRedirectIsRejectedWithoutInstalling()
    {
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)
            ? JsonResponse(Release("0.1.5"))
            : Redirect("http://example.invalid/package")));
        using var updater = new ToolUpdater("0.1.4", handler, (_, _) => throw new InvalidOperationException("Must not install"));

        await Assert.ThrowsAsync<SyncException>(() => updater.UpdateAsync(Token, default));
    }

    [Fact]
    public async Task InvalidPackageIdentityIsRejectedWithoutInstalling()
    {
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)
            ? JsonResponse(Release("0.1.5"))
            : PackageResponse(Package("0.1.4"))));
        using var updater = new ToolUpdater("0.1.3", handler, (_, _) => throw new InvalidOperationException("Must not install"));

        var exception = await Assert.ThrowsAsync<SyncException>(() => updater.UpdateAsync(Token, default));

        Assert.Contains("identity", exception.Message);
    }

    [Fact]
    public async Task FailedInstallerKeepsExistingToolAndCleansTemporaryPackage()
    {
        string? packageDirectory = null;
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)
            ? JsonResponse(Release("0.1.5"))
            : PackageResponse(Package("0.1.5"))));
        using var updater = new ToolUpdater("0.1.4", handler, (start, _) =>
        {
            packageDirectory = start.ArgumentList[4];
            return Task.FromResult(1);
        });

        var exception = await Assert.ThrowsAsync<SyncException>(() => updater.UpdateAsync(Token, default));

        Assert.Contains("unchanged", exception.Message);
        Assert.NotNull(packageDirectory);
        Assert.False(Directory.Exists(packageDirectory));
    }

    [Theory]
    [InlineData("0.1.5")]
    [InlineData("v0.1")]
    [InlineData("v0.1.5-preview")]
    [InlineData("v0.1.5.1")]
    public async Task NonStableReleaseTagsAreRejected(string tag)
    {
        using var handler = new Handler(_ => Task.FromResult(JsonResponse(ReleaseWithTag(tag))));
        using var updater = new ToolUpdater("0.1.4", handler);

        await Assert.ThrowsAsync<SyncException>(() => updater.UpdateAsync(Token, default));
    }

    [Fact]
    public async Task UpdateCommandRunsOutsideGitRepository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kanka-update-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await CommandLine.RunAsync(["update"], directory, output, error, createUpdater: () =>
                new ToolUpdater("0.1.5", new Handler(_ => Task.FromResult(JsonResponse(Release("0.1.5"))))), readEnvironment: _ => Token));
            Assert.Contains("already up to date", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void MissingMachineTokenFailsWithoutReadingRepository()
    {
        var exception = Assert.Throws<SyncException>(() => TokenConfiguration.ReadGitHubEnvironment(_ => null));
        Assert.Contains("machine environment", exception.Message);
    }

    private static string Release(string version) => $$"""
        {"tag_name":"v{{version}}","draft":false,"prerelease":false,"assets":[{"name":"KankaGitSync.{{version}}.nupkg","url":"https://api.github.com/repos/Kirtanei/KankaGitSync/releases/assets/42"}]}
        """;

    private static string ReleaseWithTag(string tag) => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":false,"assets":[]}
        """;

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Redirect(string address)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(address);
        return response;
    }

    private static HttpResponseMessage PackageResponse(byte[] package) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(package)
    };

    private static byte[] Package(string version)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("KankaGitSync.nuspec");
            using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8);
            writer.Write($"<package><metadata><id>KankaGitSync</id><version>{version}</version></metadata></package>");
        }
        return bytes.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
