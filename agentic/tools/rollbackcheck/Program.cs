// Does rollback put the library back exactly as it was, and refuse everything it cannot prove?
//
// Mutation: let Delete drop the IsPluginOutput test. The unmarked case then deletes a user file
// the plugin never wrote, which is the worst thing this plugin could do.
//
// Mutation: let Restore ignore RenamedFromPath. The renamed survivor then keeps the plugin's name
// forever and the restored original lands beside it as a second file.

// Mutation: drop the Spent test from RunAll. The ledger of refused candidates goes with the row,
// and the next sweep buys every one of them a second time against the user's provider account.

using Jellyfin.Plugin.AutoSubSync;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "rollbackcheck-" + Guid.NewGuid().ToString("N"));
var media = Path.Combine(sandbox, "media");
Directory.CreateDirectory(media);

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

string Write(string name, string text)
{
    var path = Path.Combine(media, name);
    File.WriteAllText(path, text);
    return path;
}

(RollbackService Service, FakeStore Store, BackupVault Vault) Build()
{
    var root = Path.Combine(sandbox, "server-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    var paths = new PluginPaths(new StubPaths(root), NullLogger<PluginPaths>.Instance);
    var vault = new BackupVault(paths, NullLogger<BackupVault>.Instance);
    var store = new FakeStore();

    return (new RollbackService(store, vault, NullLogger<RollbackService>.Instance), store, vault);
}

static PluginConfiguration Config() => new();

Console.WriteLine("Rollback");

Check("a retimed user file comes back from the vault", () =>
{
    var (service, store, vault) = Build();
    var path = Write("retimed.eng.srt", "the original text");
    var record = new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Retimed };

    record.BackupPath = vault.Store(record.Id, path, null);
    File.WriteAllText(path, "what the engine wrote");
    store.Upsert(record);

    var report = service.RollbackAll(Config());
    var text = File.ReadAllText(path);

    return report.Restored == 1 && text == "the original text"
        ? null : $"restored {report.Restored}, file now holds {text}";
});

Check("a file the plugin created is deleted", () =>
{
    var (service, store, _) = Build();
    var path = Write("created.eng.autosubsync.srt", "plugin output");
    store.Upsert(new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Created });

    var report = service.RollbackAll(Config());
    return report.Deleted == 1 && !File.Exists(path)
        ? null : $"deleted {report.Deleted}, exists {File.Exists(path)}";
});

Check("a file without the marker is refused, not deleted", () =>
{
    var (service, store, _) = Build();
    var path = Write("someone-elses.eng.srt", "a file the plugin never wrote");
    store.Upsert(new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Created });

    var report = service.RollbackAll(Config());
    return report.Failed == 1 && File.Exists(path)
        ? null : $"failed {report.Failed}, exists {File.Exists(path)}";
});

Check("a record whose file is already gone is skipped", () =>
{
    var (service, store, _) = Build();
    var path = Path.Combine(media, "absent.eng.autosubsync.srt");
    store.Upsert(new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Created });

    var report = service.RollbackAll(Config());
    return report.Skipped == 1 ? null : $"skipped {report.Skipped}";
});

Console.WriteLine("Rollback after deduplication renamed the survivor");

Check("a renamed survivor with a backup restores under its old name", () =>
{
    var (service, store, vault) = Build();
    var discriminated = Write("dedup-a.eng.0.srt", "the original text");
    var record = new SyncRecord
    {
        ItemName = "Film",
        OutputPath = discriminated,
        Provenance = SubtitleProvenance.Retimed
    };

    record.BackupPath = vault.Store(record.Id, discriminated, null);

    // What deduplication does once the duplicates are gone.
    var canonical = Path.Combine(media, "dedup-a.eng.srt");
    File.WriteAllText(discriminated, "what the engine wrote");
    File.Move(discriminated, canonical);
    record.RenamedFromPath = discriminated;
    record.OutputPath = canonical;
    store.Upsert(record);

    var report = service.RollbackAll(Config());

    return report.Restored == 1 && File.Exists(discriminated) && !File.Exists(canonical)
        && File.ReadAllText(discriminated) == "the original text"
        ? null
        : $"restored {report.Restored}, old name {File.Exists(discriminated)}, new name {File.Exists(canonical)}";
});

Check("a renamed file with no backup is simply named back", () =>
{
    var (service, store, _) = Build();
    var discriminated = Path.Combine(media, "dedup-b.eng.0.srt");
    var canonical = Write("dedup-b.eng.srt", "a subtitle the audio already agreed with");

    store.Upsert(new SyncRecord
    {
        ItemName = "Film",
        OutputPath = canonical,
        RenamedFromPath = discriminated,
        Provenance = SubtitleProvenance.Retimed
    });

    var report = service.RollbackAll(Config());

    return report.Restored == 1 && File.Exists(discriminated) && !File.Exists(canonical)
        ? null : $"restored {report.Restored}, old name {File.Exists(discriminated)}";
});

Check("nothing is renamed back over a file that already holds the name", () =>
{
    var (service, store, _) = Build();
    var discriminated = Write("dedup-c.eng.0.srt", "someone put this back");
    var canonical = Write("dedup-c.eng.srt", "the renamed survivor");

    store.Upsert(new SyncRecord
    {
        ItemName = "Film",
        OutputPath = canonical,
        RenamedFromPath = discriminated,
        Provenance = SubtitleProvenance.Retimed
    });

    var report = service.RollbackAll(Config());

    return report.Skipped == 1 && File.ReadAllText(discriminated) == "someone put this back"
        ? null : $"skipped {report.Skipped}, old name holds {File.ReadAllText(discriminated)}";
});

Check("a removed duplicate comes back", () =>
{
    var (service, store, vault) = Build();
    var duplicate = Write("dupe.eng.1.srt", "the duplicate's text");
    var record = new SyncRecord { ItemName = "Film", OutputPath = duplicate };

    record.BackupPath = vault.Store(record.Id, duplicate, "duplicate");
    File.Delete(duplicate);
    record.Provenance = SubtitleProvenance.Superseded;
    store.Upsert(record);

    var report = service.RollbackAll(Config());

    return report.Restored == 1 && File.Exists(duplicate)
        ? null : $"restored {report.Restored}, exists {File.Exists(duplicate)}";
});

Check("every undone record leaves the store", () =>
{
    var (service, store, _) = Build();
    var path = Write("gone.eng.autosubsync.srt", "plugin output");
    store.Upsert(new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Created });

    service.RollbackAll(Config());
    return store.GetAll().Count == 0 ? null : $"{store.GetAll().Count} records left";
});

Check("a record whose restore failed keeps its row", () =>
{
    var (service, store, _) = Build();
    var path = Write("kept.eng.srt", "a file the plugin never wrote");
    store.Upsert(new SyncRecord { ItemName = "Film", OutputPath = path, Provenance = SubtitleProvenance.Created });

    service.RollbackAll(Config());
    return store.GetAll().Count == 1 ? null : "the only pointer to the backup was dropped";
});

Console.WriteLine("Rollback of a downloaded subtitle");

SyncRecord Attempted(AcquireAttemptOutcome outcome, string id)
{
    var record = new SyncRecord { ItemName = "Film", Origin = SubtitleOrigin.Acquired };
    record.AcquireAttempts.Add(new AcquireAttempt
    {
        SubtitleId = id,
        ProviderName = "Open Subtitles",
        Outcome = outcome
    });
    return record;
}

// A download has no original behind it, so rollback deletes rather than restores.
Check("a kept download is deleted like anything else the plugin created", () =>
{
    var (service, store, _) = Build();
    var path = Write("bought.eng.autosubsync.srt", "a subtitle nobody had before");
    var record = Attempted(AcquireAttemptOutcome.Kept, "prov-1");
    record.Provenance = SubtitleProvenance.Created;
    record.OutputPath = path;
    store.Upsert(record);

    var report = service.RollbackAll(Config());

    if (report.Deleted != 1 || File.Exists(path))
    {
        return $"deleted {report.Deleted}, exists {File.Exists(path)}";
    }

    return store.GetAll().Count == 0 ? null : "the row survived the file it described";
});

Check("a download carrying the wrong name is refused, not deleted", () =>
{
    var (service, store, _) = Build();
    var path = Write("bought-unmarked.eng.srt", "a file the plugin never wrote");
    var record = Attempted(AcquireAttemptOutcome.Kept, "prov-2");
    record.Provenance = SubtitleProvenance.Created;
    record.OutputPath = path;
    store.Upsert(record);

    var report = service.RollbackAll(Config());
    return report.Failed == 1 && File.Exists(path)
        ? null : $"failed {report.Failed}, exists {File.Exists(path)}";
});

// ! Rollback undoes files. It cannot undo a download, and the ledger is the only record of one.
Check("a row whose candidates were all refused keeps its row", () =>
{
    var (service, store, _) = Build();
    var record = Attempted(AcquireAttemptOutcome.Misaligned, "prov-3");
    record.Status = SyncStatus.Failed;
    store.Upsert(record);

    service.RollbackAll(Config());

    var after = store.GetAll();
    if (after.Count != 1)
    {
        return "the only record of what was already bought and refused was dropped";
    }

    return after[0].AcquireAttempts.Count == 1 ? null : "the row survived without its ledger";
});

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
    // The sandbox is in the temp directory; leaving it is not a failure.
}

Console.WriteLine(failures == 0 ? "rollbackcheck: all cases pass" : $"rollbackcheck: {failures} failed");
return failures == 0 ? 0 : 1;

internal sealed class FakeStore : ISyncStore
{
    private readonly List<SyncRecord> _records = [];

    public List<SyncRecord> GetAll() => _records.ToList();

    public SyncRecord? GetById(Guid recordId) => _records.FirstOrDefault(r => r.Id == recordId);

    public List<SyncRecord> GetByItemId(Guid itemId) => _records.Where(r => r.ItemId == itemId).ToList();

    public SyncRecord? GetByTargetKey(Guid itemId, string targetKey)
        => _records.FirstOrDefault(r => r.ItemId == itemId && r.TargetKey == targetKey);

    public List<SyncRecord> GetByStatus(SyncStatus status) => _records.Where(r => r.Status == status).ToList();

    public void Upsert(SyncRecord record)
    {
        _records.RemoveAll(r => r.Id == record.Id);
        _records.Add(record);
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

    public int ReopenFailed() => 0;

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
