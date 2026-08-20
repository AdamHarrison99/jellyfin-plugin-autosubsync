using System.Text.Json;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;

// Exercises the real SyncStore.Migrate against a v1 records.json fixture.
// See agentic/ARCHITECTURE.md for what the staged model replaced.
var failures = 0;
var fixturePath = Path.Combine(AppContext.BaseDirectory, "v1-records.json");

if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine($"storecheck: fixture not found at {fixturePath}");
    return 1;
}

var records = JsonSerializer.Deserialize<List<SyncRecord>>(File.ReadAllText(fixturePath))
              ?? new List<SyncRecord>();

Check("the fixture loads every v1 record", () =>
    Expect(records.Count == 4, $"loaded {records.Count} records, want 4"));

// v1 wrote Provenance as a bare integer; the string converter must still read those.
Check("numeric provenance from a v1 file still loads", () =>
{
    Expect(records[0].Provenance == SubtitleProvenance.Retimed, $"got {records[0].Provenance}");
    Expect(records[1].Provenance == SubtitleProvenance.Created, $"got {records[1].Provenance}");
});

Check("a v1 record carries no stages before migration", () =>
    Expect(records.TrueForAll(r => r.Stages.Count == 0), "a record already had stages"));

var migrated = SyncStore.Migrate(records);

Check("migration reports only the records it changed", () =>
    Expect(migrated == 3, $"migrated {migrated}, want 3 (the Pending record is left alone)"));

Check("a synced record gains a succeeded Sync stage", () =>
{
    var stage = Only(records[0]);
    Expect(stage.Kind == SubtitleStageKind.Sync, $"kind was {stage.Kind}");
    Expect(stage.Outcome == StageOutcome.Succeeded, $"outcome was {stage.Outcome}");
    Expect(stage.Tool == "ffsubsync", $"tool was {stage.Tool ?? "null"}");
    Expect(stage.ElapsedMs == 41230, $"elapsed was {stage.ElapsedMs}");
    Expect(stage.CompletedUtc == records[0].UpdatedUtc, "the stage did not inherit UpdatedUtc");
});

Check("an unsupported record becomes a skipped stage, not a failed one", () =>
{
    var stage = Only(records[1]);
    Expect(stage.Outcome == StageOutcome.Skipped, $"outcome was {stage.Outcome}");
    Expect(stage.Tool is null, "a track that never ran an engine recorded one");
});

Check("a failed record keeps its message on the stage", () =>
{
    var stage = Only(records[2]);
    Expect(stage.Outcome == StageOutcome.Failed, $"outcome was {stage.Outcome}");
    Expect(stage.Message == records[2].Message, "the stage message does not match the record");
});

Check("a pending record gains no stage", () =>
    Expect(records[3].Stages.Count == 0, "a record that never completed was given a stage"));

Check("migration is idempotent", () =>
{
    var again = SyncStore.Migrate(records);
    Expect(again == 0, $"a second pass migrated {again} records");
    Expect(records[0].Stages.Count == 1, "a second pass added a duplicate stage");
});

Check("cloning a record deep-copies its stages", () =>
{
    var clone = records[0].Clone();
    clone.Stages[0].Message = "mutated";

    Expect(records[0].Stages[0].Message != "mutated", "the clone shares its stage list with the original");
});

Check("RecordStage updates in place and keeps pipeline order", () =>
{
    var record = new SyncRecord();
    record.RecordStage(SubtitleStageKind.Sync, StageOutcome.Succeeded, "ffsubsync");
    record.RecordStage(SubtitleStageKind.Convert, StageOutcome.Succeeded, "seconv");
    record.RecordStage(SubtitleStageKind.Sync, StageOutcome.Failed, "alass");

    Expect(record.Stages.Count == 2, $"had {record.Stages.Count} stages, want 2");
    Expect(record.Stages[0].Kind == SubtitleStageKind.Convert, "stages are not in pipeline order");

    var sync = record.Stages[1];
    Expect(sync.Outcome == StageOutcome.Failed, $"the Sync stage was not updated: {sync.Outcome}");
    Expect(sync.Tool == "alass", $"tool was {sync.Tool ?? "null"}");
});

// --- how the status panel groups a stored outcome ---
//
// These shipped wrong once: the failed/refused split keyed on RejectedOffsetMs, which the audio
// check leaves null whenever it reaches no verdict, so 190 refusals were counted as tool
// failures. The flag is the fix; the inference below it is only for rows written before it.

Check("a refusal that reached no verdict is not a tool failure", () =>
{
    var record = new SyncRecord { Status = SyncStatus.Failed, RefusedByAudio = true };
    Expect(SyncOutcome.IsAudioRefusal(record), "a flagged refusal counted as a failure");
});

Check("a tool failure is not an audio refusal", () =>
{
    var record = new SyncRecord { Status = SyncStatus.Failed, RefusedByAudio = false };
    record.RecordStage(SubtitleStageKind.Convert, StageOutcome.Failed);

    Expect(!SyncOutcome.IsAudioRefusal(record), "an OCR failure counted as an audio refusal");
});

// ! The flag wins over the stages. A record refused at Verify on one run and failed at Convert
//   on another carries both, and only the flag describes the run that produced its status.
Check("a stale Verify stage cannot outvote the flag", () =>
{
    var record = new SyncRecord { Status = SyncStatus.Failed, RefusedByAudio = false };
    record.RecordStage(SubtitleStageKind.Verify, StageOutcome.Failed);
    record.RecordStage(SubtitleStageKind.Convert, StageOutcome.Failed);

    Expect(!SyncOutcome.IsAudioRefusal(record), "a stage from an earlier run decided the answer");
});

Check("a legacy row falls back to its offset and stages", () =>
{
    var withOffset = new SyncRecord { Status = SyncStatus.Failed, RejectedOffsetMs = 900 };
    Expect(SyncOutcome.IsAudioRefusal(withOffset), "a legacy bounded refusal was missed");

    var withStage = new SyncRecord { Status = SyncStatus.Failed };
    withStage.RecordStage(SubtitleStageKind.Verify, StageOutcome.Failed);
    Expect(SyncOutcome.IsAudioRefusal(withStage), "a legacy no-verdict refusal was missed");

    var neither = new SyncRecord { Status = SyncStatus.Failed };
    neither.RecordStage(SubtitleStageKind.Sync, StageOutcome.Failed);
    Expect(!SyncOutcome.IsAudioRefusal(neither), "a legacy tool failure counted as a refusal");
});

Check("only a failed record can be an audio refusal", () =>
{
    var record = new SyncRecord { Status = SyncStatus.Skipped, RefusedByAudio = true };
    Expect(!SyncOutcome.IsAudioRefusal(record), "a skipped record counted as a refusal");
});

// ! A vanished source is not "already in sync". ReopenFailed and the gone-from-disk branch both
//   clear the measurements, and their absence is the only thing that separates the two.
Check("a skip is only already-in-sync when something measured it", () =>
{
    var aligned = new SyncRecord { Status = SyncStatus.Skipped, AlignedAtMs = 120 };
    Expect(SyncOutcome.NothingToDo(aligned), "an aligned skip was not counted");

    var barelyMoved = new SyncRecord { Status = SyncStatus.Skipped, SkippedMovementMs = 40 };
    Expect(SyncOutcome.NothingToDo(barelyMoved), "a below-minimum skip was not counted");

    var gone = new SyncRecord { Status = SyncStatus.Skipped };
    Expect(!SyncOutcome.NothingToDo(gone), "a vanished source counted as already in sync");
});

// ! StampStage and Migrate both stamp through StageFor. Its default arm is Failed, so a status
//   missing from the Skipped arm lands a track that never ran under FAILED on its stage row.
Check("every status a stage can carry maps to the outcome its row means", () =>
{
    Expect(SyncOutcome.StageFor(SyncStatus.Synced) == StageOutcome.Succeeded, "a synced record did not succeed");
    Expect(SyncOutcome.StageFor(SyncStatus.Skipped) == StageOutcome.Skipped, "a skip was not skipped");
    Expect(SyncOutcome.StageFor(SyncStatus.Unsupported) == StageOutcome.Skipped, "an unsupported track failed");
    Expect(SyncOutcome.StageFor(SyncStatus.SetAside) == StageOutcome.Skipped, "a set-aside track failed");
    Expect(SyncOutcome.StageFor(SyncStatus.Failed) == StageOutcome.Failed, "a failure did not fail");
});

// A borrowed fixture record would leave the cases after this one reading a record this one wiped.
static SyncRecord Refused() =>
    new()
    {
        Status = SyncStatus.Failed,
        RefusedByAudio = true,
        RejectedOffsetMs = 1400,
        Message = "Rejected: the audio check found the subtitle out of alignment."
    };

Check("reopening a failure clears what described the old run", () =>
{
    var record = Refused();
    record.RecordStage(SubtitleStageKind.Verify, StageOutcome.Failed);

    Expect(SyncStore.ReopenFailedIn(new List<SyncRecord> { record }) == 1, "the record was not reopened");
    Expect(record.Status == SyncStatus.Pending, "the record was not put back in the queue");
    Expect(record.RefusedByAudio is null, "the refusal flag survived a reopen");
    Expect(record.RejectedOffsetMs is null, "the rejected offset survived a reopen");
    Expect(record.Stages.Count == 0, "the stages survived a reopen");
});

// The second reopen path. It clears the same fields for the same reason.
Check("remeasuring a refusal clears what described the old run", () =>
{
    var record = Refused();
    record.RecordStage(SubtitleStageKind.Verify, StageOutcome.Failed);

    var report = SyncStore.Remeasure(new List<SyncRecord> { record });
    Expect(report.Reopened == 1, $"remeasure reopened {report.Reopened}, want 1");
    Expect(record.MeasurementVersion == SyncRecord.CurrentMeasurementVersion, "the record was not stamped");
    Expect(record.RefusedByAudio is null, "the refusal flag survived a remeasure");
    Expect(record.Stages.Count == 0, "the stages survived a remeasure");
});

if (failures > 0)
{
    Console.Error.WriteLine($"\nstorecheck: {failures} failure(s)");
    return 1;
}

Console.WriteLine("storecheck: all cases pass");
return 0;

void Check(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL  {name}: {ex.Message}");
        failures++;
    }
}

void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

SubtitleStage Only(SyncRecord record)
{
    if (record.Stages.Count != 1)
    {
        throw new InvalidOperationException($"expected exactly one stage, found {record.Stages.Count}");
    }

    return record.Stages[0];
}
