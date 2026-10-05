using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace KankaGitSync;

public sealed record QueueEvent(long IssueNumber, string Event, string Endpoint, long ChildId);

public sealed class GitHubIssuesClient(string owner, string repository, string token, HttpMessageHandler? handler = null) : IDisposable
{
    private const string QueueLabel = "kanka-webhook";
    private const string Marker = "<!-- kanka-webhook-queue:v1 -->";
    private static readonly Uri GitHubApiAddress = new UriBuilder(Uri.UriSchemeHttps, "api.github.com").Uri;
    private readonly HttpClient client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = GitHubApiAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<IReadOnlyList<QueueEvent>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<QueueEvent>();
        for (var page = 1; page <= 100; page++)
        {
            using var request = Request(HttpMethod.Get, $"repos/{owner}/{repository}/issues?state=open&labels={QueueLabel}&per_page=100&page={page}");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SyncException("Could not read GitHub webhook queue. No response body logged.");
            var issues = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new SyncException("GitHub webhook queue returned invalid data.");
            foreach (var issue in issues) result.Add(Parse(issue as JsonObject ?? throw new SyncException("GitHub webhook queue item is invalid.")));
            if (issues.Count < 100) return result;
        }
        throw new SyncException("GitHub webhook queue pagination exceeded the safe limit.");
    }

    public async Task CloseAsync(IEnumerable<long> issueNumbers, CancellationToken cancellationToken)
    {
        foreach (var issueNumber in issueNumbers.Distinct().Order())
        {
            using var request = Request(HttpMethod.Patch, $"repos/{owner}/{repository}/issues/{issueNumber}");
            request.Content = JsonContent.Create(new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" });
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SyncException("Could not close a GitHub webhook queue issue. The merge was committed; retry pull after checking GitHub access.");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("kanka-git-sync");
        return request;
    }

    private static QueueEvent Parse(JsonObject issue)
    {
        var number = issue.Number("number");
        var body = issue.Text("body");
        if (number <= 0 || !body.StartsWith(Marker, StringComparison.Ordinal)) throw new SyncException("GitHub webhook queue issue has an invalid marker.");
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var eventValue = lines.SingleOrDefault(line => line.StartsWith("event=", StringComparison.Ordinal))?[6..];
        var url = lines.SingleOrDefault(line => line.StartsWith("url=", StringComparison.Ordinal))?[4..];
        if (eventValue is not ("created" or "edited" or "deleted") || !Uri.TryCreate(url, UriKind.Absolute, out var address))
            throw new SyncException("GitHub webhook queue issue has an invalid event.");
        var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (address.Scheme != Uri.UriSchemeHttps || address.Host != "app.kanka.io" || segments.Length < 2 ||
            !Categories.Endpoints.Values.Contains(segments[^2], StringComparer.Ordinal) ||
            !long.TryParse(segments[^1], out var childId) || childId <= 0)
            throw new SyncException("GitHub webhook queue issue has an unsupported Kanka URL.");
        return new QueueEvent(number, eventValue, segments[^2], childId);
    }

    public void Dispose() => client.Dispose();
}
