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
    private readonly ISubtitleSource _source;
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
        ISubtitleSource source,
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
        _source = source;
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
        // ! Retired is the only split allowed between these two. Any other is how FAILED came
        //   to disagree with failed.
        var all = _store.GetAll();
        var stored = all.Where(SyncOutcome.OnStageTable).ToList();
        var records = all.Where(SyncOutcome.OnCards).ToList();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Dependencies = SummarizeDependencies(config),

            InFlight = _queue.InFlight,
            // ! The cards do not sum to this. A SetAside row is counted here and on none of them.
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
            Downloaded = records.Count(IsDownloadSurvivor),
            // A payload fetch and a retry both park here.
            Waiting = records.Count(r => r.Status == SyncStatus.Pending),

            Stages = SummarizeStages(stored, config),

            UnsupportedReasons = records
                .Where(r => r.Status == SyncStatus.Unsupported && r.Message is not null)
                .GroupBy(r => WithoutStatusPrefix(r.Message!))
                .OrderByDescending(g => g.Count())
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .Take(ReasonLimit)
                .ToList(),

            // Split the same way the cards are; one list over both would total neither.
            RefusalReasons = Reasons(records.Where(SyncOutcome.IsAudioRefusal)),
            FailureReasons = Reasons(records.Where(r =>
                r.Status == SyncStatus.Failed && !SyncOutcome.IsAudioRefusal(r)))
        });
    }

    // ! High enough never to cut a real list. The valve is for a message that stops collapsing
    //   into its group and renders one row per subtitle.
    private const int ReasonLimit = 100;

    private static object Reasons(IEnumerable<SyncRecord> records)
        => records
            .Where(r => r.Message is not null)
            .GroupBy(r => Summarize(r.Message!))
            .OrderByDescending(g => g.Count())
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .Take(ReasonLimit)
            .ToList();

    private static readonly string[] StatusPrefixes =
        { "Rejected:", "Failed:", "Skipped:", "Unsupported:" };

    // ! The heading over the list already names the outcome. Kept on the stored message, which
    //   the log lines and LogOutcome still read.
    private static string WithoutStatusPrefix(string line)
    {
        foreach (var prefix in StatusPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[prefix.Length..].TrimStart();
            return rest.Length == 0 ? line : char.ToUpperInvariant(rest[0]) + rest[1..];
        }

        return line;
    }

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
        var line = WithoutStatusPrefix(WithoutDetail(message
            .Split('\n')
            .Select(part => part.Trim())
            .LastOrDefault(part => part.Length > 0) ?? message));

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

    // ! Magnitude, not direction, over runs whose result was kept. Signed, an early subtitle
    //   cancels a late one.
    private static long? MedianAppliedOffset(List<SyncRecord> records)
    {
        var applied = records
            .Where(r => r.Status == SyncStatus.Synced && r.AppliedOffsetMs is not null)
            .Select(r => Math.Abs(r.AppliedOffsetMs!.Value))
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

        if (config.AcquireMissingSubtitles)
        {
            dependencies.Add(ProviderDependency(config));
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

    // ! Three situations a plain count collapses into one. The middle one names the provider
    //   that misled the admin, since nothing in the API says which providers download.
    private object ProviderDependency(PluginConfiguration config)
    {
        const string Name = "Subtitle providers";

        if (_source.Survey(config) is not { } providers)
        {
            return Dependency(
                Name,
                false,
                "No movie or episode is in the library yet, so the provider list cannot be read.");
        }

        var enabled = providers.Where(p => p.IsEnabled).ToList();
        var downloaders = enabled.Where(p => p.IsDownloader).Select(p => p.Name).ToList();

        if (downloaders.Count > 0)
        {
            // ! "then", not "and". The first provider to answer is the one that is used.
            return Dependency(Name, true, string.Join(", then ", downloaders) + ".");
        }

        if (enabled.Count == 0)
        {
            return Dependency(Name, false, "None installed. Nothing will be downloaded.");
        }

        var installed = string.Join(", ", enabled.Select(p => p.Name));
        return Dependency(
            Name,
            false,
            $"{installed} {(enabled.Count == 1 ? "is" : "are")} installed, but "
            + $"{(enabled.Count == 1 ? "it does" : "none of them")} "
            + "download subtitles. Nothing will be downloaded.");
    }

    // The installed providers, so the settings page can judge the names the admin typed.
    [HttpGet("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetProviders()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Shipped = DownloadProviders.Shipped,
            Installed = _source.Survey(config)?
                .Select(p => new { p.Name, p.IsDownloader, p.IsEnabled })
                .ToList()
        });
    }

    // ! Survivors only, and no File.Exists. Status carries whether the file is still there,
    //   one scan behind; a stat per record per poll is thousands of calls over a slow share.
    private static bool IsDownloadSurvivor(SyncRecord record)
        => record.Status == SyncStatus.Synced
           && record.Provenance == SubtitleProvenance.Created
           && record.Stages.Exists(s =>
               s.Kind == SubtitleStageKind.Acquire && s.Outcome == StageOutcome.Succeeded);

    // ! Fetches, not records — the only row on the table that counts them. One target can buy
    //   and refuse several candidates, and the ledger is what holds them.
    private static object AcquireRow(List<SyncRecord> records)
    {
        var attempts = records.SelectMany(r => r.AcquireAttempts).ToList();
        var stages = records
            .SelectMany(r => r.Stages.Where(s => s.Kind == SubtitleStageKind.Acquire))
            .ToList();

        return new
        {
            Kind = SubtitleStageKind.Acquire.ToString(),
            Succeeded = attempts.Count(a => a.Outcome == AcquireAttemptOutcome.Kept),

            // Targets that fetched nothing at all: nothing offered, or everything filtered out.
            Skipped = stages.Count(s => s.Outcome == StageOutcome.Skipped),
            Failed = attempts.Count(a => a.Outcome != AcquireAttemptOutcome.Kept),
            AverageMs = AverageMs(stages)
        };
    }

    // ! Only steps the settings actually turn on.
    private static List<object> SummarizeStages(List<SyncRecord> records, PluginConfiguration config)
    {
        // ! Paired back to its record. A stage outcome alone cannot tell a refusal from a failure.
        var byKind = records
            .SelectMany(r => r.Stages.Select(s => (Record: r, Stage: s)))
            .ToLookup(x => x.Stage.Kind);

        var pipeline = new[]
        {
            (Kind: SubtitleStageKind.Convert, On: config.ConvertImageSubtitles),
            (Kind: SubtitleStageKind.Sync, On: true),
            (Kind: SubtitleStageKind.Verify, On: true),
            (Kind: SubtitleStageKind.Transform, On: config.RemoveHearingImpairedTags),
            (Kind: SubtitleStageKind.Deduplicate, On: config.DeduplicateSubtitles)
        };

        var rows = pipeline
            .Where(step => step.On)
            .Select(step => (object)new
            {
                Kind = step.Kind.ToString(),
                Succeeded = byKind[step.Kind].Count(x => x.Stage.Outcome == StageOutcome.Succeeded),
                Skipped = byKind[step.Kind].Count(x => x.Stage.Outcome == StageOutcome.Skipped),
                // ! A refusal is not a failure. Only Verify can hold one, and it is reported on
                //   its own card and reason block, never here.
                Failed = byKind[step.Kind].Count(x =>
                    x.Stage.Outcome == StageOutcome.Failed
                    && (step.Kind != SubtitleStageKind.Verify || !SyncOutcome.IsAudioRefusal(x.Record))),
                AverageMs = AverageMs(byKind[step.Kind].Select(x => x.Stage))
            })
            .ToList();

        // ! First. Acquisition runs before anything else the pipeline does to a subtitle.
        if (config.AcquireMissingSubtitles)
        {
            rows.Insert(0, AcquireRow(records));
        }

        return rows;
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
