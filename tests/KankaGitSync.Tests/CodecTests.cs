using System.Text.Json.Nodes;
using YamlDotNet.Core;

namespace KankaGitSync.Tests;

public sealed class CodecTests
{
    [Theory]
    [InlineData("<p>Hello <strong>world</strong>.</p>")]
    [InlineData("<div class=\"custom\"><span data-thing=\"x\">Keep</span></div>")]
    [InlineData("<table><tr><td colspan=\"2\">Preserve</td></tr></table>")]
    [InlineData("<pre>  preserve\n space  </pre>")]
    [InlineData("<p><em>one</em> <em>two</em></p>")]
    [InlineData("<p>[entity:11|Max] [entity:11|anchor:post-1]</p>")]
    public void HtmlRoundTripsWithoutLoss(string html)
    {
        var mappings = new Dictionary<string, Mapping> { ["max"] = new(11, 1, "character", "entity", null) };
        var imported = ContentCodec.Import(html, mappings);
        var exported = ContentCodec.Export(imported, mappings);
        Assert.Equal(ContentCodec.NormalizeHtml(html), ContentCodec.NormalizeHtml(exported));
        Assert.Equal(imported, ContentCodec.Import(exported, mappings));
    }

    [Fact]
    public void MarkdownAndReferencesRenderDeterministically()
    {
        var mappings = new Dictionary<string, Mapping> { ["max"] = new(11, 1, "character", "entity", null) };
        Assert.Contains("[entity:11|Max]", ContentCodec.Export("Visit [[max|Max]].", mappings));
        Assert.Throws<SyncException>(() => ContentCodec.Export("[[missing]]", mappings));
        Assert.Equal("[entity:999]", ContentCodec.Import("[entity:999]", mappings));
    }

    [Fact]
    public void YamlRetainsScalarTypesAndCanonicalOrder()
    {
        var original = new JsonObject
        {
            ["z"] = "850000",
            ["a"] = true,
            ["null"] = null,
            ["list"] = new JsonArray("false", 42),
            ["mapping"] = new JsonObject { ["name"] = "Max" }
        };
        var yaml = YamlCodec.Write(original);
        Assert.Equal(Canonical.Json(original), Canonical.Json(YamlCodec.Read(yaml)));
        Assert.Equal(yaml, YamlCodec.Write(YamlCodec.Read(yaml)));
        var resource = new Resource("max", "entity", null, new JsonObject { ["id"] = "max" }, "Some prose.");
        var parsed = YamlCodec.ReadMarkdown(YamlCodec.WriteMarkdown(resource));
        Assert.Equal(resource.Body, parsed.Body);
        Assert.Equal("max", parsed.Metadata.Text("id"));
    }

    [Theory]
    [InlineData("x: &a [1]\ny: *a")]
    [InlineData("x: 1\nx: 2")]
    [InlineData("- 1\n- 2")]
    [InlineData("x: 1\n---\nx: 2")]
    public void UnsafeOrAmbiguousYamlFails(string text)
    {
        var exception = Record.Exception(() => YamlCodec.Read(text));
        Assert.True(exception is SyncException or YamlException);
    }

    [Fact]
    public void InvalidFrontMatterFails()
    {
        Assert.Throws<SyncException>(() => YamlCodec.ReadMarkdown("hello"));
        Assert.Throws<SyncException>(() => YamlCodec.ReadMarkdown("---\nid: max\n"));
    }

    [Theory]
    [InlineData("max", true)]
    [InlineData("../max", false)]
    [InlineData("Max", false)]
    [InlineData("max--a", false)]
    public void IdentifiersCannotEscapePaths(string identifier, bool valid) => Assert.Equal(valid, Canonical.ValidId(identifier));

    [Fact]
    public void SlugsHandleDuplicatesAndUnusableNames()
    {
        Assert.Equal("max-3", Canonical.NewId("Max", ["max", "max-2"]));
        Assert.Equal("entry", Canonical.NewId("🎲", []));
        Assert.True(Canonical.ValidId(Canonical.NewId(new string('a', 200), [])));
    }

    [Theory]
    [InlineData("schema_version: 2\ncampaign_id: 1")]
    [InlineData("schema_version: 1\ncampaign_id: 0")]
    [InlineData("schema_version: 1\ncampaign_id: 1\nrequests_per_minute: 91")]
    [InlineData("schema_version: 1\ncampaign_id: 1\ntoken: secret")]
    public void InvalidConfigurationRejected(string text) => Assert.Throws<SyncException>(() => Configuration.Read(text));

    [Fact]
    public void ConfigurationRoundTrips() => Assert.Equal(new Configuration(123, 30), Configuration.Read(new Configuration(123, 30).Write()));
}
