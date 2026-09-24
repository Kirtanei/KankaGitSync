using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace KankaGitSync.Tests;

public sealed class HttpTests
{
    private const string TestToken = "test-only-bearer";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task PaginationUsesAuthenticationAndStaysInCampaign()
    {
        var count = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(TestToken, request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.Equal("api.kanka.io", request.RequestUri?.Host);
            return Task.FromResult(++count == 1
                ? Response("""{"data":[{"id":1}],"links":{"next":"https://api.kanka.io/1.0/campaigns/123/entities?page=2"}}""")
                : Response("""{"data":[{"id":2}],"links":{"next":null}}"""));
        });
        using var client = Client(handler);
        var records = await client.ListAsync("entities", default);
        Assert.Equal(2, records.Count);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("https://api.kanka.io/1.0/campaigns/999/entities")]
    [InlineData("http://api.kanka.io/1.0/campaigns/123/entities")]
    [InlineData("https://user:pass@api.kanka.io/1.0/campaigns/123/entities")]
    [InlineData("../../profile")]
    [InlineData("https://api.kanka.io/1.0/campaigns/123/entities/%2f..%2f..%2f999/entities")]
    public async Task UntrustedPaginationCannotLeakCredentials(string next)
    {
        var requests = 0;
        using var handler = new Handler(_ =>
        {
            requests++;
            return Task.FromResult(Response(new JsonObject { ["data"] = new JsonArray(), ["links"] = new JsonObject { ["next"] = next } }.ToJsonString()));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<SyncException>(() => client.ListAsync("entities", default));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CyclicPaginationIsRejected()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("""{"data":[],"links":{"next":"entities"}}""")));
        using var client = Client(handler);
        await Assert.ThrowsAsync<SyncException>(() => client.ListAsync("entities", default));
    }

    [Fact]
    public async Task RateLimitHonorsServerDelayAndExhaustsRetries()
    {
        var delays = new List<TimeSpan>();
        var requests = 0;
        using var handler = new Handler(_ =>
        {
            requests++;
            var response = Response("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        });
        using var client = new KankaClient(123, TestToken, handler: handler, delayAsync: (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<SyncException>(() => client.GetAsync("characters/1", default));
        Assert.Equal(4, requests);
        Assert.All(delays, delay => Assert.True(delay.TotalSeconds > 44));
    }

    [Fact]
    public async Task PatchUsesOnlySuppliedFieldsAndErrorsDoNotEchoBodies()
    {
        var requests = 0;
        using var handler = new Handler(async request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("{\"name\":\"Max\"}", await request.Content!.ReadAsStringAsync());
            return Response("server secret " + TestToken, HttpStatusCode.UnprocessableEntity);
        });
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<SyncException>(() => client.WriteAsync("characters/1", new JsonObject { ["name"] = "Max" }, false, default));
        Assert.Contains("422", error.Message);
        Assert.DoesNotContain(TestToken, error.Message);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"name\":\"test-only-bearer\"}}")]
    [InlineData("{\"data\":{\"name\":\"\\u0074est-only-bearer\"}}")]
    public async Task InvalidOrCredentialBearingResponsesAreNotPersisted(string body)
    {
        using var handler = new Handler(_ => Task.FromResult(Response(body)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<SyncException>(() => client.GetAsync("characters/1", default));
    }

    [Fact]
    public async Task CreateAndReadReturnDataEnvelope()
    {
        using var handler = new Handler(request =>
        {
            Assert.Contains(request.Method, new[] { HttpMethod.Post, HttpMethod.Get });
            return Task.FromResult(Response("{\"data\":{\"id\":1}}"));
        });
        using var client = Client(handler);
        Assert.Equal(1, (await client.WriteAsync("characters", new JsonObject { ["name"] = "Max" }, true, default)).Number("id"));
        Assert.Equal(1, (await client.GetAsync("characters/1", default)).Number("id"));
    }

    [Theory]
    [InlineData(0, "token", 30)]
    [InlineData(1, "", 30)]
    [InlineData(1, "token\n", 30)]
    [InlineData(1, "token", 91)]
    public void InvalidClientConfigurationRejected(long campaign, string token, int rate) =>
        Assert.Throws<SyncException>(() => new KankaClient(campaign, token, rate));

    private static KankaClient Client(HttpMessageHandler handler) => new(123, TestToken, handler: handler, delayAsync: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task RelationCreateIdentifiesNewRecordFromCollectionResponse()
    {
        var requests = 0;
        using var handler = new Handler(request =>
        {
            requests++;
            if (requests == 2)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                return Task.FromResult(Response("{\"data\":[{\"id\":1},{\"id\":2}]}"));
            }
            return Task.FromResult(Response(requests == 1
                ? "{\"data\":[{\"id\":1,\"relation\":\"old\"}]}"
                : "{\"data\":[{\"id\":1,\"relation\":\"old\"},{\"id\":2,\"relation\":\"new\"}]}"));
        });
        using var client = Client(handler);
        var result = await client.WriteAsync("entities/11/relations", new JsonObject { ["relation"] = "new" }, true, default);
        Assert.Equal(2, result.Number("id"));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task AmbiguousRelationCreationDoesNotGuessAnIdentity()
    {
        var requests = 0;
        using var handler = new Handler(_ => Task.FromResult(Response(++requests < 3
            ? "{\"data\":[]}"
            : "{\"data\":[{\"id\":2,\"relation\":\"new\"},{\"id\":3,\"relation\":\"new\"}]}")));
        using var client = Client(handler);
        await Assert.ThrowsAsync<SyncException>(() => client.WriteAsync("entities/11/relations", new JsonObject { ["relation"] = "new" }, true, default));
    }
}
