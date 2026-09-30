namespace KankaGitSync;

public static class TokenConfiguration
{
    private static readonly string[] Names = ["KANKA_TOKEN", "KANKA_API_TOKEN"];

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

    private static void ReadEntry(string line, Dictionary<string, string> values)
    {
        var entry = line.Trim();
        if (entry.StartsWith("export ", StringComparison.Ordinal) || entry.StartsWith("export\t", StringComparison.Ordinal))
            entry = entry[6..].TrimStart();
        var separator = entry.IndexOfAny(['=', ' ', '\t', '#']);
        var name = separator < 0 ? entry : entry[..separator];
        if (!Names.Contains(name, StringComparer.Ordinal)) return;
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
