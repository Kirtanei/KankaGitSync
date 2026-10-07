using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;

namespace KankaGitSync.Domain;

public static partial class ContentCodec
{
    public static string Import(string html, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.PassThrough },
            GithubFlavored = true
        });
        var candidate = converter.Convert(html).ReplaceLineEndings("\n").TrimEnd();
        // A lossy converter is never authoritative: retain original HTML on any mismatch.
        var preserved = NormalizeHtml(Markdown.ToHtml(candidate)) == NormalizeHtml(html) ? candidate : html.TrimEnd();
        return ImportReferences(preserved, mappings).ReplaceLineEndings("\n");
    }

    public static string ImportReferences(string text, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var entityIds = mappings.Where(pair => pair.Value.Kind == "entity")
            .ToDictionary(pair => pair.Value.EntityId, pair => pair.Key);
        return RemoteReference().Replace(text, match =>
        {
            var identifier = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!entityIds.TryGetValue(identifier, out var localId)) return match.Value;
            return "[[" + localId + match.Groups[2].Value + "]]";
        });
    }

    public static string Export(string markdown, IReadOnlyDictionary<string, Mapping> mappings)
    {
        var expanded = ExportReferences(markdown, mappings);
        var trimmed = expanded.Trim();
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>')) return trimmed.ReplaceLineEndings("\n");
        return Markdown.ToHtml(expanded).ReplaceLineEndings("\n").TrimEnd();
    }

    public static string ExportReferences(string text, IReadOnlyDictionary<string, Mapping> mappings) =>
        LocalReference().Replace(text, match =>
        {
            if (!mappings.TryGetValue(match.Groups[1].Value, out var mapping) || mapping.Kind != "entity")
                throw new SyncException("Reference does not have a published entity mapping.");
            return "[entity:" + mapping.EntityId.ToString(CultureInfo.InvariantCulture) + match.Groups[2].Value + "]";
        });

    public static string NormalizeHtml(string html) => html.ReplaceLineEndings("\n").Trim();

    [GeneratedRegex(@"\[entity:(\d+)(\|[^\]|:<>\r\n]+)?\]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RemoteReference();
    [GeneratedRegex(@"\[\[([a-z0-9]+(?:-[a-z0-9]+)*)(\|[^\]|<>\r\n]+)?\]\]", RegexOptions.CultureInvariant, 1000)]
    public static partial Regex LocalReference();
}
