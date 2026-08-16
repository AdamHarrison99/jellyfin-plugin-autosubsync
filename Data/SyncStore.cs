using System.Text.Json;
using Jellyfin.Plugin.AutoSubSync.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Data;

public interface ISyncStore
{
    List<SyncRecord> GetAll();

    SyncRecord? GetById(Guid recordId);

    List<SyncRecord> GetByItemId(Guid itemId);

    SyncRecord? GetByTargetKey(Guid itemId, string targetKey);

    List<SyncRecord> GetByStatus(SyncStatus status);

    void Upsert(SyncRecord record);

    void UpsertMany(IEnumerable<SyncRecord> records);

    void Remove(Guid recordId);

    void RemoveMany(IEnumerable<Guid> recordIds);

    // Puts every failed record back in the queue. Returns how many were reopened.
    int ReopenFailed();

    int Clear();

    // Writes pending changes to disk now. Call at the end of a batch of work.
    void Flush();
}

public class SyncStore : ISyncStore, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataFilePath;
    private readonly string _backupFilePath;
    private readonly object _lock = new();
    private readonly ILogger<SyncStore> _logger;
    private readonly List<SyncRecord> _records;
    private readonly Timer _flushTimer;
    private bool _dirty;
    private bool _disposed;

    public SyncStore(PluginPaths paths, ILogger<SyncStore> logger)
    {
        _logger = logger;

        var pluginDataDir = paths.Home;
        Directory.CreateDirectory(pluginDataDir);

        _dataFilePath = Path.Combine(pluginDataDir, "records.json");
        _backupFilePath = Path.Combine(pluginDataDir, "records.backup.json");

        // Clean up a stale temp file left behind by a previous crash.
        var tempPath = _dataFilePath + ".tmp";
        if (File.Exists(tempPath))
        {
            try
            {
                File.Delete(tempPath);
                _logger.LogInformation("Cleaned up stale temp file: {Path}", tempPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to clean up stale temp file: {Path}", tempPath);
            }
        }

        _records = Load();

        // Writes are coalesced.
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    // Accessors hand out copies.
    public List<SyncRecord> GetAll()
    {
        lock (_lock)
        {
            return _records.ConvertAll(r => r.Clone());
        }
    }

    public SyncRecord? GetById(Guid recordId)
    {
        lock (_lock)
        {
            return _records.Find(r => r.Id == recordId)?.Clone();
        }
    }

    public List<SyncRecord> GetByItemId(Guid itemId)
    {
        lock (_lock)
        {
            return _records.Where(r => r.ItemId == itemId).Select(r => r.Clone()).ToList();
        }
    }

    public SyncRecord? GetByTargetKey(Guid itemId, string targetKey)
    {
        lock (_lock)
        {
            return _records.Find(r =>
                r.ItemId == itemId &&
                string.Equals(r.TargetKey, targetKey, StringComparison.Ordinal))?.Clone();
        }
    }

    public List<SyncRecord> GetByStatus(SyncStatus status)
    {
        lock (_lock)
        {
            return _records.Where(r => r.Status == status).Select(r => r.Clone()).ToList();
        }
    }

    public void Upsert(SyncRecord record)
    {
        lock (_lock)
        {
            UpsertLocked(record);
            _dirty = true;
        }
    }

    public void UpsertMany(IEnumerable<SyncRecord> records)
    {
        lock (_lock)
        {
            foreach (var record in records)
            {
                UpsertLocked(record);
            }

            _dirty = true;
        }
    }

    public void Remove(Guid recordId)
    {
        lock (_lock)
        {
            if (_records.RemoveAll(r => r.Id == recordId) > 0)
            {
                _dirty = true;
            }
        }
    }

    public void RemoveMany(IEnumerable<Guid> recordIds)
    {
        lock (_lock)
        {
            var idSet = new HashSet<Guid>(recordIds);
            if (_records.RemoveAll(r => idSet.Contains(r.Id)) > 0)
            {
                _dirty = true;
            }
        }
    }

    // ! Clears the bound as well as the status. A record that keeps RejectedOffsetMs reads as one
    //   the plugin measured and declined, and IsExhausted would park it again untried.
    public int ReopenFailed()
    {
        lock (_lock)
        {
            var reopened = ReopenFailedIn(_records);

            if (reopened > 0)
            {
                _dirty = true;
            }

            return reopened;
        }
    }

    internal static int ReopenFailedIn(List<SyncRecord> records)
    {
        var reopened = 0;

        foreach (var record in records)
        {
            if (record.Status != SyncStatus.Failed)
            {
                continue;
            }

            record.Status = SyncStatus.Pending;
            record.RejectedOffsetMs = null;
            record.Message = null;

            // ! Clear it w/ the rest. A retained flag describes a run whose stages and offset
            //   were just erased.
            record.RefusedByAudio = null;
            record.Stages?.Clear();
            reopened++;
        }

        return reopened;
    }

    public int Clear()
    {
        lock (_lock)
        {
            var count = _records.Count;
            if (count > 0)
            {
                _records.Clear();
                _dirty = true;
            }

            return count;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                Save();
                _dirty = false;
            }
            catch (Exception ex)
            {
                // ! Never propagate.
                _logger.LogError(ex, "Failed to write the sync store; will retry on the next flush");
            }
        }
    }

    private void UpsertLocked(SyncRecord record)
    {
        record.UpdatedUtc = DateTime.UtcNow;

        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        // Identity is (ItemId, TargetKey), not Id.
        var index = _records.FindIndex(r =>
            r.ItemId == record.ItemId &&
            string.Equals(r.TargetKey, record.TargetKey, StringComparison.Ordinal));

        if (index >= 0)
        {
            record.Id = _records[index].Id;
            record.CreatedUtc = _records[index].CreatedUtc;
            _records[index] = record.Clone();
            return;
        }

        if (record.CreatedUtc == default)
        {
            record.CreatedUtc = DateTime.UtcNow;
        }

        _records.Add(record.Clone());
    }

    private List<SyncRecord> Load()
    {
        if (!File.Exists(_dataFilePath))
        {
            return new List<SyncRecord>();
        }

        try
        {
            var json = File.ReadAllText(_dataFilePath);
            var records = JsonSerializer.Deserialize<List<SyncRecord>>(json, SerializerOptions)
                          ?? new List<SyncRecord>();

            var migrated = Migrate(records);
            if (migrated > 0)
            {
                _logger.LogInformation("Gave {Count} records from an earlier version a Sync stage", migrated);
            }

            var remeasured = Remeasure(records);
            if (remeasured.Stamped > 0)
            {
                // ! Persist the version, or every restart re-opens the newest rejections.
                _dirty = true;
            }

            if (remeasured.Reopened > 0)
            {
                _logger.LogInformation(
                    "Re-opened {Count} offset-limit rejections for the current measurement",
                    remeasured.Reopened);
            }

            return records;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse sync store, attempting backup restore");
            return LoadBackup();
        }
    }

    // Gives a record written before the staged pipeline the Sync stage it implicitly had.
    internal static int Migrate(List<SyncRecord> records)
    {
        var migrated = 0;

        foreach (var record in records)
        {
            record.Stages ??= new List<SubtitleStage>();

            // A Pending record never completed a stage.
            if (record.Stages.Count > 0 || record.Status == SyncStatus.Pending)
            {
                continue;
            }

            record.Stages.Add(new SubtitleStage
            {
                Kind = SubtitleStageKind.Sync,
                Outcome = OutcomeFor(record.Status),
                Tool = record.ToolUsed,
                Message = record.Message,
                ElapsedMs = record.ElapsedMs,
                CompletedUtc = record.UpdatedUtc
            });

            migrated++;
        }

        return migrated;
    }

    internal readonly record struct RemeasureReport(int Stamped, int Reopened);

    // A rejection measured by an older rule is not evidence about the current one.
    internal static RemeasureReport Remeasure(List<SyncRecord> records)
    {
        var stamped = 0;
        var reopened = 0;

        foreach (var record in records)
        {
            if (record.MeasurementVersion >= SyncRecord.CurrentMeasurementVersion)
            {
                continue;
            }

            record.MeasurementVersion = SyncRecord.CurrentMeasurementVersion;
            stamped++;

            if (record.Status != SyncStatus.Failed || record.RejectedOffsetMs is null)
            {
                continue;
            }

            record.Status = SyncStatus.Pending;
            record.RejectedOffsetMs = null;
            record.Message = null;
            // ! Everything describing the old run goes, as in ReopenFailed. The record is Pending
            //   → it runs again and re-stamps.
            record.RefusedByAudio = null;
            record.Stages?.Clear();
            reopened++;
        }

        return new RemeasureReport(stamped, reopened);
    }

    private static StageOutcome OutcomeFor(SyncStatus status) => status switch
    {
        SyncStatus.Synced => StageOutcome.Succeeded,
        SyncStatus.Skipped => StageOutcome.Skipped,
        SyncStatus.DryRun => StageOutcome.Skipped,
        SyncStatus.Unsupported => StageOutcome.Skipped,
        _ => StageOutcome.Failed
    };

    private List<SyncRecord> LoadBackup()
    {
        if (!File.Exists(_backupFilePath))
        {
            _logger.LogWarning("No backup file found, starting with an empty sync store");
            return new List<SyncRecord>();
        }

        try
        {
            var json = File.ReadAllText(_backupFilePath);
            var records = JsonSerializer.Deserialize<List<SyncRecord>>(json, SerializerOptions)
                          ?? new List<SyncRecord>();
            Migrate(records);
            Remeasure(records);
            _logger.LogInformation("Restored {Count} records from backup", records.Count);

            File.Copy(_backupFilePath, _dataFilePath, overwrite: true);
            return records;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Backup file is also corrupt, starting with an empty sync store");
            return new List<SyncRecord>();
        }
    }

    private void Save()
    {
        if (File.Exists(_dataFilePath))
        {
            var backupTemp = _backupFilePath + ".tmp";
            File.Copy(_dataFilePath, backupTemp, overwrite: true);
            File.Move(backupTemp, _backupFilePath, overwrite: true);
        }

        var tempPath = _dataFilePath + ".tmp";
        var json = JsonSerializer.Serialize(_records, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _dataFilePath, overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        Flush();
        GC.SuppressFinalize(this);
    }
}
