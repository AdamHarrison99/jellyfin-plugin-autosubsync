using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Trailing object of an "assy-cli batch --json" NDJSON stream.
public class AssyBatchSummaryEnvelope
{
    [JsonPropertyName("summary")]
    public AssyBatchSummary? Summary { get; set; }
}

public class AssyBatchSummary
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("ok")]
    public int Ok { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("aborted")]
    public bool Aborted { get; set; }
}
