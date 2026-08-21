using System;
using System.Linq;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Reads a stored outcome the way the status panel groups it.
public static class SyncOutcome
{
    // ! The one refusal RequireAudioConfirmation raises on a subtitle the item already carries.
    //   Every other refusal stands without it.
    public const string NoVerdictRefusal =
        "Rejected: the audio check reached no verdict on the subtitle this item already has.";

    // ! The same refusal reached after buying the whole list. Grouped with the single-candidate
    //   form, or the card empties the moment an item is offered more than one subtitle.
    public const string NoVerdictExhaustedPrefix =
        "Rejected: the audio check reached no verdict on any subtitle offered for this language";

    // ! Rows written before the current wording carry an older one. Reading the current string
    //   alone moves every one of them onto the card that did not cause them.
    private static readonly string[] LegacyNoVerdict =
    {
        "Rejected: the audio check reached no verdict on this title.",
        "Rejected: the audio check reached no verdict on this title — rejected as inconclusive."
    };

    // The language is the one the offers were searched for; a target without one omits it.
    public static string NoVerdictExhausted(string? language)
        => string.IsNullOrWhiteSpace(language)
            ? NoVerdictExhaustedPrefix + "."
            : NoVerdictExhaustedPrefix + ": " + language.Trim() + ".";

    // ! Not RejectedOffsetMs: a refusal that reached no verdict carries no offset.
    public static bool IsAudioRefusal(SyncRecord record)
        => record.Status == SyncStatus.Failed && (record.RefusedByAudio ?? InferredRefusal(record));

    // ! A setting caused this one, so it is counted apart from the refusals that stand alone.
    //   Prefix, not equality: the exhausted form names the language it searched.
    public static bool IsInconclusiveRefusal(SyncRecord record)
        => IsAudioRefusal(record)
           && record.Message is { } message
           && (message == NoVerdictRefusal
               || message.StartsWith(NoVerdictExhaustedPrefix, StringComparison.Ordinal)
               || Array.IndexOf(LegacyNoVerdict, message) >= 0);

    // ! What the audio check refused, wherever the row ended up. A set-aside row still carries
    //   the Verify refusals of candidates its allowance stopped it acting on.
    public static bool CarriesAudioRefusal(SyncRecord record)
        => IsAudioRefusal(record) || record.Status == SyncStatus.SetAside;

    // ! Only a measured skip is "already in sync". A source that vanished measured nothing.
    public static bool NothingToDo(SyncRecord record)
        => record.Status == SyncStatus.Skipped
           && (record.AlignedAtMs is not null || record.SkippedMovementMs is not null);

    // ! The one map from a stored status to a stage outcome. Pending and DryRun never reach it.
    public static StageOutcome StageFor(SyncStatus status) => status switch
    {
        SyncStatus.Synced => StageOutcome.Succeeded,
        SyncStatus.Skipped or SyncStatus.Unsupported or SyncStatus.SetAside => StageOutcome.Skipped,
        _ => StageOutcome.Failed
    };

    // The cards describe the library as it is now.
    public static bool OnCards(SyncRecord record) => !record.Stale && !record.Retired;

    // ! The stage table describes work that ran, and a row the plugin closed itself still ran.
    public static bool OnStageTable(SyncRecord record) => !record.Stale;

    // ! Rows written before the flag only. A stage can outlive the run that wrote it.
    private static bool InferredRefusal(SyncRecord record)
        => record.RejectedOffsetMs is not null
           || record.Stages.Any(s =>
               s.Kind == SubtitleStageKind.Verify && s.Outcome == StageOutcome.Failed);
}
