namespace KankaGitSync;

public static class EnvironmentFile
{
    public static async Task InitializeAsync(GitRepository repository, TextWriter output, Func<string?> readToken, CancellationToken cancellationToken)
    {
        var path = repository.SafePath(".env");
        var tracked = await repository.RequireAsync(["ls-files", "--", ".env"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tracked.Length != 0) throw new SyncException(".env is tracked by Git. Remove it from the index before running init-env.");
        if (Directory.Exists(path)) throw new SyncException("Cannot create .env: a directory already exists at that path.");
        if (File.Exists(path))
        {
            await EnsureIgnoredAsync(repository, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(".env already exists in the world repository root; its contents were preserved. Git ignore protection is in place.").ConfigureAwait(false);
            return;
        }
        await output.WriteAsync("Kanka API token (input hidden): ").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var token = readToken();
        await output.WriteLineAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var value = FormatToken(token);
        await EnsureIgnoredAsync(repository, cancellationToken).ConfigureAwait(false);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
            await writer.WriteAsync("# Local Kanka API credentials. Keep this file out of Git.\nKANKA_API_TOKEN=" + value + "\n").ConfigureAwait(false);
        await output.WriteLineAsync("Saved token to .env in the world repository root. Review and commit any .gitignore change.").ConfigureAwait(false);
    }

    private static string FormatToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new SyncException("No valid token entered. Enter a nonblank single-line token.");
        token = token.Trim();
        if (!token.Contains('"')) return "\"" + token + "\"";
        if (!token.Contains('\'')) return "'" + token + "'";
        throw new SyncException("Token contains unsupported quote characters. No credentials were saved.");
    }

    private static async Task EnsureIgnoredAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        var result = await repository.RunAsync(["check-ignore", "-q", "--", ".env"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return;
        if (result.ExitCode != 1) throw new SyncException("Could not check .env Git ignore protection.");
        await File.AppendAllTextAsync(repository.SafePath(".gitignore"), "\n# Local Kanka API credentials\n/.env\n", cancellationToken).ConfigureAwait(false);
    }
}
