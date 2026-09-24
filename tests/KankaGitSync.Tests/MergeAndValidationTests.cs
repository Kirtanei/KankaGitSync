using System.Text.Json.Nodes;

namespace KankaGitSync.Tests;

public sealed class MergeAndValidationTests
{
    [Fact]
    public void DifferentStructuredFieldsMergeAndSameFieldsConflict()
    {
        var basis = YamlCodec.Read("population: 800000\ngovernment: Council");
        var local = YamlCodec.Read("population: 850000\ngovernment: Council");
        var remote = YamlCodec.Read("population: 800000\ngovernment: Directorate");
        Assert.Equal("{\"government\":\"Directorate\",\"population\":850000}", Canonical.Json(SemanticMerge.Merge(basis, local, remote)));
        remote["population"] = 900000;
        Assert.Throws<SyncException>(() => SemanticMerge.Merge(basis, local, remote));
    }

    [Fact]
    public void SimultaneousPrivacyEditsConflictEvenWhenEqual()
    {
        var basis = YamlCodec.Read("private: false");
        var changed = YamlCodec.Read("private: true");
        Assert.Throws<SyncException>(() => SemanticMerge.Merge(basis, changed, changed));
    }

    [Fact]
    public void UnchangedAndDeletedFieldsMerge()
    {
        var basis = YamlCodec.Read("name: Max\nfield: remove");
        var local = YamlCodec.Read("name: King");
        var remote = basis.Copy();
        Assert.Equal("{\"name\":\"King\"}", Canonical.Json(SemanticMerge.Merge(basis, local, remote)));
        Assert.Equal("{\"name\":\"King\"}", Canonical.Json(SemanticMerge.Merge(basis, local, local)));
    }

    [Fact]
    public void MarkdownMetadataMergesWithoutReplacingProse()
    {
        var basis = "---\nid: max\nname: Max\ntype: prince\n---\n\nHistory.\n";
        var local = basis.Replace("type: prince", "type: king", StringComparison.Ordinal);
        var remote = basis.Replace("name: Max", "name: Maximilian", StringComparison.Ordinal);
        var merged = SemanticMerge.MergeFile("index.md", basis, local, remote);
        Assert.Contains("type: king", merged);
        Assert.Contains("name: Maximilian", merged);
        Assert.Contains("History.", merged);
        Assert.Contains("population: 2", SemanticMerge.MergeFile("properties.yml", "population: 1", "population: 2", "population: 1"));
    }

    [Fact]
    public void DeleteEditAndPrivacySafetyChecksRequireReview()
    {
        var basis = SnapshotWithEntity();
        var local = SnapshotWithEntity();
        var remote = SnapshotWithEntity();
        local.Resources.Clear();
        remote.Resources["max"] = remote.Resources["max"] with { Body = "Changed" };
        Assert.Throws<SyncException>(() => SemanticMerge.CheckSafety(basis, local, remote));
        SemanticMerge.CheckSafety(basis, local, basis);
        local = SnapshotWithEntity();
        ((JsonObject)local.Resources["max"].Metadata["visibility"]!)["private"] = true;
        ((JsonObject)remote.Resources["max"].Metadata["visibility"]!)["private"] = true;
        Assert.Throws<SyncException>(() => SemanticMerge.CheckSafety(basis, local, remote));
    }

    [Fact]
    public void ReferencesCategoriesPrivacyAndPublicationAreValidated()
    {
        var snapshot = SnapshotWithEntity();
        snapshot.Resources["max"] = snapshot.Resources["max"] with { Body = "[[missing]]" };
        Assert.Contains(Validation.Check(snapshot), error => error.Contains("broken", StringComparison.Ordinal));
        var metadata = snapshot.Resources["max"].Metadata;
        metadata["category"] = "unknown";
        metadata["visibility"] = "all";
        metadata["fields"] = new JsonObject { ["permissions"] = "bad" };
        metadata["token"] = "not allowed";
        Assert.True(Validation.Check(snapshot).Count >= 4);
        Assert.Throws<SyncException>(() => Validation.Require(snapshot));
    }

    [Fact]
    public void UnpublishedTargetsAndWrongTagsFailValidation()
    {
        var snapshot = SnapshotWithEntity();
        snapshot.Resources["hidden"] = SynchronizationTests.NewEntity("hidden", publish: false);
        snapshot.Resources["max"] = snapshot.Resources["max"] with { Body = "[[hidden]]" };
        snapshot.Resources["max"].Metadata["tags"] = new JsonArray("hidden");
        Assert.Contains(Validation.Check(snapshot), error => error.Contains("unpublished", StringComparison.Ordinal));
        Assert.Contains(Validation.Check(snapshot), error => error.Contains("tag reference", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpublishedContentCreatesNoOperations()
    {
        var local = SnapshotWithEntity();
        local.Resources["max"].Metadata["publish"] = false;
        Assert.Empty(Planner.Build(local, new Snapshot()));
        Assert.Contains("0 API", Planner.Describe([]));
    }

    [Fact]
    public void PreviouslyMappedMissingResourcesAreNotRecreated()
    {
        var local = SnapshotWithEntity();
        var remote = new Snapshot();
        remote.Mappings["max"] = new Mapping(1, 1, "location", "entity", null);
        Assert.Throws<SyncException>(() => Planner.Build(local, remote));
    }

    [Fact]
    public void WorldFilesRejectDuplicateIdsAndUnownedContent()
    {
        var files = WorldFiles.Write(SnapshotWithEntity());
        files["world/locations/second/index.md"] = files["world/locations/max/index.md"];
        Assert.Throws<SyncException>(() => WorldFiles.Read(files));
        files.Remove("world/locations/second/index.md");
        files["world/locations/max/secrets.md"] = "oops";
        Assert.Throws<SyncException>(() => WorldFiles.Read(files));
        Assert.Throws<SyncException>(() => WorldFiles.Read(new Dictionary<string, string> { ["world/orphan.md"] = "orphan" }));
    }

    [Fact]
    public void ResourceKindVisibilityAndPropertyTypesAreChecked()
    {
        var snapshot = SnapshotWithEntity();
        snapshot.Resources["post"] = new Resource("post", "post", "max", new JsonObject { ["id"] = "post", ["name"] = "", ["visibility"] = "bad" });
        snapshot.Resources["property"] = new Resource("property", "property", "max", new JsonObject { ["id"] = "property", ["name"] = "P", ["type"] = "bad" });
        snapshot.Resources["relation"] = new Resource("relation", "relation", "max", new JsonObject { ["id"] = "relation", ["target"] = "max", ["attitude"] = 999, ["visibility"] = "all" });
        Assert.True(Validation.Check(snapshot).Count >= 4);
    }

    private static Snapshot SnapshotWithEntity()
    {
        var snapshot = new Snapshot();
        snapshot.Resources["max"] = SynchronizationTests.NewEntity("max");
        return snapshot;
    }
}
