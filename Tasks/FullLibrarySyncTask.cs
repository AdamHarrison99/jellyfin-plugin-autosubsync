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
    private readonly ISyncStore _store;
    private readonly BackupVault _vault;
    private readonly AssyRuntime _runtime;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FullLibrarySyncTask> _logger;

    public FullLibrarySyncTask(
        LibraryScopeResolver scopeResolver,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SubtitleDeduplicator deduplicator,
        ISyncStore store,
        BackupVault vault,
        AssyRuntime runtime,
        ILibraryManager libraryManager,
        ILogger<FullLibrarySyncTask> logger)
    {
        _scopeResolver = scopeResolver;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _deduplicator = deduplicator;
        _store = store;
        _vault = vault;
        _runtime = runtime;
        _libraryManager = libraryManager;
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

        // ! Check readiness once. Per-item failures would be thousands of rows for one problem.
        var status = await _runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsReady)
        {
            _logger.LogWarning("Skipping full library sync: {Message}", status.Message);
            return;
        }

        progress.Report(0);

        var items = _scopeResolver.GetItemsInScope(config);
        _logger.LogInformation("AutoSubSync full scan starting over {Count} items", items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targets = _discovery.Discover(items[i], config);

            foreach (var target in targets)
            {
                await _orchestrator.ProcessAsync(target, config, cancellationToken).ConfigureAwait(false);
            }

            _deduplicator.ProcessItem(items[i].Id, targets, config);

            progress.Report((double)(i + 1) / items.Count * 100);
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
