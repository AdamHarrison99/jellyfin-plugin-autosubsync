using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Api;

[ApiController]
[Route("AutoSubSync")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AutoSubSyncController : ControllerBase
{
    private readonly ISyncStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleDiscoveryService _discovery;
    private readonly SyncOrchestrator _orchestrator;
    private readonly SyncQueue _queue;
    private readonly RollbackService _rollback;
    private readonly AssyRuntime _runtime;
    private readonly ILogger<AutoSubSyncController> _logger;

    public AutoSubSyncController(
        ISyncStore store,
        ILibraryManager libraryManager,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SyncQueue queue,
        RollbackService rollback,
        AssyRuntime runtime,
        ILogger<AutoSubSyncController> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _queue = queue;
        _rollback = rollback;
        _runtime = runtime;
        _logger = logger;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var records = _store.GetAll();
        var engine = _runtime.GetStatus();

        return Ok(new
        {
            EngineReady = engine.IsReady,
            EngineState = engine.Readiness.ToString(),
            EngineMessage = engine.Message,

            InFlight = _queue.InFlight,
            Total = records.Count,
            Synced = records.Count(r => r.Status == SyncStatus.Synced),
            Failed = records.Count(r => r.Status == SyncStatus.Failed),
            Skipped = records.Count(r => r.Status == SyncStatus.Skipped),
            DryRun = records.Count(r => r.Status == SyncStatus.DryRun),
            Unsupported = records.Count(r => r.Status == SyncStatus.Unsupported),

            Stages = SummarizeStages(records),

            UnsupportedReasons = records
                .Where(r => r.Status == SyncStatus.Unsupported && r.Message is not null)
                .GroupBy(r => r.Message!)
                .OrderByDescending(g => g.Count())
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .ToList(),

            LastRecordUpdateUtc = records.Count == 0 ? null : records.Max(r => (DateTime?)r.UpdatedUtc)
        });
    }

    // One row per pipeline step that has actually run, in pipeline order.
    private static List<object> SummarizeStages(List<SyncRecord> records)
        => records
            .SelectMany(r => r.Stages)
            .GroupBy(s => s.Kind)
            .OrderBy(g => g.Key)
            .Select(g => (object)new
            {
                Kind = g.Key.ToString(),
                Succeeded = g.Count(s => s.Outcome == StageOutcome.Succeeded),
                Skipped = g.Count(s => s.Outcome == StageOutcome.Skipped),
                Failed = g.Count(s => s.Outcome == StageOutcome.Failed),
                ElapsedMs = g.Sum(s => s.ElapsedMs)
            })
            .ToList();

    // Queues the work and returns; does not wait for the sync.
    [HttpPost("SyncItem/{itemId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SyncItem([FromRoute] Guid itemId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return BadRequest();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var targets = _discovery.Discover(item, config);

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var target in targets)
                {
                    await _orchestrator.ProcessAsync(target, config, CancellationToken.None).ConfigureAwait(false);
                }

                _store.Flush();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queued sync failed for item {ItemId}", itemId);
            }
        });

        return Accepted(new { Queued = targets.Count });
    }

    [HttpPost("RollbackAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<RollbackReport> RollbackAll()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return BadRequest();
        }

        // ! Rolling back while syncs are running would race the placer.
        if (_queue.InFlight > 0)
        {
            return Conflict(new { Message = "Syncs are still running. Wait for them to finish." });
        }

        _logger.LogInformation("Rollback requested");
        return Ok(_rollback.RollbackAll(config));
    }

    [HttpPost("ClearDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearDatabase()
    {
        var cleared = _store.Clear();
        _store.Flush();
        _logger.LogInformation("Cleared {Count} AutoSubSync records", cleared);
        return Ok(new { Cleared = cleared });
    }
}
