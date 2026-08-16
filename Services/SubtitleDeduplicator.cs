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
            var keeper = ChooseKeeper(group);

            if (group.Count > 1)
            {
                groups++;
            }

            foreach (var candidate in group)
            {
                // ! Identity is not enough; the keeper's own file must never be the candidate.
                if (ReferenceEquals(candidate, keeper)
                    || string.Equals(candidate.Path, keeper.Path, StringComparison.OrdinalIgnoreCase))
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

            if (!config.DryRunMode)
            {
                Canonicalize(keeper, config);
            }
            else if (CanonicalPath(keeper.Path) is { } wanted && !File.Exists(wanted))
            {
                _logger.LogInformation("Dry run: would rename {Path} to {Canonical}", keeper.Path, wanted);
            }
        }

        return new DeduplicationReport(groups, removed, wouldRemove);
    }

    private sealed class Candidate
    {
        public required SyncRecord Record { get; init; }

        public required string Path { get; init; }

        public required DateTime CreatedUtc { get; init; }

        public required long Length { get; init; }

        public required bool IsPluginFile { get; init; }

        public required SubtitleProfile Profile { get; init; }
    }

    // ! Every member must have synced. Nothing here can time-check an unsynced copy.
    private List<List<Candidate>> Group(Guid itemId, IEnumerable<SubtitleTarget> targets)
    {
        var slots = new Dictionary<SubtitleSlot, List<Candidate>>();
        var poisoned = new HashSet<SubtitleSlot>();

        // ! One entry per file. Two targets can name the same sidecar, and a group holding it
        //   twice deletes it as its own duplicate.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            // ! Never processed, so it has no output to compare and cannot poison the slot.
            if (target.UnsupportedReason is not null)
            {
                continue;
            }

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

            if (!seen.Add(candidate.Path))
            {
                continue;
            }

            if (!slots.TryGetValue(slot, out var list))
            {
                list = new List<Candidate>();
                slots[slot] = list;
            }

            list.Add(candidate);
        }

        // ! Singletons come back too. A slot deduplicated on an earlier pass still holds a
        //   survivor named for duplicates that are already gone.
        return slots
            .Where(pair => !poisoned.Contains(pair.Key))
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
            CreatedUtc = info.CreationTimeUtc,
            Length = info.Length,
            IsPluginFile = record.Provenance == SubtitleProvenance.Created,
            Profile = profile
        };
    }

    // A file the user chose to have outlives one the plugin produced, then the one that was there
    // first. An unnumbered name wins ties.
    private static Candidate ChooseKeeper(List<Candidate> group)
        => group
            .OrderBy(c => c.IsPluginFile)
            .ThenBy(c => c.CreatedUtc)
            .ThenByDescending(c => c.Length)
            .ThenBy(c => CanonicalPath(c.Path) is not null)
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

        // ! Retired, ¬Stale: off the cards now, but the removal stays on the stage table.
        record.Retired = true;

        // ! Shared w/ the store, which identifies a removal by this text when it retires one.
        MarkStage(
            record,
            StageOutcome.Succeeded,
            SyncStore.RemovedAsDuplicate + Path.GetFileName(keeperPath) + ".");

        _logger.LogInformation(
            "Removed {Duplicate} ({Content:P0} the same text and {Formatting:P0} the same styling as {Keeper}); it is in the backup vault",
            candidate.Path,
            score.Content,
            score.Formatting,
            keeperPath);

        return true;
    }

    // The survivor keeps a discriminator only its duplicates made necessary.
    private void Canonicalize(Candidate keeper, PluginConfiguration config)
    {
        if (CanonicalPath(keeper.Path) is not { } canonical || File.Exists(canonical))
        {
            return;
        }

        // ! A digit marker is legal. Renaming past it hides the file from discovery and rollback.
        if (SubtitleNaming.IsPluginOutput(keeper.Path, config.MarkerSuffix)
            && !SubtitleNaming.IsPluginOutput(canonical, config.MarkerSuffix))
        {
            return;
        }

        try
        {
            File.Move(keeper.Path, canonical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not rename {Path} to {Canonical}", keeper.Path, canonical);
            return;
        }

        // ! Rollback restores to this, not to OutputPath, once the two differ.
        keeper.Record.RenamedFromPath ??= keeper.Path;
        keeper.Record.OutputPath = canonical;

        _logger.LogInformation(
            "Renamed {Path} to {Canonical}; its duplicates are gone",
            keeper.Path,
            canonical);

        // ! Saved, ¬staged. The row counts duplicates removed, and a rename removed none; the
        //   save is still required or rollback loses where to put the backup.
        Save(keeper.Record);
    }

    // "movie.eng.0.srt" -> "movie.eng.srt". Two digits at most, so a year is never taken for one.
    internal static string? CanonicalPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var cut = stem.LastIndexOf('.');

        if (cut <= 0)
        {
            return null;
        }

        var tail = stem[(cut + 1)..];
        if (tail.Length is 0 or > 2 || !tail.All(char.IsAsciiDigit))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        return Path.Combine(directory, stem[..cut] + Path.GetExtension(path));
    }

    private void MarkStage(SyncRecord record, StageOutcome outcome, string message)
    {
        var stage = record.RecordStage(SubtitleStageKind.Deduplicate, outcome);
        stage.Message = message;
        Save(record);
    }

    // ! A store failure must not abort the sweep that called this.
    private void Save(SyncRecord record)
    {
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
