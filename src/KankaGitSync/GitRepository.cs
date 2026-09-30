using System.Diagnostics;
using System.Text;

namespace KankaGitSync;

public sealed class GitRepository(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public const string Main = "refs/heads/main";
    public const string Live = "refs/heads/kanka/live";
    public const string Verified = "refs/kanka/verified";
    public const string Published = "refs/kanka/published";

    public async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(FindGit())
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Credentials are for HTTP only, never inherited by Git hooks or subprocesses.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment != null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.Environment.Remove("KANKA_TOKEN");
        start.Environment.Remove("KANKA_API_TOKEN");
        using var process = Process.Start(start) ?? throw new SyncException("Could not start Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // Observe both readers even when cancellation interrupts the process.
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
    }

    public async Task<string> RequireAsync(string[] arguments, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, input, environment, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SyncException($"Git {arguments[0]} failed. Check repository ownership, merge state, and Git configuration.");
        return result.Output.TrimEnd('\r', '\n');
    }

    public async Task<string?> ResolveAsync(string reference)
    {
        var result = await RunAsync(["rev-parse", "--verify", "--quiet", reference]).ConfigureAwait(false);
        if (result.ExitCode == 1) return null;
        if (result.ExitCode != 0) throw new SyncException("Cannot read Git references. Check repository ownership.");
        return result.Output.Trim();
    }

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant)
    {
        var result = await RunAsync(["merge-base", "--is-ancestor", ancestor, descendant]).ConfigureAwait(false);
        return result.ExitCode switch { 0 => true, 1 => false, _ => throw new SyncException("Cannot inspect Git ancestry.") };
    }

    public async Task<SortedDictionary<string, string>> ReadTreeAsync(string? reference)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (reference == null) return result;
        var listing = await RequireAsync(["ls-tree", "-rz", reference]).ConfigureAwait(false);
        var blobs = new List<(string Path, string ObjectId)>();
        foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var boundary = entry.IndexOf('\t');
            var header = entry[..boundary].Split(' ');
            var path = entry[(boundary + 1)..];
            if (!IsManaged(path)) continue;
            if (header[0] != "100644" && header[0] != "100755") throw new SyncException("Managed paths cannot be symbolic links or submodules.");
            blobs.Add((path, header[2]));
        }
        if (blobs.Count != 0) await ReadBlobsAsync(blobs, result).ConfigureAwait(false);
        return result;
    }

    private async Task ReadBlobsAsync(List<(string Path, string ObjectId)> blobs, SortedDictionary<string, string> files)
    {
        var result = await RunAsync(["cat-file", "--batch"], string.Join('\n', blobs.Select(blob => blob.ObjectId)) + "\n").ConfigureAwait(false);
        if (result.ExitCode != 0) throw new SyncException("Cannot read a Git blob.");
        var bytes = Encoding.UTF8.GetBytes(result.Output);
        var offset = 0;
        foreach (var blob in blobs)
        {
            var boundary = Array.IndexOf(bytes, (byte)'\n', offset);
            if (boundary < 0) throw new SyncException("Truncated Git object header.");
            var header = Encoding.UTF8.GetString(bytes, offset, boundary - offset).Split(' ');
            if (header.Length != 3 || header[0] != blob.ObjectId || header[1] != "blob" ||
                !int.TryParse(header[2], out var length) || length < 0 || length > bytes.Length - boundary - 2)
                throw new SyncException("Invalid Git object response.");
            files[blob.Path] = Encoding.UTF8.GetString(bytes, boundary + 1, length);
            offset = boundary + length + 2;
        }
    }

    public static bool IsManaged(string path) => path.StartsWith("world/", StringComparison.Ordinal) ||
        path.StartsWith(".kanka/", StringComparison.Ordinal) && !path.StartsWith(".kanka/runtime/", StringComparison.Ordinal);

    public async Task<string> CommitLiveAsync(SortedDictionary<string, string> files, string message)
    {
        var previous = await ResolveAsync(Live).ConfigureAwait(false);
        var parent = previous ?? await ResolveAsync(Main).ConfigureAwait(false);
        var temporary = Path.Combine(Path.GetTempPath(), "kanka-index-" + Guid.NewGuid().ToString("N"));
        var environment = TechnicalIdentity();
        environment["GIT_INDEX_FILE"] = temporary;
        try
        {
            await RequireAsync(parent == null ? ["read-tree", "--empty"] : ["read-tree", parent], environment: environment).ConfigureAwait(false);
            await ReplaceManagedAsync(parent, files, environment).ConfigureAwait(false);
            var tree = await RequireAsync(["write-tree"], environment: environment).ConfigureAwait(false);
            if (previous != null && tree == await RequireAsync(["rev-parse", previous + "^{tree}"]).ConfigureAwait(false)) return previous;
            var arguments = new List<string> { "commit-tree", tree };
            if (parent != null) arguments.AddRange(["-p", parent]);
            var commit = await RequireAsync(arguments.ToArray(), message, environment).ConfigureAwait(false);
            await UpdateRefAsync(Live, commit, previous).ConfigureAwait(false);
            return commit;
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(temporary + ".lock");
        }
    }

    private async Task ReplaceManagedAsync(string? parent, SortedDictionary<string, string> files, Dictionary<string, string> environment)
    {
        var oldFiles = await ReadTreeAsync(parent).ConfigureAwait(false);
        foreach (var path in oldFiles.Keys.Except(files.Keys))
            await RequireAsync(["update-index", "--force-remove", "--", path], environment: environment).ConfigureAwait(false);
        foreach (var pair in files)
        {
            SafePath(pair.Key);
            if (oldFiles.TryGetValue(pair.Key, out var oldContent) && oldContent == pair.Value) continue;
            var blob = await RequireAsync(["hash-object", "-w", "--stdin"], pair.Value).ConfigureAwait(false);
            await RequireAsync(["update-index", "--add", "--cacheinfo", "100644", blob, pair.Key], environment: environment).ConfigureAwait(false);
        }
    }

    public Task<string> UpdateRefAsync(string reference, string commit, string? expected) =>
        RequireAsync(["update-ref", reference, commit, expected ?? new string('0', commit.Length)]);

    public async Task EnsureCleanMainAsync()
    {
        if (await RequireAsync(["symbolic-ref", "HEAD"]).ConfigureAwait(false) != Main)
            throw new SyncException("This operation requires the main branch.");
        if ((await RequireAsync(["status", "--porcelain"]).ConfigureAwait(false)).Length != 0)
            throw new SyncException("Commit or stash working-tree changes first.");
    }

    public string SafePath(string relative)
    {
        if (relative.Contains('\\') || Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is ".." or "." or ""))
            throw new SyncException("Unsafe repository path.");
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new SyncException("Path escapes repository.");
        var current = Root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new SyncException("Symbolic links are not allowed in managed paths.");
        }
        return path;
    }

    public async Task<FileStream> LockAsync()
    {
        var common = await RequireAsync(["rev-parse", "--git-common-dir"]).ConfigureAwait(false);
        var directory = Path.GetFullPath(common, Root);
        return new FileStream(Path.Combine(directory, "kanka-sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static Dictionary<string, string> TechnicalIdentity() => new()
    {
        ["GIT_AUTHOR_NAME"] = "Kanka Sync",
        ["GIT_AUTHOR_EMAIL"] = "kanka-sync@localhost",
        ["GIT_COMMITTER_NAME"] = "Kanka Sync",
        ["GIT_COMMITTER_EMAIL"] = "kanka-sync@localhost"
    };

    private static string FindGit()
    {
        var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        throw new SyncException("Git was not found on PATH.");
    }
}
