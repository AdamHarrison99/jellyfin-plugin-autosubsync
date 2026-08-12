using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.EventHandlers;

public class LibraryEventHandler : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleDiscoveryService _discovery;
    private readonly SyncOrchestrator _orchestrator;
    private readonly SubtitleDeduplicator _deduplicator;
    private readonly LibraryScopeResolver _scopeResolver;
    private readonly AssyRuntime _runtime;
    private readonly ILogger<LibraryEventHandler> _logger;

    private readonly Dictionary<Guid, DateTime> _lastQueued = new();
    private readonly object _debounceLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    public LibraryEventHandler(
        ILibraryManager libraryManager,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SubtitleDeduplicator deduplicator,
        LibraryScopeResolver scopeResolver,
        AssyRuntime runtime,
        ILogger<LibraryEventHandler> logger)
    {
        _libraryManager = libraryManager;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _deduplicator = deduplicator;
        _scopeResolver = scopeResolver;
        _runtime = runtime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoSyncOnItemAdded)
        {
            return;
        }

        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        // Debounce runs before the scope test, which touches the filesystem.
        if (!ShouldQueue(e.Item.Id))
        {
            return;
        }

        // ! Never block: this runs on the library event thread.
        _ = Task.Run(() => ProcessAsync(e.Item), _shutdownCts.Token);
    }

    private async Task ProcessAsync(BaseItem item)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !_scopeResolver.IsInScope(item, config))
            {
                return;
            }

            // ! Readiness is checked before discovery; a missing payload must not write records.
            var status = await _runtime.EnsureReadyAsync(_shutdownCts.Token).ConfigureAwait(false);
            if (!status.IsReady)
            {
                _logger.LogDebug("Skipping {Name}: {Message}", item.Name, status.Message);
                return;
            }

            var targets = _discovery.Discover(item, config);

            foreach (var target in targets)
            {
                await _orchestrator
                    .ProcessAsync(target, config, _shutdownCts.Token)
                    .ConfigureAwait(false);
            }

            _deduplicator.ProcessItem(item.Id, targets, config);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event-driven sync failed for {Name}", item.Name);
        }
    }

    private bool ShouldQueue(Guid itemId)
    {
        lock (_debounceLock)
        {
            var now = DateTime.UtcNow;

            if (_lastQueued.TryGetValue(itemId, out var last) && now - last < DebounceWindow)
            {
                return false;
            }

            _lastQueued[itemId] = now;

            // Bound the debounce map.
            if (_lastQueued.Count > 5000)
            {
                foreach (var stale in _lastQueued
                             .Where(kvp => now - kvp.Value > DebounceWindow)
                             .Select(kvp => kvp.Key)
                             .ToList())
                {
                    _lastQueued.Remove(stale);
                }
            }

            return true;
        }
    }

    public void Dispose()
    {
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
