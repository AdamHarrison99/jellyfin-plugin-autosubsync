using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Configuration;
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
    private readonly SeConvRuntime _seConv;
    private readonly ILogger<AutoSubSyncController> _logger;

    public AutoSubSyncController(
        ISyncStore store,
        ILibraryManager libraryManager,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SyncQueue queue,
        RollbackService rollback,
        AssyRuntime runtime,
        SeConvRuntime seConv,
        ILogger<AutoSubSyncController> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _queue = queue;
        _rollback = rollback;
        _runtime = runtime;
        _seConv = seConv;
        _logger = logger;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var records = _store.GetAll();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Dependencies = SummarizeDependencies(config),

            InFlight = _queue.InFlight,
            Total = records.Count,
            Synced = records.Count(r => r.Status == SyncStatus.Synced),
            MedianAppliedOffsetMs = MedianAppliedOffset(records),
            Failed = records.Count(r => r.Status == SyncStatus.Failed),
            Skipped = records.Count(r => r.Status == SyncStatus.Skipped),
            DryRun = records.Count(r => r.Status == SyncStatus.DryRun),
            Unsupported = records.Count(r => r.Status == SyncStatus.Unsupported),

            Stages = SummarizeStages(records, config),

            UnsupportedReasons = records
                .Where(r => r.Status == SyncStatus.Unsupported && r.Message is not null)
                .GroupBy(r => r.Message!)
                .OrderByDescending(g => g.Count())
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .ToList(),

            LastRecordUpdateUtc = records.Count == 0 ? null : records.Max(r => (DateTime?)r.UpdatedUtc)
        });
    }

    // The typical correction, over runs whose result was kept. Null once nothing has been measured.
    private static long? MedianAppliedOffset(List<SyncRecord> records)
    {
        var applied = records
            .Where(r => r.Status == SyncStatus.Synced && r.AppliedOffsetMs is not null)
            .Select(r => r.AppliedOffsetMs!.Value)
            .Order()
            .ToList();

        return applied.Count == 0 ? null : applied[applied.Count / 2];
    }

    // ! Only what the settings ask for. A missing OCR tool is not a fault when OCR is off.
    private List<object> SummarizeDependencies(PluginConfiguration config)
    {
        var assy = _runtime.GetStatus();

        var dependencies = new List<object>
        {
            Dependency("Sync engine", assy.IsReady, assy.Message)
        };

        // ! Hearing-impaired stripping needs the converter, never Tesseract.
        if (config.ConvertImageSubtitles || config.RemoveHearingImpairedTags)
        {
            var converter = _seConv.GetConverterStatus();
            dependencies.Add(Dependency("Subtitle converter", converter.IsReady, converter.Message));
        }

        if (config.ConvertImageSubtitles)
        {
            var tesseract = SeConvRuntime.ResolveTesseractDirectory();
            dependencies.Add(Dependency(
                "Tesseract",
                tesseract is not null,
                tesseract is not null
                    ? $"Tesseract is installed at {tesseract}."
                    : "Tesseract is not installed on this server. Install it, then restart Jellyfin."));
        }

        return dependencies;
    }

    private static object Dependency(string name, bool ready, string message)
        => new { Name = name, Ready = ready, Message = message };

    // ! Only steps the settings actually turn on. Acquire is unbuilt, so it is not here.
    private static List<object> SummarizeStages(List<SyncRecord> records, PluginConfiguration config)
    {
        var byKind = records.SelectMany(r => r.Stages).ToLookup(s => s.Kind);

        var pipeline = new[]
        {
            (Kind: SubtitleStageKind.Convert, On: config.ConvertImageSubtitles),
            (Kind: SubtitleStageKind.Sync, On: true),
            (Kind: SubtitleStageKind.Transform, On: config.RemoveHearingImpairedTags),
            (Kind: SubtitleStageKind.Deduplicate, On: config.DeduplicateSubtitles)
        };

        return pipeline
            .Where(step => step.On)
            .Select(step => (object)new
            {
                Kind = step.Kind.ToString(),
                Succeeded = byKind[step.Kind].Count(s => s.Outcome == StageOutcome.Succeeded),
                Skipped = byKind[step.Kind].Count(s => s.Outcome == StageOutcome.Skipped),
                Failed = byKind[step.Kind].Count(s => s.Outcome == StageOutcome.Failed),
                AverageMs = AverageMs(byKind[step.Kind])
            })
            .ToList();
    }

    // ! Mean over the runs that were timed, never a lifetime total; a total only ever grows.
    private static long? AverageMs(IEnumerable<SubtitleStage> stages)
    {
        var timed = stages.Where(s => s.ElapsedMs > 0).ToList();
        return timed.Count == 0 ? null : (long)timed.Average(s => s.ElapsedMs);
    }

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
