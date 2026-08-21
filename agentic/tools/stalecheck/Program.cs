// Does the status panel stop counting what the library no longer has, without stranding a backup?
//
// Mutation: let Reconcile drop every unoffered record. The vault cases then lose the only pointer
// to their backup and rollback can never restore those files.
//
// Mutation: let Reconcile match on TargetKey alone. The renamed survivor is then reported gone
// the run after deduplication tidied its name.
//
// Mutation: make OnStageTable exclude Retired, or set Stale where SubtitleDeduplicator.Remove sets
// Retired. Every duplicate the plugin removes then vanishes from the panel that reports the work.
//
// Mutation: let Reconcile un-retire on the offered path alone, without testing the file. Jellyfin
// advertises a deleted sidecar until the item's metadata refreshes, so the row rejoins the cards
// and the pass after the refresh marks it stale, taking the removal off the stage table with it.
//
// Mutation: let Reconcile un-retire on a key match. An embedded key names a stream inside the
// video and outlives the sidecar deduplication deleted, so that row returns to the cards as
// synced with no file behind it.

// Mutation: drop the Downloaded clause from Reconcile. A download the plugin placed fills the very
// gap that offered it, so the target is gone the next scan and the downloaded card empties itself.
//
// Mutation: make Downloaded true without testing the file. A download the user deleted then stays
// on the card while the language it was bought for is empty again.

using Jellyfin.Plugin.AutoSubSync;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "stalecheck-" + Guid.NewGuid().ToString("N"));
var media = Path.Combine(sandbox, "media");
Directory.CreateDirectory(media);

var itemId = Guid.NewGuid();
var video = Path.Combine(media, "Movie (2011).mkv");

void Check(string name, Func<string?> body)
{
    string? failure;
    try
    {
        failure = body();
    }
    catch (Exception ex)
    {
        failure = ex.Message;
    }

    Console.WriteLine(failure is null ? $"  ok    {name}" : $"  FAIL  {name}: {failure}");
    if (failure is not null)
    {
        failures++;
    }
}

(RecordReconciler Reconciler, FakeStore Store, BackupVault Vault) Build()
{
    var root = Path.Combine(sandbox, "server-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    var paths = new PluginPaths(new StubPaths(root), NullLogger<PluginPaths>.Instance);
    var vault = new BackupVault(paths, NullLogger<BackupVault>.Instance);
    var store = new FakeStore();

    return (new RecordReconciler(store, vault, NullLogger<RecordReconciler>.Instance), store, vault);
}

SyncRecord Record(string key, SyncStatus status)
    => new()
    {
        Id = Guid.NewGuid(),
        ItemId = itemId,
        ItemName = "Movie",
        TargetKey = key,
        VideoPath = video,
        Status = status,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

SubtitleTarget Target(string key, string? subtitlePath = null)
    => new()
    {
        ItemId = itemId,
        ItemName = "Movie",
        VideoPath = video,
        Origin = SubtitleOrigin.External,
        SubtitlePath = subtitlePath,
        Key = key
    };

// An embedded track carries no SubtitlePath: its key names a stream inside the video.
SubtitleTarget Embedded(string key)
    => new()
    {
        ItemId = itemId,
        ItemName = "Movie",
        VideoPath = video,
        Origin = SubtitleOrigin.Embedded,
        StreamIndex = 3,
        Key = key
    };

string WriteFile(string name)
{
    var path = Path.Combine(media, name);
    File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
    return path;
}

Console.WriteLine("stalecheck");

Check("an offered target is left alone", () =>
{
    var (reconciler, store, _) = Build();
    var record = Record("ext:movie.eng.srt", SyncStatus.Synced);
    store.Upsert(record);

    reconciler.Reconcile(itemId, [Target("ext:movie.eng.srt")]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the record was removed";
    }

    return after.Stale ? "it was marked stale" : null;
});

Check("a failed row for a target that is gone is removed", () =>
{
    var (reconciler, store, _) = Build();
    var record = Record("ext:movie.eng.srt", SyncStatus.Failed);
    store.Upsert(record);

    reconciler.Reconcile(itemId, []);

    return store.GetById(record.Id) is null ? null : "the row survived with nothing to restore";
});

Check("a row holding a backup is kept and stops counting", () =>
{
    var (reconciler, store, vault) = Build();
    var original = WriteFile("backed-up.eng.srt");
    var record = Record("ext:backed-up.eng.srt", SyncStatus.Synced);
    record.Provenance = SubtitleProvenance.Retimed;
    record.BackupPath = vault.Store(record.Id, original);
    record.OutputPath = original;
    store.Upsert(record);

    if (record.BackupPath is null || !File.Exists(record.BackupPath))
    {
        return "the vault copy was never made";
    }

    reconciler.Reconcile(itemId, []);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was removed and the backup is now unreachable";
    }

    if (!after.Stale)
    {
        return "it is still counted";
    }

    return File.Exists(after.BackupPath!) ? null : "the vault copy was discarded";
});

Check("a created output still on disk keeps its row", () =>
{
    var (reconciler, store, _) = Build();
    var output = WriteFile("created.eng.autosync.srt");
    var record = Record("ext:created.eng.srt", SyncStatus.Synced);
    record.Provenance = SubtitleProvenance.Created;
    record.OutputPath = output;
    store.Upsert(record);

    reconciler.Reconcile(itemId, []);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "rollback lost the only proof the plugin wrote that file";
    }

    return after.Stale ? null : "it is still counted";
});

Check("a target that comes back is counted again", () =>
{
    var (reconciler, store, _) = Build();
    var record = Record("ext:movie.eng.srt", SyncStatus.Synced);
    record.Stale = true;
    store.Upsert(record);

    reconciler.Reconcile(itemId, [Target("ext:movie.eng.srt")]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the record was removed";
    }

    return after.Stale ? "it is still uncounted" : null;
});

Check("a renamed survivor is matched by its path", () =>
{
    var (reconciler, store, _) = Build();
    var canonical = Path.Combine(media, "Movie (2011).eng.srt");
    var record = Record("ext:Movie (2011).eng.2.srt", SyncStatus.Synced);
    record.RenamedFromPath = Path.Combine(media, "Movie (2011).eng.2.srt");
    record.OutputPath = canonical;
    store.Upsert(record);

    reconciler.Reconcile(itemId, [Target("ext:Movie (2011).eng.srt", canonical)]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was removed";
    }

    return after.Stale ? "deduplication's own rename read as a missing subtitle" : null;
});

Check("an item outside the enabled libraries stops counting", () =>
{
    var (reconciler, store, _) = Build();
    var visible = Record("ext:movie.eng.srt", SyncStatus.Synced);
    var hidden = Record("ext:other.eng.srt", SyncStatus.Synced);
    hidden.ItemId = Guid.NewGuid();
    store.Upsert(visible);
    store.Upsert(hidden);

    reconciler.MarkOutOfScope([itemId]);

    if (store.GetById(hidden.Id) is not { Stale: true })
    {
        return "the out-of-scope row is still counted";
    }

    return store.GetById(visible.Id) is { Stale: false } ? null : "a visited item was marked stale";
});

Check("a stale row is not reopened by retry", () =>
{
    var live = Record("ext:movie.eng.srt", SyncStatus.Failed);
    var gone = Record("ext:other.eng.srt", SyncStatus.Failed);
    gone.Stale = true;

    var reopened = SyncStore.ReopenFailedIn([live, gone]);

    if (reopened != 1)
    {
        return $"reopened {reopened} records, expected 1";
    }

    return gone.Status == SyncStatus.Failed ? null : "a target nothing offers was queued again";
});

Check("a retired row leaves the cards and stays on the stage table", () =>
{
    var removed = Record("ext:movie.eng.2.srt", SyncStatus.Synced);
    removed.Retired = true;

    if (SyncOutcome.OnCards(removed))
    {
        return "a file the plugin deleted is still counted as in the library";
    }

    return SyncOutcome.OnStageTable(removed)
        ? null
        : "the removal it records is invisible, which is the defect K1 fixed";
});

Check("a stale row leaves both", () =>
{
    var gone = Record("ext:movie.eng.srt", SyncStatus.Synced);
    gone.Stale = true;

    return !SyncOutcome.OnCards(gone) && !SyncOutcome.OnStageTable(gone)
        ? null
        : "a target nothing offers is still counted somewhere";
});

Check("a live row is on both", () =>
{
    var live = Record("ext:movie.eng.srt", SyncStatus.Synced);

    return SyncOutcome.OnCards(live) && SyncOutcome.OnStageTable(live)
        ? null
        : "an offered target went missing from the panel";
});

Check("Reconcile leaves a retired row alone", () =>
{
    var (reconciler, store, _) = Build();
    var output = WriteFile("removed.eng.2.srt");
    var record = Record("ext:removed.eng.2.srt", SyncStatus.Synced);
    record.Retired = true;
    record.OutputPath = output;
    record.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Succeeded);
    store.Upsert(record);

    reconciler.Reconcile(itemId, []);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped and its removal is unreportable";
    }

    if (after.Stale)
    {
        return "it was restamped stale, which hides the stage again";
    }

    return after.Stages.Any(s => s.Kind == SubtitleStageKind.Deduplicate)
        ? null
        : "the removal stage was lost";
});

Check("a removed duplicate put back by hand rejoins the cards", () =>
{
    var (reconciler, store, _) = Build();
    var restored = WriteFile("restored.eng.2.srt");
    var record = Record("ext:restored.eng.2.srt", SyncStatus.Synced);
    record.Retired = true;
    record.OutputPath = restored;
    store.Upsert(record);

    reconciler.Reconcile(itemId, [Target("ext:restored.eng.2.srt", restored)]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped";
    }

    return SyncOutcome.OnCards(after) ? null : "a file that is back is still uncounted";
});

// Jellyfin keeps advertising a sidecar the plugin deleted until the item's metadata is refreshed,
// so discovery offers the path and the file behind it is gone. Un-retiring on that offer hands the
// row to the stale branch on the next pass, and the removal leaves the stage table for good.
Check("a removal offered by stale metadata stays retired", () =>
{
    var (reconciler, store, _) = Build();
    var phantom = Path.Combine(media, "phantom.eng.2.srt");
    var record = Record("ext:phantom.eng.2.srt", SyncStatus.Synced);
    record.Retired = true;
    record.OutputPath = phantom;
    record.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Succeeded);
    store.Upsert(record);

    // The offer names the deleted file; nothing wrote it back.
    reconciler.Reconcile(itemId, [Target("ext:phantom.eng.2.srt", phantom)]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped and its removal is unreportable";
    }

    if (!after.Retired)
    {
        return "it was un-retired by an offer with no file behind it";
    }

    // The pass after the metadata refresh no longer offers it.
    reconciler.Reconcile(itemId, []);

    after = store.GetById(record.Id);
    if (after is null || !SyncOutcome.OnStageTable(after))
    {
        return "the removal left the stage table";
    }

    return SyncOutcome.OnCards(after)
        ? "a sidecar deduplication deleted is counted as synced again"
        : null;
});

Check("an embedded row whose sidecar was removed stays retired", () =>
{
    var (reconciler, store, _) = Build();
    var record = Record("emb:3:subrip", SyncStatus.Synced);
    record.Retired = true;

    // Deduplication deleted this; nothing wrote it back.
    record.OutputPath = Path.Combine(media, "extracted.eng.autosubsync.srt");
    record.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Succeeded);
    store.Upsert(record);

    // The stream is inside the video, so discovery offers this key on every scan.
    reconciler.Reconcile(itemId, [Embedded("emb:3:subrip")]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped and its removal is unreportable";
    }

    if (!SyncOutcome.OnStageTable(after))
    {
        return "the removal left the stage table";
    }

    return SyncOutcome.OnCards(after)
        ? "a sidecar deduplication deleted is counted as synced again"
        : null;
});

Check("an old removal marked stale is retired on load", () =>
{
    var removal = Record("ext:movie.eng.2.srt", SyncStatus.Synced);
    removal.Stale = true;
    removal.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Succeeded).Message =
        SyncStore.RemovedAsDuplicate + "Movie (2011).eng.srt.";

    var rename = Record("ext:movie.eng.srt", SyncStatus.Synced);
    rename.Stale = true;
    rename.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Succeeded).Message =
        "Renamed to Movie (2011).eng.srt once its duplicates were removed.";

    var dryRun = Record("ext:movie.fre.2.srt", SyncStatus.DryRun);
    dryRun.Stale = true;
    dryRun.RecordStage(SubtitleStageKind.Deduplicate, StageOutcome.Skipped).Message =
        "Would be removed as a duplicate of Movie (2011).fre.srt.";

    var count = SyncStore.RetireRemovedDuplicates([removal, rename, dryRun]);

    if (count != 1)
    {
        return $"retired {count} records, expected 1";
    }

    if (!SyncOutcome.OnStageTable(removal) || SyncOutcome.OnCards(removal))
    {
        return "the removal did not land on the stage table alone";
    }

    if (rename.Retired || dryRun.Retired)
    {
        return "a rename or a dry run was mistaken for a removal";
    }

    // Idempotent: a second load must not double-count or undo anything.
    return SyncStore.RetireRemovedDuplicates([removal, rename, dryRun]) == 0
        ? null
        : "the migration runs again on every load";
});

Check("a retired row is not reopened by retry", () =>
{
    var live = Record("ext:movie.eng.srt", SyncStatus.Failed);
    var removed = Record("ext:movie.eng.2.srt", SyncStatus.Failed);
    removed.Retired = true;

    var reopened = SyncStore.ReopenFailedIn([live, removed]);

    if (reopened != 1)
    {
        return $"reopened {reopened} records, expected 1";
    }

    return removed.Status == SyncStatus.Failed ? null : "a file the plugin deleted was queued again";
});

Console.WriteLine();
Console.WriteLine("A downloaded subtitle, from the gap that bought it to the day it is deleted");

// A language the item has nothing in. Discovery offers this until something fills it.
SubtitleTarget Gap(string language)
    => new()
    {
        ItemId = itemId,
        ItemName = "Movie",
        VideoPath = video,
        Origin = SubtitleOrigin.Acquired,
        Language = language,
        Key = SubtitleTarget.AcquireKey(language)
    };

SyncRecord Bought(string language, string? output)
{
    var record = Record(SubtitleTarget.AcquireKey(language), SyncStatus.Synced);
    record.Origin = SubtitleOrigin.Acquired;
    record.Provenance = SubtitleProvenance.Created;
    record.OutputPath = output;
    record.RecordStage(SubtitleStageKind.Acquire, StageOutcome.Succeeded);
    return record;
}

Check("a gap with nothing bought yet is left alone", () =>
{
    var (reconciler, store, _) = Build();
    var record = Record(SubtitleTarget.AcquireKey("eng"), SyncStatus.Failed);
    record.Origin = SubtitleOrigin.Acquired;
    store.Upsert(record);

    reconciler.Reconcile(itemId, [Gap("eng")]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "an offered gap was dropped";
    }

    return after.Stale ? "an offered gap was marked stale" : null;
});

// ! The one the whole clause exists for. Success is what stops the target being offered.
Check("a download the plugin placed keeps counting", () =>
{
    var (reconciler, store, _) = Build();
    var output = WriteFile("Movie (2011).eng.autosubsync.srt");
    var record = Bought("eng", output);
    store.Upsert(record);

    // The placed file fills the language, so no acquire target is offered for it any more.
    reconciler.Reconcile(itemId, []);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped and rollback can never delete that file";
    }

    if (after.Stale)
    {
        return "a subtitle sitting in the library left the downloaded card";
    }

    return SyncOutcome.OnCards(after) ? null : "the download is no longer counted";
});

Check("a download the user deleted is offered again, not dropped", () =>
{
    var (reconciler, store, _) = Build();
    var output = WriteFile("Movie (2011).spa.autosubsync.srt");
    var record = Bought("spa", output);
    store.Upsert(record);

    File.Delete(output);

    // The language is empty again, so discovery offers the gap a second time.
    reconciler.Reconcile(itemId, [Gap("spa")]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row rollback needs was dropped";
    }

    return after.Stale ? "the returning gap was not counted" : null;
});

Check("a deleted download nobody wants any more stops counting", () =>
{
    var (reconciler, store, _) = Build();
    var output = WriteFile("Movie (2011).fre.autosubsync.srt");
    var record = Bought("fre", output);
    store.Upsert(record);

    File.Delete(output);

    // Downloading turned off: no gap is offered and no file answers the row.
    reconciler.Reconcile(itemId, []);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return null;
    }

    return after.Stale ? null : "a download that is gone is still counted";
});

Check("a download in a library that left scope stops counting", () =>
{
    var (reconciler, store, _) = Build();
    var record = Bought("eng", WriteFile("Movie (2011).eng.2.autosubsync.srt"));
    store.Upsert(record);

    reconciler.MarkOutOfScope([Guid.NewGuid()]);

    var after = store.GetById(record.Id);
    if (after is null)
    {
        return "the row was dropped";
    }

    return after.Stale ? null : "an item outside the enabled libraries is still counted";
});

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
}

Console.WriteLine(failures == 0 ? "stalecheck passed" : $"stalecheck FAILED ({failures})");
return failures == 0 ? 0 : 1;

internal sealed class FakeStore : ISyncStore
{
    private readonly List<SyncRecord> _records = [];

    public List<SyncRecord> GetAll() => _records.ConvertAll(r => r.Clone());

    public SyncRecord? GetById(Guid recordId) => _records.FirstOrDefault(r => r.Id == recordId)?.Clone();

    public List<SyncRecord> GetByItemId(Guid itemId)
        => _records.Where(r => r.ItemId == itemId).Select(r => r.Clone()).ToList();

    public SyncRecord? GetByTargetKey(Guid itemId, string targetKey)
        => _records.FirstOrDefault(r => r.ItemId == itemId && r.TargetKey == targetKey)?.Clone();

    public List<SyncRecord> GetByStatus(SyncStatus status)
        => _records.Where(r => r.Status == status).Select(r => r.Clone()).ToList();

    public void Upsert(SyncRecord record)
    {
        _records.RemoveAll(r => r.Id == record.Id);
        _records.Add(record.Clone());
    }

    public void UpsertMany(IEnumerable<SyncRecord> records)
    {
        foreach (var record in records)
        {
            Upsert(record);
        }
    }

    public void Remove(Guid recordId) => _records.RemoveAll(r => r.Id == recordId);

    public void RemoveMany(IEnumerable<Guid> recordIds)
    {
        foreach (var id in recordIds)
        {
            Remove(id);
        }
    }

    public int ReopenFailed() => SyncStore.ReopenFailedIn(_records);

    public int Clear()
    {
        var count = _records.Count;
        _records.Clear();
        return count;
    }

    public void Flush()
    {
    }
}

internal sealed class StubPaths(string root) : IApplicationPaths
{
    public string ProgramDataPath => root;

    public string WebPath => Path.Combine(root, "web");

    public string ProgramSystemPath => root;

    public string DataPath => Path.Combine(root, "data");

    public string ImageCachePath => Path.Combine(root, "cache", "images");

    public string PluginsPath => Path.Combine(root, "plugins");

    public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

    public string LogDirectoryPath => Path.Combine(root, "log");

    public string ConfigurationDirectoryPath => Path.Combine(root, "config");

    public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

    public string CachePath => Path.Combine(root, "cache");

    public string TempDirectory => Path.Combine(root, "temp");

    public string VirtualDataPath => Path.Combine(root, "data");

    public string TrickplayPath => Path.Combine(root, "trickplay");

    public string BackupPath => Path.Combine(root, "backup");

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}
