using System.Linq;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Reads a stored outcome the way the status panel groups it.
public static class SyncOutcome
{
    // ! ¬RejectedOffsetMs: a refusal that reached no verdict carries no offset.
    public static bool IsAudioRefusal(SyncRecord record)
        => record.Status == SyncStatus.Failed && (record.RefusedByAudio ?? InferredRefusal(record));

    // ! Only a measured skip is "already in sync". A source that vanished measured nothing.
    public static bool NothingToDo(SyncRecord record)
        => record.Status == SyncStatus.Skipped
           && (record.AlignedAtMs is not null || record.SkippedMovementMs is not null);

    // ! Rows written before the flag only. A stage can outlive the run that wrote it.
    private static bool InferredRefusal(SyncRecord record)
        => record.RejectedOffsetMs is not null
           || record.Stages.Any(s =>
               s.Kind == SubtitleStageKind.Verify && s.Outcome == StageOutcome.Failed);
}
