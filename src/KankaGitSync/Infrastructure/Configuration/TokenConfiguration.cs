namespace KankaGitSync.Infrastructure.Configuration;

public static class TokenConfiguration
{
    private static readonly string[] Names = ["KANKA_TOKEN", "KANKA_API_TOKEN"];
    private const string GitHubName = "KANKA_GITHUB_TOKEN";

    public static string Read(GitRepository repository, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var token = SelectToken(readEnvironment);
        if (token != null) return token;
        var path = repository.SafePath(".env");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path)) ReadEntry(line, values);
        return SelectToken(name => values.GetValueOrDefault(name))
            ?? throw new SyncException("Set KANKA_API_TOKEN or KANKA_TOKEN in the world repository's .env file or environment.");
    }

    public static string ReadGitHub(GitRepository repository, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var token = readEnvironment(GitHubName);
        if (!string.IsNullOrWhiteSpace(token)) return ValidateGitHub(token);
        var path = repository.SafePath(".env");
        if (File.Exists(path))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path)) ReadEntry(line, values, GitHubName);
            if (values.TryGetValue(GitHubName, out token)) return ValidateGitHub(token);
        }
        throw new SyncException("Set KANKA_GITHUB_TOKEN in the world repository's .env file or environment.");
    }

    public static string ReadGitHubEnvironment(Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var token = readEnvironment(GitHubName);
        if (!string.IsNullOrWhiteSpace(token)) return ValidateGitHub(token);
        throw new SyncException("Set KANKA_GITHUB_TOKEN as a machine environment variable with Contents: Read access to Kirtanei/KankaGitSync.");
    }

    private static string? SelectToken(Func<string, string?> readValue)
    {
        foreach (var name in Names)
        {
            var value = readValue(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Any(char.IsControl)) throw InvalidEntry();
            return value;
        }
        return null;
    }

    private static string ValidateGitHub(string token)
    {
        if (token.Any(char.IsControl)) throw InvalidEntry();
        return token;
    }

    private static void ReadEntry(string line, Dictionary<string, string> values, params string[] additionalNames)
    {
        var entry = line.Trim();
        if (entry.StartsWith("export ", StringComparison.Ordinal) || entry.StartsWith("export\t", StringComparison.Ordinal))
            entry = entry[6..].TrimStart();
        var separator = entry.IndexOfAny(['=', ' ', '\t', '#']);
        var name = separator < 0 ? entry : entry[..separator];
        if (!Names.Contains(name, StringComparer.Ordinal) && !additionalNames.Contains(name, StringComparer.Ordinal)) return;
        var remainder = entry[name.Length..].TrimStart();
        if (!remainder.StartsWith('=')) throw InvalidEntry();
        values[name] = ParseValue(remainder[1..].Trim());
    }

    private static string ParseValue(string value)
    {
        if (value.Length == 0) return "";
        if (value[0] is not ('\'' or '"')) return value.Split('#', 2)[0].TrimEnd();
        var closing = value.IndexOf(value[0], 1);
        if (closing < 0) throw InvalidEntry();
        var trailing = value[(closing + 1)..].TrimStart();
        if (trailing.Length != 0 && !trailing.StartsWith('#')) throw InvalidEntry();
        return value[1..closing];
    }

    private static SyncException InvalidEntry() => new("Invalid token setting. Use a single-line KANKA_API_TOKEN or KANKA_TOKEN value; credential contents are not logged.");
}
