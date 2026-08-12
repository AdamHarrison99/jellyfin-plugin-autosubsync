using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleStageKind
{
    Acquire = 0,
    Convert = 1,
    Sync = 2,
    Transform = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StageOutcome
{
    Pending = 0,
    Succeeded = 1,
    Skipped = 2,
    Failed = 3
}

// One step of the pipeline that ran for a target, in the order it ran.
public class SubtitleStage
{
    public SubtitleStageKind Kind { get; set; }

    public StageOutcome Outcome { get; set; }

    public string? Tool { get; set; }

    public string? Message { get; set; }

    public long ElapsedMs { get; set; }

    public DateTime CompletedUtc { get; set; }

    // Provider match confidence, on Acquire only.
    public double? Confidence { get; set; }

    // ! All fields must stay value types or strings.
    public SubtitleStage Clone() => (SubtitleStage)MemberwiseClone();
}
