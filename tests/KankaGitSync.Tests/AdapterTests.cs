using System.Text.Json.Nodes;

namespace KankaGitSync.Tests;

public sealed class AdapterTests
{
    [Theory]
    [InlineData("module")]
    [InlineData("type")]
    [InlineData("entity_type")]
    [InlineData("entity_type_code")]
    public async Task CurrentAndLegacyModuleFieldsImportEditableResources(string field)
    {
        var client = new TestCampaign
        {
            CustomizeEntity = entity =>
            {
                var category = entity.Text("type");
                entity.Remove("module");
                entity.Remove("type");
                entity[field] = field == "module" ? new JsonObject { ["code"] = category } : JsonValue.Create(category);
            }
        };
        client.Records["characters/1"]["type"] = "Monarch";
        var snapshot = await new KankaAdapter(client).FetchAsync(new Snapshot(), default);
        Assert.Equal(6, snapshot.Resources.Count);
        Assert.Equal("character", snapshot.Resources["maximilian"].Category);
        Assert.Equal("Monarch", snapshot.Resources["maximilian"].Metadata.Text("type"));
        Assert.Empty(Planner.Build(snapshot, snapshot));
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ExplicitUnknownModuleRemainsUnmanagedDespiteLegacyTypeAndPreviousMapping()
    {
        var client = new TestCampaign();
        var adapter = new KankaAdapter(client);
        var previous = await adapter.FetchAsync(new Snapshot(), default);
        client.CustomizeEntity = entity => entity["module"] = new JsonObject { ["code"] = "custom_module" };
        var snapshot = await adapter.FetchAsync(previous, default);
        Assert.Empty(snapshot.Resources);
        Assert.Equal(3, snapshot.Raw.Count);
        Assert.All(snapshot.Raw.Keys, identifier => Assert.StartsWith("unmanaged-", identifier));
        Assert.Contains("3 entities", Assert.Single(Validation.Warnings(snapshot)));
    }

    [Fact]
    public async Task MissingModuleIdentityFailsInsteadOfSilentlySkippingCampaign()
    {
        var client = new TestCampaign
        {
            CustomizeEntity = entity => { entity.Remove("module"); entity.Remove("type"); }
        };
        var exception = await Assert.ThrowsAsync<SyncException>(() => new KankaAdapter(client).FetchAsync(new Snapshot(), default));
        Assert.Contains("no module code", exception.Message);
    }

    [Fact]
    public async Task FetchRecoversPreviouslyUnmanagedImport()
    {
        var client = new TestCampaign();
        var previous = new Snapshot();
        foreach (var entity in await client.ListAsync("entities", default))
            previous.Raw["unmanaged-" + entity.Number("id")] = entity;
        var snapshot = await new KankaAdapter(client).FetchAsync(previous, default);
        Assert.Equal(6, snapshot.Resources.Count);
        Assert.DoesNotContain(snapshot.Raw.Keys, identifier => identifier.StartsWith("unmanaged-", StringComparison.Ordinal));
        Assert.Empty(Validation.Check(snapshot));
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task RemoteRenameRetainsIdentityAndDuplicateNamesHaveDistinctIds()
    {
        var client = new TestCampaign();
        client.Records["characters/7"] = TestCampaign.Entity(7, 77, "Maximilian");
        var adapter = new KankaAdapter(client);
        var initial = await adapter.FetchAsync(new Snapshot(), default);
        Assert.Contains("maximilian-2", initial.Resources.Keys);
        client.Records["characters/1"]["name"] = "King Maximilian";
        var changed = await adapter.FetchAsync(initial, default);
        Assert.Equal("King Maximilian", changed.Resources["maximilian"].Name);
        Assert.Equal(initial.Mappings["maximilian"], changed.Mappings["maximilian"]);
    }

    [Theory]
    [InlineData("entities/11/attributes/4", "type_id", 99)]
    [InlineData("entities/11/posts/5", "visibility_id", 99)]
    [InlineData("entities/11/relations/6", "target_id", 999)]
    [InlineData("characters/1", "entity_id", 0)]
    public async Task UnknownRemoteSemanticsAbortImport(string path, string field, long value)
    {
        var client = new TestCampaign();
        client.Records[path][field] = value;
        await Assert.ThrowsAsync<SyncException>(() => new KankaAdapter(client).FetchAsync(new Snapshot(), default));
    }

    [Fact]
    public async Task PropertyReferencesAndAllSupportedResourcesRoundTrip()
    {
        var client = new TestCampaign();
        client.Records["entities/11/attributes/4"]["value"] = "[entity:33]";
        var snapshot = await new KankaAdapter(client).FetchAsync(new Snapshot(), default);
        Assert.Equal("[[tarant]]", snapshot.Resources["maximilian-population"].Metadata.Text("value"));
        foreach (var resource in snapshot.Resources.Values)
        {
            var payload = KankaAdapter.Payload(resource, snapshot.Mappings);
            var raw = snapshot.Raw[resource.Id].Copy();
            foreach (var field in payload) raw[field.Key] = field.Value?.DeepClone();
            var roundTrip = KankaAdapter.Import(resource.Id, snapshot.Mappings[resource.Id], raw, snapshot.Mappings);
            Assert.Equal(resource.Signature, roundTrip.Signature);
        }
    }

    [Fact]
    public void PreservedNumericMentionsAreReportedWithoutDiscardingContent()
    {
        var snapshot = new Snapshot();
        snapshot.Resources["max"] = SynchronizationTests.NewEntity("max", "[entity:999|anchor:post-1]");
        Assert.Single(Validation.Warnings(snapshot));
        Assert.Empty(Validation.Check(snapshot));
    }

    [Fact]
    public void UnknownKindsAndCategoriesFailClosed()
    {
        Assert.Throws<SyncException>(() => Categories.Endpoint("unknown"));
        Assert.Throws<SyncException>(() => KankaAdapter.AttachmentEndpoint("unknown"));
        Assert.Throws<SyncException>(() => KankaAdapter.PositiveId(new JsonObject(), "id"));
        Assert.Throws<SyncException>(() => KankaAdapter.Import("max", new Mapping(1, 1, "location", "unknown", null), new JsonObject(), new Dictionary<string, Mapping>()));
    }
}
