using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Mirrors assy-cli's JSON output. Do not rename without re-checking upstream main/cli.py.
public class AssyResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    // Batch mode only.
    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("returncode")]
    public int? ReturnCode { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }
}
