using System.Text.Json.Nodes;

namespace KankaGitSync.Infrastructure.Configuration;

public sealed record Configuration(long CampaignId, int RequestsPerMinute)
{
    public static Configuration Read(string text)
    {
        var values = YamlCodec.Read(text);
        if (values.Number("schema_version") != 1) throw new SyncException("Unrecognized synchronization schema version.");
        if (values.Any(pair => pair.Key is not ("schema_version" or "campaign_id" or "requests_per_minute")))
            throw new SyncException("Unknown configuration setting. Tokens belong in the environment or world-root .env, not campaign configuration.");
        var campaign = values.Number("campaign_id");
        var rate = values.Number("requests_per_minute", 30);
        if (campaign <= 0 || rate is < 1 or > 90) throw new SyncException("Invalid campaign ID or rate limit.");
        return new Configuration(campaign, (int)rate);
    }

    public string Write() => YamlCodec.Write(new JsonObject
    {
        ["schema_version"] = 1,
        ["campaign_id"] = CampaignId,
        ["requests_per_minute"] = RequestsPerMinute
    });
}
