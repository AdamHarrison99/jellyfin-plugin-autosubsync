using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Persisted outcome for one SubtitleTarget. Identity is (ItemId, TargetKey), not Id.
public class SyncRecord
{
    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string TargetKey { get; set; } = string.Empty;

    public SubtitleOrigin Origin { get; set; }

    public string VideoPath { get; set; } = string.Empty;

    // Null for embedded targets.
    public string? SourceSubtitlePath { get; set; }

    public string? OutputPath { get; set; }

    // ! Where rollback must put the backup. Set only when deduplication renamed the survivor.
    public string? RenamedFromPath { get; set; }

    public string? BackupPath { get; set; }

    // ! Rollback branches on this. Retimed restores; Created deletes.
    public SubtitleProvenance Provenance { get; set; }

    public string? ToolUsed { get; set; }

    public string? ReferenceUsed { get; set; }

    public long ElapsedMs { get; set; }

    public int? ReturnCode { get; set; }

    public SyncStatus Status { get; set; }

    // The pipeline steps that ran, oldest first. A v1 record gains a synthesized Sync stage.
    public List<SubtitleStage> Stages { get; set; } = new();

    public string? Message { get; set; }

    public int AttemptCount { get; set; }

    // ! Bump when the offset measurement changes. A record below it has its rejection re-opened.
    public const int CurrentMeasurementVersion = 1;

    public int MeasurementVersion { get; set; }

    // ! Set only when the audio check refused a result. Widening the tolerance retries the record.
    public long? RejectedOffsetMs { get; set; }

    // ! Set only when the audio check left a subtitle alone. Tightening the tolerance retries it.
    public long? AlignedAtMs { get; set; }

    // How far the engine moved the subtitle, on a run that was kept. Null if the timings would not parse.
    public long? AppliedOffsetMs { get; set; }

    // ! Set only when a result moved too little to keep. A lower minimum retries the record.
    public long? SkippedMovementMs { get; set; }

    // ! The output settings this outcome was produced under. Null means a record from before
    //   stamping; it is read as current so an upgrade re-syncs nothing.
    public string? SettingsStamp { get; set; }

    // Both fingerprints must still match for a target to be skipped.
    public long SourceLength { get; set; }

    public DateTime SourceLastWriteUtc { get; set; }

    public string? SourceSha256 { get; set; }

    public long VideoLength { get; set; }

    public string? VideoPartialHash { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    // ! Every field except Stages must stay a value type or string; Stages is copied by hand.
    public SyncRecord Clone()
    {
        var clone = (SyncRecord)MemberwiseClone();
        clone.Stages = Stages.Select(s => s.Clone()).ToList();
        return clone;
    }

    public SubtitleStage RecordStage(SubtitleStageKind kind, StageOutcome outcome, string? tool = null)
    {
        var stage = Stages.FirstOrDefault(s => s.Kind == kind);

        if (stage is null)
        {
            stage = new SubtitleStage { Kind = kind };
            Stages.Add(stage);
            Stages.Sort((a, b) => a.Kind.CompareTo(b.Kind));
        }

        stage.Outcome = outcome;
        stage.Tool = tool ?? stage.Tool;
        stage.CompletedUtc = DateTime.UtcNow;
        return stage;
    }
}
