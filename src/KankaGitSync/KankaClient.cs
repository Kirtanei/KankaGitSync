using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KankaGitSync;

public interface IKankaClient
{
    Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken cancellationToken);
    Task<JsonObject> WriteAsync(string path, JsonObject body, bool create, CancellationToken cancellationToken);
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

public sealed class KankaClient : IKankaClient, IDisposable
{
    private const int MaximumAttempts = 4;
    private const long MaximumResponseBytes = 32 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri campaignUri;
    private readonly string token;
    private readonly TimeSpan interval;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;

    public KankaClient(long campaignId, string token, int requestsPerMinute = 30, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        if (campaignId <= 0 || requestsPerMinute is < 1 or > 90) throw new SyncException("Invalid campaign or request limit.");
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new SyncException("Set KANKA_API_TOKEN or KANKA_TOKEN to a valid bearer token.");
        this.token = token;
        this.delayAsync = delayAsync ?? Task.Delay;
        campaignUri = new Uri($"https://api.kanka.io/1.0/campaigns/{campaignId}/");
        interval = TimeSpan.FromMinutes(1.0 / requestsPerMinute);
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = MaximumResponseBytes
        };
    }

    public async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken) =>
        (await RequestAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false))["data"] as JsonObject
        ?? throw new SyncException("API returned an invalid object response.");

    public async Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var result = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = path;
        while (next != null)
        {
            if (!visited.Add(next) || visited.Count > 10000) throw new SyncException("Invalid API pagination cycle.");
            var page = await RequestAsync(HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            if (page["data"] is not JsonArray items) throw new SyncException("API returned an invalid collection.");
            foreach (var item in items)
                result.Add(item as JsonObject ?? throw new SyncException("Invalid API collection item."));
            next = page["links"]?["next"]?.GetValue<string>();
        }
        return result;
    }

    public async Task<JsonObject> WriteAsync(string path, JsonObject body, bool create, CancellationToken cancellationToken)
    {
        if (create && path.EndsWith("/relations", StringComparison.Ordinal))
            return await CreateRelationAsync(path, body, cancellationToken).ConfigureAwait(false);
        var response = await RequestAsync(create ? HttpMethod.Post : HttpMethod.Patch, path, body, cancellationToken).ConfigureAwait(false);
        return response["data"] as JsonObject ?? throw new SyncException("API write returned no object; refetch before retrying.");
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var address = SafeUri(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendDeleteWithBackoffAsync(address, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonObject> CreateRelationAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        var previous = (await ListAsync(path, cancellationToken).ConfigureAwait(false))
            .Select(value => KankaAdapter.PositiveId(value, "id")).ToHashSet();
        // Kanka returns a collection including existing relations to the same target after creation.
        await RequestAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        var current = await ListAsync(path, cancellationToken).ConfigureAwait(false);
        var candidates = current.Where(value => !previous.Contains(KankaAdapter.PositiveId(value, "id")) &&
            body.All(field => Planner.Equivalent(field.Key, field.Value, value[field.Key]))).ToArray();
        if (candidates.Length != 1) throw new SyncException("Relation creation outcome is ambiguous; refetch and reconcile before retrying.");
        return candidates[0];
    }

    private Uri SafeUri(string path)
    {
        var address = new Uri(campaignUri, path);
        var decodedPath = Uri.UnescapeDataString(address.AbsolutePath);
        if (address.Scheme != Uri.UriSchemeHttps || address.Host != campaignUri.Host || address.Port != 443 ||
            address.UserInfo.Length != 0 || !address.AbsolutePath.StartsWith(campaignUri.AbsolutePath, StringComparison.Ordinal) ||
            decodedPath.Contains('\\') || decodedPath.Split('/').Any(segment => segment is "." or ".."))
            throw new SyncException("Refused API URL outside the configured campaign.");
        return address;
    }

    private async Task<JsonObject> RequestAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var address = SafeUri(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendWithBackoffAsync(method, address, body, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonObject> SendWithBackoffAsync(HttpMethod method, Uri address, JsonObject? body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body != null) request.Content = JsonContent.Create(body);
            nextRequest = DateTimeOffset.UtcNow + interval;
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                nextRequest = DateTimeOffset.UtcNow + RetryDelay(response, attempt);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SyncException($"Kanka request failed (HTTP {(int)response.StatusCode}). No response body logged.");
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (content.Contains(token, StringComparison.Ordinal)) throw new SyncException("API response unexpectedly contained credentials; refused to persist it.");
            var parsed = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                ?? throw new SyncException("Invalid API JSON response.");
            if (ContainsCredential(parsed)) throw new SyncException("Decoded API data unexpectedly contained credentials; refused to persist it.");
            return parsed;
        }
        throw new SyncException("Kanka rate limit persisted; try again later.");
    }

    private async Task SendDeleteWithBackoffAsync(Uri address, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            nextRequest = DateTimeOffset.UtcNow + interval;
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                nextRequest = DateTimeOffset.UtcNow + RetryDelay(response, attempt);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SyncException($"Kanka request failed (HTTP {(int)response.StatusCode}). No response body logged.");
            return;
        }
        throw new SyncException("Kanka rate limit persisted; try again later.");
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30 * Math.Pow(2, attempt));
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(30);
    }

    private bool ContainsCredential(JsonNode? node) => node switch
    {
        JsonObject fields => fields.Any(field => field.Key.Contains(token, StringComparison.Ordinal) || ContainsCredential(field.Value)),
        JsonArray items => items.Any(ContainsCredential),
        JsonValue value when value.TryGetValue<string>(out var text) => text.Contains(token, StringComparison.Ordinal),
        _ => false
    };

    public void Dispose()
    {
        client.Dispose();
        gate.Dispose();
    }
}
