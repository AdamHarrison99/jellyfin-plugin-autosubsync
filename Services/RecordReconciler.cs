using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Holds the record store to what discovery still offers. The status panel counts what survives.
public class RecordReconciler
{
    private readonly ISyncStore _store;
    private readonly BackupVault _vault;
    private readonly ILogger<RecordReconciler> _logger;

    public RecordReconciler(ISyncStore store, BackupVault vault, ILogger<RecordReconciler> logger)
    {
        _store = store;
        _vault = vault;
        _logger = logger;
    }

    // Settles one item against the targets discovery just offered for it.
    public void Reconcile(Guid itemId, IReadOnlyList<SubtitleTarget> targets)
    {
        var keys = new HashSet<string>(targets.Select(t => t.Key), StringComparer.Ordinal);

        // ! Deduplication renames a survivor and leaves its key behind. The path still matches.
        var paths = new HashSet<string>(
            targets.Where(t => t.SubtitlePath is not null).Select(t => t.SubtitlePath!),
            StringComparer.OrdinalIgnoreCase);

        var drop = new List<Guid>();

        foreach (var record in _store.GetByItemId(itemId))
        {
            var offered = keys.Contains(record.TargetKey)
                || (record.OutputPath is not null && paths.Contains(record.OutputPath));

            if (offered == !record.Stale)
            {
                continue;
            }

            if (offered)
            {
                record.Stale = false;
                _store.Upsert(record);
                continue;
            }

            // ! Nothing to restore is the only licence to delete. A BackupPath row is the sole
            //   pointer to its vault copy.
            if (record.BackupPath is null && record.OutputPath is null)
            {
                drop.Add(record.Id);
                continue;
            }

            record.Stale = true;
            _store.Upsert(record);
        }

        Drop(drop);
    }

    // Records for items outside the enabled libraries.
    public void MarkOutOfScope(IEnumerable<Guid> inScope)
    {
        var seen = new HashSet<Guid>(inScope);

        // ! An empty scope has measured nothing. Marking on it blanks the panel over an
        //   unmounted share or a library that has not finished loading.
        if (seen.Count == 0)
        {
            return;
        }

        var stranded = _store.GetAll()
            .Where(r => !r.Stale && !seen.Contains(r.ItemId))
            .ToList();

        if (stranded.Count == 0)
        {
            return;
        }

        foreach (var record in stranded)
        {
            record.Stale = true;
        }

        _store.UpsertMany(stranded);
        _logger.LogInformation(
            "Marked {Count} records stale for items outside the enabled libraries",
            stranded.Count);
    }

    private void Drop(List<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var id in ids)
        {
            _vault.Discard(id);
        }

        _store.RemoveMany(ids);
        _logger.LogInformation("Dropped {Count} records for subtitles that are gone", ids.Count);
    }
}
