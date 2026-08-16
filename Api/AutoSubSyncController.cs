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
    private readonly SyncCancellation _cancellation;
    private readonly RollbackService _rollback;
    private readonly RecordReconciler _reconciler;
    private readonly ItemChangeGate _gate;
    private readonly AssyRuntime _runtime;
    private readonly SeConvRuntime _seConv;
    private readonly ILogger<AutoSubSyncController> _logger;

    public AutoSubSyncController(
        ISyncStore store,
        ILibraryManager libraryManager,
        SubtitleDiscoveryService discovery,
        SyncOrchestrator orchestrator,
        SyncQueue queue,
        SyncCancellation cancellation,
        RollbackService rollback,
        RecordReconciler reconciler,
        ItemChangeGate gate,
        AssyRuntime runtime,
        SeConvRuntime seConv,
        ILogger<AutoSubSyncController> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _queue = queue;
        _cancellation = cancellation;
        _rollback = rollback;
        _reconciler = reconciler;
        _gate = gate;
        _runtime = runtime;
        _seConv = seConv;
        _logger = logger;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        // ! One population. A card filtered apart from the stage table is how FAILED came to
        //   disagree with failed.
        var records = _store.GetAll().Where(r => !r.Stale).ToList();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Dependencies = SummarizeDependencies(config),

            InFlight = _queue.InFlight,
            Total = records.Count,
            Synced = records.Count(r => r.Status == SyncStatus.Synced),
            MedianAppliedOffsetMs = MedianAppliedOffset(records),
            // A result the audio refused is not a tool failure, and the two are not fixed alike.
            Failed = records.Count(r => r.Status == SyncStatus.Failed && !SyncOutcome.IsAudioRefusal(r)),
            Rejected = records.Count(SyncOutcome.IsAudioRefusal),
            Skipped = records.Count(SyncOutcome.NothingToDo),
            SourceMissing = records.Count(r =>
                r.Status == SyncStatus.Skipped && !SyncOutcome.NothingToDo(r)),
            DryRun = records.Count(r => r.Status == SyncStatus.DryRun),
            Unsupported = records.Count(r => r.Status == SyncStatus.Unsupported),
            // ! Counted so the cards add up to Total. A payload fetch and a retry both park here.
            Waiting = records.Count(r => r.Status == SyncStatus.Pending),

            Stages = SummarizeStages(records, config),

            UnsupportedReasons = records
                .Where(r => r.Status == SyncStatus.Unsupported && r.Message is not null)
                .GroupBy(r => r.Message!)
                .OrderByDescending(g => g.Count())
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .ToList(),

            // Split the same way the cards are; one list over both would total neither.
            RefusalReasons = Reasons(records.Where(SyncOutcome.IsAudioRefusal)),
            FailureReasons = Reasons(records.Where(r =>
                r.Status == SyncStatus.Failed && !SyncOutcome.IsAudioRefusal(r))),

            LastRecordUpdateUtc = records.Count == 0 ? null : records.Max(r => (DateTime?)r.UpdatedUtc)
        });
    }

    private static object Reasons(IEnumerable<SyncRecord> records)
        => records
            .Where(r => r.Message is not null)
            .GroupBy(r => Summarize(r.Message!))
            .OrderByDescending(g => g.Count())
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .Take(8)
            .ToList();

    // ! A trailing parenthetical is per-subtitle detail, and grouping wants the sentence alone.
    private static string WithoutDetail(string line)
    {
        var close = line.LastIndexOf(')');
        var open = close > 0 ? line.LastIndexOf('(', close - 1) : -1;

        return open > 0 ? line[..open].TrimEnd() + line[(close + 1)..] : line;
    }

    // ! An engine failure arrives as a whole stderr dump. The varying parts are file positions.
    private static string Summarize(string message)
    {
        var line = WithoutDetail(message
            .Split('\n')
            .Select(part => part.Trim())
            .LastOrDefault(part => part.Length > 0) ?? message);

        if (line.Length > 120)
        {
            line = line[..120] + "…";
        }

        var builder = new System.Text.StringBuilder(line.Length);
        var inNumber = false;

        foreach (var character in line)
        {
            if (char.IsAsciiDigit(character))
            {
                if (!inNumber)
                {
                    builder.Append('#');
                }

                inNumber = true;
                continue;
            }

            inNumber = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    // The typical correction, over runs whose result was kept.
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
                    : "Tesseract is not installed on this server. It must be installed and Jellyfin restarted."));
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
            (Kind: SubtitleStageKind.Verify, On: true),
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
                    await _orchestrator.ProcessAsync(target, config, _cancellation.Token).ConfigureAwait(false);
                }

                _reconciler.Reconcile(item.Id, targets);

                // ! Closes the item against the refresh these writes queue, as both other
                //   entry points do. Without it a manual sync costs a second pass.
                _gate.Commit(item, config);
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
            return Conflict(new { Message = "Syncs are still running." });
        }

        _logger.LogInformation("Rollback requested");
        return Ok(_rollback.RollbackAll(config));
    }

    [HttpPost("RetryFailed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult RetryFailed()
    {
        // ! Reopening a record the queue is working on would race its own write back.
        if (_queue.InFlight > 0)
        {
            return Conflict(new { Message = "Syncs are still running." });
        }

        var reopened = _store.ReopenFailed();
        _store.Flush();
        _logger.LogInformation("Reopened {Count} failed subtitles for retry", reopened);
        return Ok(new { Reopened = reopened });
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
