using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Tasks;

public class FullLibrarySyncTask : IScheduledTask
{
    private readonly LibraryScopeResolver _scopeResolver;
    private readonly SubtitleDiscoveryService _discovery;
    private readonly SyncOrchestrator _orchestrator;
    private readonly SubtitleDeduplicator _deduplicator;
    private readonly ItemChangeGate _gate;
    private readonly ISyncStore _store;
    private readonly BackupVault _vault;
    private readonly AssyRuntime _runtime;
    private readonly ILibraryManager _libraryManager;
    private readonly SyncCancellation _cancellation;
    private readonly VobSubStaging _vobSub;
    private readonly ILogger<FullLibrarySyncTask> _logger;

    public FullLibrarySyncTask(
        LibraryScopeResolver scopeResolver,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SubtitleDeduplicator deduplicator,
        ItemChangeGate gate,
        ISyncStore store,
        BackupVault vault,
        AssyRuntime runtime,
        ILibraryManager libraryManager,
        SyncCancellation cancellation,
        VobSubStaging vobSub,
        ILogger<FullLibrarySyncTask> logger)
    {
        _vobSub = vobSub;
        _scopeResolver = scopeResolver;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _deduplicator = deduplicator;
        _gate = gate;
        _store = store;
        _vault = vault;
        _runtime = runtime;
        _libraryManager = libraryManager;
        _cancellation = cancellation;
        _logger = logger;
    }

    public string Name => "Full Library Sync";

    public string Key => "AutoSubSyncFullLibrarySync";

    public string Description =>
        "Scans every movie and episode in the configured libraries, finds their external and embedded "
        + "subtitle tracks, and synchronizes the ones that have not already been synchronized. Subtitles "
        + "whose source file is unchanged since the last successful run are skipped. In dry run mode "
        + "matches are recorded and logged but no files are written.";

    public string Category => "AutoSubSync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("Plugin configuration unavailable; skipping full library sync");
            return;
        }

        // Staged VobSub payloads outlive the scan that made them.
        _vobSub.Sweep();

        // ! Check readiness once. Per-item failures would be thousands of rows for one problem.
        var status = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsReady)
        {
            _logger.LogWarning("Skipping full library sync: {Message}", status.Message);
            return;
        }

        // ! A stop must reach syncs an event or the API started; those run under their own tokens.
        using var stopEverything = cancellationToken.Register(_cancellation.StopAll);

        progress.Report(0);

        var items = _scopeResolver.GetItemsInScope(config);

        // ! The ceiling the queue could ever admit. Offering more only parks threads on it.
        var parallelism = Math.Clamp(config.ResolveMaxConcurrentSyncs(), 1, SyncQueue.HardMax);

        _logger.LogInformation(
            "AutoSubSync full scan starting over {Count} items, up to {Parallelism} at a time",
            items.Count,
            parallelism);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };

        var done = 0;

        try
        {
            // ! One item per worker, its own targets in order. SyncQueue is still the gate that
            //   decides how many engines run.
            await Parallel.ForEachAsync(
                items,
                options,
                async (item, ct) =>
                {
                    var targets = _discovery.Discover(item, config);

                    foreach (var target in targets)
                    {
                        await _orchestrator.ProcessAsync(target, config, ct).ConfigureAwait(false);
                    }

                    _deduplicator.ProcessItem(item.Id, targets, config);

                    // ! Closes the item against the refresh its own writes just queued.
                    _gate.Commit(item, config);

                    progress.Report((double)Interlocked.Increment(ref done) / items.Count * 100);
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ! Flush before rethrowing; everything synced so far is already on disk and the
            //   records are the only thing that stops the next run from redoing it.
            _store.Flush();
            _logger.LogInformation("AutoSubSync full scan cancelled");
            throw;
        }

        Prune();
        _store.Flush();

        progress.Report(100);
        _logger.LogInformation("AutoSubSync full scan finished");
    }

    // Drops records for items missing from the library, and their backups.
    private void Prune()
    {
        // ! Both must be gone. Removing a record whose backup survives strands that backup.
        var orphaned = _store.GetAll()
            .Where(r => _libraryManager.GetItemById(r.ItemId) is null && !File.Exists(r.VideoPath))
            .ToList();

        if (orphaned.Count == 0)
        {
            return;
        }

        foreach (var record in orphaned)
        {
            _vault.Discard(record.Id);
            _gate.Forget(record.ItemId);
        }

        _store.RemoveMany(orphaned.Select(r => r.Id));
        _logger.LogInformation("Pruned {Count} records for items no longer in the library", orphaned.Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
