using System.Linq;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Reads a stored outcome the way the status panel groups it.
public static class SyncOutcome
{
    // ! The one refusal RequireAudioConfirmation raises. Every other refusal stands without it.
    public const string NoVerdictRefusal =
        "Rejected: the audio check reached no verdict on this title — rejected as inconclusive.";

    // ! Not RejectedOffsetMs: a refusal that reached no verdict carries no offset.
    public static bool IsAudioRefusal(SyncRecord record)
        => record.Status == SyncStatus.Failed && (record.RefusedByAudio ?? InferredRefusal(record));

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
