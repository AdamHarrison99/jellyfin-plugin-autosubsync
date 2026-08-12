using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

public record RollbackReport(int Restored, int Deleted, int Skipped, int Failed);

// Undoes every change the plugin made to the library.
public class RollbackService
{
    // ! Two passes over the same records would report phantom failures.
    private readonly Lock _gate = new();

    private readonly ISyncStore _store;
    private readonly BackupVault _vault;
    private readonly ILogger<RollbackService> _logger;

    public RollbackService(ISyncStore store, BackupVault vault, ILogger<RollbackService> logger)
    {
        _store = store;
        _vault = vault;
        _logger = logger;
    }

    public RollbackReport RollbackAll(PluginConfiguration config)
    {
        lock (_gate)
        {
            return RunAll(config);
        }
    }

    private RollbackReport RunAll(PluginConfiguration config)
    {
        var restored = 0;
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var undone = new List<Guid>();

        foreach (var record in _store.GetAll())
        {
            var outcome = Undo(record, config);

            switch (outcome)
            {
                case RollbackOutcome.Restored: restored++; break;
                case RollbackOutcome.Deleted: deleted++; break;
                case RollbackOutcome.Failed: failed++; break;
                default: skipped++; break;
            }

            // ! A failed record keeps its row; it is the only pointer to the backup.
            if (outcome != RollbackOutcome.Failed)
            {
                undone.Add(record.Id);
            }
        }

        _store.RemoveMany(undone);
        _store.Flush();

        _logger.LogInformation(
            "Rollback complete: {Restored} restored, {Deleted} deleted, {Skipped} skipped, {Failed} failed",
            restored,
            deleted,
            skipped,
            failed);

        return new RollbackReport(restored, deleted, skipped, failed);
    }

    private enum RollbackOutcome
    {
        Skipped,
        Restored,
        Deleted,
        Failed
    }

    // ! Provenance decides the verb. Restoring a Created file would resurrect nothing.
    private RollbackOutcome Undo(SyncRecord record, PluginConfiguration config)
    {
        var outcome = record.Provenance == SubtitleProvenance.Retimed
            ? Restore(record)
            : Delete(record, config);

        // ! Keep the backup when the restore did not happen.
        if (outcome != RollbackOutcome.Failed)
        {
            _vault.Discard(record.Id);
        }

        return outcome;
    }

    private RollbackOutcome Restore(SyncRecord record)
    {
        var original = record.OutputPath ?? record.SourceSubtitlePath;

        if (record.BackupPath is null || original is null)
        {
            _logger.LogInformation("Nothing to restore for {Item}: no backup was taken", record.ItemName);
            return RollbackOutcome.Skipped;
        }

        return _vault.Restore(record.BackupPath, original)
            ? RollbackOutcome.Restored
            : RollbackOutcome.Failed;
    }

    private RollbackOutcome Delete(SyncRecord record, PluginConfiguration config)
    {
        if (record.OutputPath is null || !File.Exists(record.OutputPath))
        {
            return RollbackOutcome.Skipped;
        }

        // ! Never delete on the record alone; the name must also be ours.
        if (!SubtitleNaming.IsPluginOutput(record.OutputPath, config.MarkerSuffix))
        {
            _logger.LogWarning(
                "Refusing to delete {Path}: it does not carry the marker {Marker}",
                record.OutputPath,
                config.MarkerSuffix);
            return RollbackOutcome.Failed;
        }

        try
        {
            File.Delete(record.OutputPath);
            return RollbackOutcome.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to delete {Path}", record.OutputPath);
            return RollbackOutcome.Failed;
        }
    }
}
