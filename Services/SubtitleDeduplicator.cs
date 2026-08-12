using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

public record DeduplicationReport(int Groups, int Removed, int WouldRemove);

// Collapses same-slot subtitles that turned out to hold the same text.
public class SubtitleDeduplicator
{
    private const double Threshold = 0.85;

    private readonly ISyncStore _store;
    private readonly BackupVault _vault;
    private readonly ILogger<SubtitleDeduplicator> _logger;

    public SubtitleDeduplicator(ISyncStore store, BackupVault vault, ILogger<SubtitleDeduplicator> logger)
    {
        _store = store;
        _vault = vault;
        _logger = logger;
    }

    public DeduplicationReport ProcessItem(
        Guid itemId,
        IEnumerable<SubtitleTarget> targets,
        PluginConfiguration config)
    {
        if (!config.DeduplicateSubtitles)
        {
            return new DeduplicationReport(0, 0, 0);
        }

        var groups = 0;
        var removed = 0;
        var wouldRemove = 0;

        foreach (var group in Group(itemId, targets))
        {
            groups++;
            var keeper = ChooseKeeper(group);

            foreach (var candidate in group)
            {
                if (ReferenceEquals(candidate, keeper))
                {
                    continue;
                }

                var score = SubtitleSimilarity.Compare(keeper.Profile, candidate.Profile);
                if (!score.Matches(Threshold))
                {
                    continue;
                }

                if (config.DryRunMode)
                {
                    wouldRemove++;
                    MarkStage(
                        candidate.Record,
                        StageOutcome.Skipped,
                        $"Would be removed as a duplicate of {Path.GetFileName(keeper.Path)}.");

                    _logger.LogInformation(
                        "Dry run: would remove {Duplicate} ({Content:P0} the same text and {Formatting:P0} the same styling as {Keeper})",
                        candidate.Path,
                        score.Content,
                        score.Formatting,
                        keeper.Path);
                    continue;
                }

                if (Remove(candidate, keeper.Path, score))
                {
                    removed++;
                }
            }
        }

        return new DeduplicationReport(groups, removed, wouldRemove);
    }

    private sealed class Candidate
    {
        public required SyncRecord Record { get; init; }

        public required string Path { get; init; }

        public required long Length { get; init; }

        public required bool IsPluginFile { get; init; }

        public required SubtitleProfile Profile { get; init; }
    }

    // ! Every member must have synced. Nothing here can time-check an unsynced copy.
    private List<List<Candidate>> Group(Guid itemId, IEnumerable<SubtitleTarget> targets)
    {
        var slots = new Dictionary<SubtitleSlot, List<Candidate>>();
        var poisoned = new HashSet<SubtitleSlot>();

        foreach (var target in targets)
        {
            var slot = new SubtitleSlot(
                LanguageCodes.Normalize(target.Language) ?? string.Empty,
                target.IsForced,
                target.IsHearingImpaired);

            var record = _store.GetByTargetKey(itemId, target.Key);
            var candidate = ToCandidate(record);

            if (candidate is null)
            {
                poisoned.Add(slot);
                continue;
            }

            if (!slots.TryGetValue(slot, out var list))
            {
                list = new List<Candidate>();
                slots[slot] = list;
            }

            list.Add(candidate);
        }

        return slots
            .Where(pair => pair.Value.Count > 1 && !poisoned.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToList();
    }

    private static Candidate? ToCandidate(SyncRecord? record)
    {
        if (record?.OutputPath is not { } path)
        {
            return null;
        }

        if (record.Status is not (SyncStatus.Synced or SyncStatus.Skipped))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists || SubtitleProfile.Read(path) is not { } profile)
        {
            return null;
        }

        return new Candidate
        {
            Record = record,
            Path = path,
            Length = info.Length,
            IsPluginFile = record.Provenance == SubtitleProvenance.Created,
            Profile = profile
        };
    }

    // A file the user chose to have outlives one the plugin produced.
    private static Candidate ChooseKeeper(List<Candidate> group)
        => group
            .OrderBy(c => c.IsPluginFile)
            .ThenByDescending(c => c.Length)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .First();

    // ! The vault copy gates the removal. No backup, no delete.
    private bool Remove(Candidate candidate, string keeperPath, SimilarityScore score)
    {
        var record = candidate.Record;

        // ! Labelled. An unlabelled copy lands on the pre-overwrite original and is dropped.
        var backup = _vault.Store(record.Id, candidate.Path, "duplicate");
        if (backup is null)
        {
            _logger.LogWarning("Backup failed for {Path}; leaving the duplicate in place", candidate.Path);
            MarkStage(record, StageOutcome.Failed, "Backup failed; the duplicate was left in place.");
            return false;
        }

        try
        {
            File.Delete(candidate.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to remove the duplicate {Path}", candidate.Path);
            MarkStage(record, StageOutcome.Failed, "The duplicate could not be removed.");
            return false;
        }

        // ! Only a user file becomes Superseded. Promoting a Created record makes rollback
        //   restore plugin output into the library.
        if (record.Provenance == SubtitleProvenance.Retimed)
        {
            record.Provenance = SubtitleProvenance.Superseded;
        }

        record.BackupPath ??= backup;
        MarkStage(record, StageOutcome.Succeeded, $"Removed as a duplicate of {Path.GetFileName(keeperPath)}.");

        _logger.LogInformation(
            "Removed {Duplicate} ({Content:P0} the same text and {Formatting:P0} the same styling as {Keeper}); it is in the backup vault",
            candidate.Path,
            score.Content,
            score.Formatting,
            keeperPath);

        return true;
    }

    // ! A store failure must not abort the sweep that called this.
    private void MarkStage(SyncRecord record, StageOutcome outcome, string message)
    {
        var stage = record.RecordStage(SubtitleStageKind.Deduplicate, outcome);
        stage.Message = message;
        record.UpdatedUtc = DateTime.UtcNow;

        try
        {
            _store.Upsert(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to record deduplication for {Path}", record.OutputPath);
        }
    }
}
