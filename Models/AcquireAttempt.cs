using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// How one fetched candidate ended.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcquireAttemptOutcome
{
    // The audio check confirmed it and it was placed.
    Kept = 0,

    // The audio check found it out of alignment.
    Misaligned = 1,

    // The audio check reached no verdict.
    Inconclusive = 2,

    // The bytes turned out to carry hearing-impaired annotations.
    HearingImpaired = 3,

    // The fetch, the engine, or the placement did not complete.
    Failed = 4
}

// One candidate this target fetched and judged. A download was spent on every one of these.
public class AcquireAttempt
{
    // ! The provider-scoped id. A re-uploaded file carries a new one and is offered again.
    public string SubtitleId { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public DateTime AttemptedUtc { get; set; }

    public AcquireAttemptOutcome Outcome { get; set; }

    // ! All fields must stay value types or strings.
    public AcquireAttempt Clone() => (AcquireAttempt)MemberwiseClone();
}
