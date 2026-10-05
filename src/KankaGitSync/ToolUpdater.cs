using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace KankaGitSync;

public sealed record ToolUpdateResult(bool Updated, string Version);

public sealed class ToolUpdater : IDisposable
{
    private const string Owner = "Kirtanei";
    private const string Repository = "KankaGitSync";
    private const string PackageId = "KankaGitSync";
    private const long MaximumPackageBytes = 100L * 1024 * 1024;
    private static readonly Uri ApiAddress = new UriBuilder(Uri.UriSchemeHttps, "api.github.com").Uri;
    private readonly HttpClient client;
    private readonly string currentVersion;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runProcessAsync;

    public ToolUpdater(string currentVersion, HttpMessageHandler? handler = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runProcessAsync = null)
    {
        this.currentVersion = VersionNumber.Parse(currentVersion).ToString();
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = ApiAddress,
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = MaximumPackageBytes
        };
        this.runProcessAsync = runProcessAsync ?? RunProcessAsync;
    }

    public static string CurrentVersion()
    {
        var version = typeof(ToolUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ToolUpdater).Assembly.GetName().Version?.ToString(3)
            ?? throw new SyncException("Cannot determine the installed Git Kanka version.");
        return VersionNumber.Parse(version.Split('+', 2)[0]).ToString();
    }

    public async Task<ToolUpdateResult> UpdateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new SyncException("Set KANKA_GITHUB_TOKEN as a machine environment variable with Contents: Read access to Kirtanei/KankaGitSync.");
        var release = await LatestReleaseAsync(token, cancellationToken).ConfigureAwait(false);
        if (release.Version.CompareTo(VersionNumber.Parse(currentVersion)) <= 0)
            return new ToolUpdateResult(false, release.Version.ToString());

        var directory = Path.Combine(Path.GetTempPath(), "kanka-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, release.PackageName);
        try
        {
            await DownloadPackageAsync(release.AssetUri, token, packagePath, cancellationToken).ConfigureAwait(false);
            ValidatePackage(packagePath, release.Version);
            var exitCode = await runProcessAsync(UpdateProcess(directory, release.Version), cancellationToken).ConfigureAwait(false);
            if (exitCode != 0) throw new SyncException("Git Kanka update could not be installed. The current version is unchanged.");
            return new ToolUpdateResult(true, release.Version.ToString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<Release> LatestReleaseAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"repos/{Owner}/{Repository}/releases/latest", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SyncException("Could not read the latest Git Kanka release. Check KANKA_GITHUB_TOKEN access; no response body logged.");
        var release = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new SyncException("GitHub returned an invalid release response.");
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true)
            throw new SyncException("GitHub returned an unsupported prerelease.");
        var tag = release.Text("tag_name");
        if (!tag.StartsWith('v')) throw new SyncException("GitHub release version must use vMAJOR.MINOR.PATCH.");
        var version = VersionNumber.Parse(tag[1..]);
        var packageName = $"{PackageId}.{version}.nupkg";
        var assets = release["assets"] as JsonArray ?? throw new SyncException("GitHub release has no package assets.");
        var asset = assets.OfType<JsonObject>().SingleOrDefault(item => item.Text("name") == packageName)
            ?? throw new SyncException("Latest GitHub release is missing its expected package asset.");
        if (!Uri.TryCreate(asset.Text("url"), UriKind.Absolute, out var assetUri) || !IsAssetApiUri(assetUri))
            throw new SyncException("GitHub release package URL is invalid.");
        return new Release(version, packageName, assetUri);
    }

    private async Task DownloadPackageAsync(Uri assetUri, string token, string packagePath, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, assetUri, token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is >= System.Net.HttpStatusCode.MultipleChoices and < System.Net.HttpStatusCode.BadRequest)
        {
            var location = response.Headers.Location;
            if (location == null || !IsDownloadUri(location)) throw new SyncException("GitHub release download redirect is invalid.");
            await DownloadRedirectAsync(location, packagePath, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccessStatusCode) throw new SyncException("Could not download the Git Kanka release package. The current version is unchanged.");
        await WritePackageAsync(response, packagePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadRedirectAsync(Uri address, string packagePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SyncException("Could not download the Git Kanka release package. The current version is unchanged.");
        await WritePackageAsync(response, packagePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePackageAsync(HttpResponseMessage response, string packagePath, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new SyncException("GitHub release package exceeds the safe size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > MaximumPackageBytes) throw new SyncException("GitHub release package exceeds the safe size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidatePackage(string packagePath, VersionNumber version)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var manifest = archive.Entries.SingleOrDefault(entry => entry.FullName == PackageId + ".nuspec")
                ?? throw new SyncException("GitHub release package has no valid manifest.");
            using var stream = manifest.Open();
            var document = XDocument.Load(stream, LoadOptions.None);
            var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
            var identity = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "id")?.Value;
            var packageVersion = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
            if (identity != PackageId || VersionNumber.Parse(packageVersion ?? "") != version)
                throw new SyncException("GitHub release package identity does not match the release.");
        }
        catch (InvalidDataException exception)
        {
            throw new SyncException("GitHub release package is invalid.", exception);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new SyncException("GitHub release package manifest is invalid.", exception);
        }
    }

    private static ProcessStartInfo UpdateProcess(string packageDirectory, VersionNumber version)
    {
        var start = new ProcessStartInfo(FindDotnet()) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("tool");
        start.ArgumentList.Add("update");
        start.ArgumentList.Add("--global");
        start.ArgumentList.Add("--add-source");
        start.ArgumentList.Add(packageDirectory);
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add(version.ToString());
        start.ArgumentList.Add(PackageId);
        return start;
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(start) ?? throw new SyncException("Could not start the dotnet tool updater.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new SyncException("Could not start the dotnet tool updater.", exception);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token) => Request(method, new Uri(ApiAddress, path), token);

    private static HttpRequestMessage Request(HttpMethod method, Uri address, string token)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("kanka-git-sync");
        return request;
    }

    private static bool IsAssetApiUri(Uri address) => address.Scheme == Uri.UriSchemeHttps && address.Host == "api.github.com" &&
        address.Port == 443 && address.UserInfo.Length == 0 && address.AbsolutePath.StartsWith($"/repos/{Owner}/{Repository}/releases/assets/", StringComparison.Ordinal) &&
        long.TryParse(address.AbsolutePath.Split('/')[^1], out var assetId) && assetId > 0;

    private static bool IsDownloadUri(Uri address) => address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && address.Port == 443 &&
        address.UserInfo.Length == 0 && (address.Host == "github-releases.githubusercontent.com" || address.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));

    private static string FindDotnet()
    {
        var current = Environment.ProcessPath;
        if (current != null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        throw new SyncException("dotnet was not found on PATH.");
    }

    public void Dispose() => client.Dispose();

    private sealed record Release(VersionNumber Version, string PackageName, Uri AssetUri);

    private sealed record VersionNumber(int Major, int Minor, int Patch) : IComparable<VersionNumber>
    {
        public static VersionNumber Parse(string value)
        {
            var parts = value.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
                !int.TryParse(parts[2], out var patch) || major < 0 || minor < 0 || patch < 0)
                throw new SyncException("GitHub release version must use vMAJOR.MINOR.PATCH.");
            return new VersionNumber(major, minor, patch);
        }

        public int CompareTo(VersionNumber? other) => other == null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }
}
