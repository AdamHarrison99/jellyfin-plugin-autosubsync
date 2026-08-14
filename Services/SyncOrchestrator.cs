using System.Diagnostics;
using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// The per-target pipeline.
public class SyncOrchestrator
{
    private const string SeConvToolName = "seconv";

    private readonly IAssyCliRunner _runner;
    private readonly ISubtitleExtractor _extractor;
    private readonly ImageSubtitleExtractor _imageExtractor;
    private readonly ISeConvRunner _seConv;
    private readonly ISyncStore _store;
    private readonly SyncQueue _queue;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SubtitlePlacer _placer;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IAssyCliRunner runner,
        ISubtitleExtractor extractor,
        ImageSubtitleExtractor imageExtractor,
        ISeConvRunner seConv,
        ISyncStore store,
        SyncQueue queue,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        SubtitlePlacer placer,
        ILogger<SyncOrchestrator> logger)
    {
        _applicationPaths = applicationPaths;
        _runner = runner;
        _extractor = extractor;
        _imageExtractor = imageExtractor;
        _seConv = seConv;
        _store = store;
        _queue = queue;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _placer = placer;
        _logger = logger;
    }

    public async Task<SyncRecord> ProcessAsync(
        SubtitleTarget target,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var record = _store.GetByTargetKey(target.ItemId, target.Key) ?? NewRecord(target);

        try
        {
            if (IsExhausted(record, target, config))
            {
                _logger.LogDebug(
                    "{Item} ({Key}) failed and is unchanged since",
                    target.ItemName,
                    target.Key);
                return record;
            }

            if (target.UnsupportedReason is { } reason)
            {
                record.Status = SyncStatus.Unsupported;
                record.Message = reason;
                SafeUpsert(record);
                return record;
            }

            // ! Must stay ahead of all filesystem work.
            if (config.DryRunMode)
            {
                record.Status = SyncStatus.DryRun;
                record.Message = "Dry run: would sync this subtitle.";
                _logger.LogInformation(
                    "DRY RUN: would sync {Origin} subtitle for {Item}",
                    target.Origin,
                    target.ItemName);
                SafeUpsert(record);
                return record;
            }

            return await _queue
                .RunAsync(
                    ct => RunPipelineAsync(target, record, config, ct),
                    TryGetLength(target.VideoPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ! Record it, then let it propagate. Swallowing it here leaves the caller's loop
            //   running and every remaining target starts an engine only to be killed again.
            record.Status = SyncStatus.Pending;
            record.Message = "Cancelled.";
            SafeUpsert(record);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error syncing {Item} ({Key})", target.ItemName, target.Key);
            record.Status = SyncStatus.Failed;
            record.RejectedOffsetMs = null;
            record.Message = ex.Message;
            SafeUpsert(record);
            return record;
        }
    }

    // ! Must not throw; called from catch blocks.
    private void SafeUpsert(SyncRecord record, SubtitleStageKind kind = SubtitleStageKind.Sync)
    {
        try
        {
            StampStage(record, kind);
            _store.Upsert(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record the result for {Item}", record.ItemName);
        }
    }

    // One funnel for every exit path, so no outcome reaches the store without its stage.
    private static void StampStage(SyncRecord record, SubtitleStageKind kind)
    {
        if (record.Status == SyncStatus.Pending)
        {
            return;
        }

        var outcome = record.Status switch
        {
            SyncStatus.Synced => StageOutcome.Succeeded,
            SyncStatus.Skipped or SyncStatus.DryRun or SyncStatus.Unsupported => StageOutcome.Skipped,
            _ => StageOutcome.Failed
        };

        var stage = record.RecordStage(kind, outcome, record.ToolUsed);
        stage.Message = record.Message;
        stage.ElapsedMs = record.ElapsedMs;
    }

    private static void RecordStage(
        SyncRecord record,
        SubtitleStageKind kind,
        StageOutcome outcome,
        string? message,
        long elapsedMs)
    {
        var stage = record.RecordStage(kind, outcome, SeConvToolName);
        stage.Message = message;
        stage.ElapsedMs = elapsedMs;
    }

    private async Task<SyncRecord> RunPipelineAsync(
        SubtitleTarget target,
        SyncRecord record,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var scratch = new List<string>();

        try
        {
            // ! Must precede extraction.
            if (IsStillCurrent(record, target, target.SubtitlePath))
            {
                _logger.LogDebug("{Item} ({Key}) is unchanged since the last sync", target.ItemName, target.Key);
                return record;
            }

            string? inputPath;

            if (target.RequiresOcr)
            {
                inputPath = await ConvertAsync(target, record, scratch, cancellationToken)
                    .ConfigureAwait(false);

                if (inputPath is null)
                {
                    return record;
                }
            }
            else
            {
                inputPath = target.Origin == SubtitleOrigin.External
                    ? target.SubtitlePath
                    : Track(scratch, await _extractor
                        .ExtractAsync(target.VideoPath, target.StreamIndex!.Value, target.Codec, cancellationToken)
                        .ConfigureAwait(false));

                if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                {
                    return Fail(record, "Could not produce a subtitle file to sync.");
                }
            }

            // ! Fingerprint the source, never a converted copy; the copy does not survive the run.
            CaptureFingerprint(record, target, target.SubtitlePath ?? inputPath);

            var extension = Path.GetExtension(inputPath);

            if (!SyncEngine.Supports(extension))
            {
                record.Status = SyncStatus.Unsupported;
                record.Message = $"The sync engine does not read {extension} subtitles.";
                SafeUpsert(record);
                return record;
            }

            var attempt = await RunEngineAsync(target, record, inputPath, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.ProducedPath is null)
            {
                return Fail(record, attempt.Message);
            }

            var bounded = config.MaximumOffsetMs > 0 || config.MinimumOffsetMs > 0;
            var shift = bounded ? MeasureShift(inputPath, attempt.ProducedPath) : null;

            if (config.MaximumOffsetMs > 0 && shift > config.MaximumOffsetMs)
            {
                TryDelete(attempt.ProducedPath);
                return Fail(
                    record,
                    $"The engine moved the subtitle by {shift}ms, past the {config.MaximumOffsetMs}ms limit.",
                    shift);
            }

            if (config.MinimumOffsetMs > 0 && shift < config.MinimumOffsetMs)
            {
                TryDelete(attempt.ProducedPath);
                record.Status = SyncStatus.Skipped;
                record.Message = $"Already in sync (shift under {config.MinimumOffsetMs}ms).";
                SafeUpsert(record);
                return record;
            }

            var finalPath = config.RemoveHearingImpairedTags
                ? await TransformAsync(target, record, attempt.ProducedPath, scratch, cancellationToken)
                    .ConfigureAwait(false)
                : attempt.ProducedPath;

            var placement = _placer.Place(target, record, finalPath, config);
            if (placement is null)
            {
                TryDelete(finalPath);
                return Fail(record, "Could not write the synced subtitle into the library.");
            }

            record.OutputPath = placement.OutputPath;
            record.BackupPath = placement.BackupPath;
            record.Provenance = placement.Provenance;

            // ! An in-place write replaces the file the fingerprint was taken from.
            if (placement.Provenance == SubtitleProvenance.Retimed)
            {
                RefreshSourceFingerprint(record, placement.OutputPath);
            }

            record.Status = SyncStatus.Synced;
            record.Message = null;
            SafeUpsert(record);

            if (config.RefreshItemAfterSync)
            {
                QueueRefresh(target.ItemId);
            }

            return record;
        }
        finally
        {
            foreach (var path in scratch)
            {
                TryDelete(path);
            }
        }
    }

    // OCR: turns a bitmap track into text an alignment engine can read.
    private async Task<string?> ConvertAsync(
        SubtitleTarget target,
        SyncRecord record,
        List<string> scratch,
        CancellationToken cancellationToken)
    {
        record.ToolUsed = SeConvToolName;

        if (await _seConv.EnsureOcrReadyAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            record.Status = SyncStatus.Unsupported;
            record.Message = unavailable;
            SafeUpsert(record, SubtitleStageKind.Convert);
            return null;
        }

        var source = target.Origin == SubtitleOrigin.External
            ? target.SubtitlePath
            : Track(scratch, await _imageExtractor
                .ExtractAsync(target.VideoPath, target.StreamIndex!.Value, target.Codec, cancellationToken)
                .ConfigureAwait(false));

        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            return FailStage(record, "Could not read the image subtitle track.", SubtitleStageKind.Convert);
        }

        var output = Track(scratch, ScratchPath(".srt"))!;
        var result = await _seConv
            .OcrAsync(source, output, target.Language, cancellationToken)
            .ConfigureAwait(false);

        record.ElapsedMs = result.ElapsedMs;

        if (!result.Succeeded)
        {
            return FailStage(record, result.Message, SubtitleStageKind.Convert);
        }

        RecordStage(record, SubtitleStageKind.Convert, StageOutcome.Succeeded, null, result.ElapsedMs);
        _logger.LogInformation(
            "OCR read {Item} ({Key}) in {Elapsed}ms", target.ItemName, target.Key, result.ElapsedMs);

        return result.OutputPath;
    }

    // Strips hearing-impaired annotations, but only from a track that carries them.
    private async Task<string> TransformAsync(
        SubtitleTarget target,
        SyncRecord record,
        string syncedPath,
        List<string> scratch,
        CancellationToken cancellationToken)
    {
        var detection = SdhDetector.Inspect(syncedPath);
        if (!detection.IsHearingImpaired)
        {
            RecordStage(
                record,
                SubtitleStageKind.Transform,
                StageOutcome.Skipped,
                "No hearing-impaired annotations to remove.",
                0);
            return syncedPath;
        }

        // ! Checked only once the file is known to need it; the converter is a large download.
        if (await _seConv.EnsureConverterReadyAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            RecordStage(record, SubtitleStageKind.Transform, StageOutcome.Skipped, unavailable, 0);
            return syncedPath;
        }

        var output = Track(scratch, ScratchPath(".srt"))!;
        var result = await _seConv
            .RemoveHearingImpairedAsync(syncedPath, output, cancellationToken)
            .ConfigureAwait(false);

        // ! A failed strip keeps the synced file. Losing the sync over a cosmetic pass is worse.
        if (!result.Succeeded)
        {
            RecordStage(record, SubtitleStageKind.Transform, StageOutcome.Failed, result.Message, result.ElapsedMs);
            _logger.LogWarning(
                "Could not strip hearing-impaired tags from {Item}: {Message}", target.ItemName, result.Message);
            return syncedPath;
        }

        RecordStage(record, SubtitleStageKind.Transform, StageOutcome.Succeeded, null, result.ElapsedMs);
        _logger.LogInformation(
            "Removed hearing-impaired tags from {Item} ({Marked} of {Total} cues)",
            target.ItemName,
            detection.MarkedCueCount,
            detection.CueCount);

        // The sidecar name drops its sdh token only once the tags are gone.
        target.IsHearingImpaired = false;
        scratch.Add(syncedPath);

        return result.OutputPath!;
    }

    private string ScratchDirectory()
    {
        var scratchDir = Path.Combine(_applicationPaths.TempDirectory, "AutoSubSync");
        Directory.CreateDirectory(scratchDir);
        return scratchDir;
    }

    private string ScratchPath(string extension)
        => Path.Combine(ScratchDirectory(), Guid.NewGuid().ToString("N") + extension);

    private static string? Track(List<string> scratch, string? path)
    {
        if (path is not null)
        {
            scratch.Add(path);
        }

        return path;
    }

    private record EngineAttempt(string? ProducedPath, string? Message);

    private async Task<EngineAttempt> RunEngineAsync(
        SubtitleTarget target,
        SyncRecord record,
        string inputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scratchDir = ScratchDirectory();
        var scratchOutput = Path.Combine(
            scratchDir,
            Guid.NewGuid().ToString("N") + Path.GetExtension(inputPath));

        AssyInvocationResult invocation;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            invocation = await _runner
                .SyncAsync(target.VideoPath, inputPath, scratchOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(scratchOutput);
            throw;
        }

        stopwatch.Stop();

        record.AttemptCount++;
        record.ToolUsed = invocation.Result?.Tool ?? SyncEngine.Name;
        record.ReferenceUsed = target.VideoPath;
        // ! Fall back to the wall clock: a timed-out attempt reports no elapsed time, and
        //   zero would hide the most expensive attempts from the stage averages.
        record.ElapsedMs = invocation.Result?.ElapsedMs ?? stopwatch.ElapsedMilliseconds;
        record.ReturnCode = invocation.ExitCode;

        if (invocation.Succeeded && invocation.Result?.Ok == true)
        {
            // ! Only trust a path we handed the engine.
            var produced = IsWithin(scratchDir, invocation.Result.Output) ? invocation.Result.Output! : scratchOutput;
            if (File.Exists(produced))
            {
                return new EngineAttempt(produced, null);
            }

            TryDelete(scratchOutput);
            return new EngineAttempt(null, "The engine reported success but wrote no file.");
        }

        var message = invocation.Result?.Message;
        var reason = string.IsNullOrWhiteSpace(message) ? invocation.StandardError : message;

        TryDelete(scratchOutput);

        if (invocation.TimedOut)
        {
            _logger.LogWarning(
                "The sync engine timed out after {Elapsed:n0}s on {Item}: {Message}",
                stopwatch.Elapsed.TotalSeconds,
                target.ItemName,
                reason);
        }
        else
        {
            _logger.LogDebug("The sync engine failed for {Item}: {Message}", target.ItemName, reason);
        }

        return new EngineAttempt(null, reason);
    }

    private static bool IsWithin(string directory, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // ! Ignoring case off Windows would accept a sibling directory as this one.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, comparison);
    }

    private void CaptureFingerprint(SyncRecord record, SubtitleTarget target, string inputPath)
    {
        // A changed input starts the attempt budget over.
        if (!FingerprintMatches(record, target, inputPath))
        {
            record.AttemptCount = 0;
        }

        try
        {
            var videoInfo = new FileInfo(target.VideoPath);
            record.VideoLength = videoInfo.Length;
            record.VideoPartialHash = FileFingerprint.TryComputePartial(target.VideoPath);

            // The video hash already covers an embedded track.
            if (target.Origin == SubtitleOrigin.Embedded)
            {
                record.SourceLength = 0;
                record.SourceLastWriteUtc = default;
                record.SourceSha256 = null;
                return;
            }

            var subtitleInfo = new FileInfo(inputPath);
            record.SourceLength = subtitleInfo.Length;
            record.SourceLastWriteUtc = subtitleInfo.LastWriteTimeUtc;
            record.SourceSha256 = FileFingerprint.TryComputeFull(inputPath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not fingerprint {Path}", inputPath);
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void RefreshSourceFingerprint(SyncRecord record, string path)
    {
        try
        {
            var info = new FileInfo(path);
            record.SourceLength = info.Length;
            record.SourceLastWriteUtc = info.LastWriteTimeUtc;
            record.SourceSha256 = FileFingerprint.TryComputeFull(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not fingerprint {Path}", path);
        }
    }

    internal static bool IsStillCurrent(SyncRecord record, SubtitleTarget target, string? subtitlePath)
        => (record.Status == SyncStatus.Synced || record.Status == SyncStatus.Skipped)
           && FingerprintMatches(record, target, subtitlePath);

    // ! The engine ran already. Identical inputs fail identically; only a change retries.
    internal static bool IsExhausted(SyncRecord record, SubtitleTarget target, PluginConfiguration config)
        => record.Status == SyncStatus.Failed
           && !LimitWouldNowAccept(record, config)
           && FingerprintMatches(record, target, target.SubtitlePath);

    // ! A rejection the limit caused is not an engine failure. Raising the limit has to retry it.
    private static bool LimitWouldNowAccept(SyncRecord record, PluginConfiguration config)
        => record.RejectedOffsetMs is { } rejected
           && (config.MaximumOffsetMs <= 0 || rejected <= config.MaximumOffsetMs);

    private static bool FingerprintMatches(SyncRecord record, SubtitleTarget target, string? subtitlePath)
    {
        if (record.VideoPartialHash is null
            || record.VideoPartialHash != FileFingerprint.TryComputePartial(target.VideoPath))
        {
            return false;
        }

        // The video hash already covers an embedded track.
        if (target.Origin == SubtitleOrigin.Embedded)
        {
            return true;
        }

        return record.SourceSha256 is not null
               && subtitlePath is not null
               && record.SourceSha256 == FileFingerprint.TryComputeFull(subtitlePath);
    }

    // Null on unparseable timings, which leaves both bounds untested and keeps the result.
    // ! Both ends: a rate correction can move the last cue by minutes and the first by nothing.
    private static long? MeasureShift(string inputPath, string outputPath)
    {
        var atStart = Delta(
            SubtitleOffsetProbe.TryGetFirstCueMs(inputPath),
            SubtitleOffsetProbe.TryGetFirstCueMs(outputPath));

        var atEnd = Delta(
            SubtitleOffsetProbe.TryGetLastCueMs(inputPath),
            SubtitleOffsetProbe.TryGetLastCueMs(outputPath));

        if (atStart is null)
        {
            return atEnd;
        }

        return atEnd is null ? atStart : Math.Max(atStart.Value, atEnd.Value);
    }

    private static long? Delta(long? before, long? after)
        => before is null || after is null ? null : Math.Abs(after.Value - before.Value);

    private void QueueRefresh(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        _providerManager.QueueRefresh(
            item.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
            RefreshPriority.Low);
    }

    private SyncRecord Fail(SyncRecord record, string? message, long? rejectedOffsetMs = null)
    {
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = rejectedOffsetMs;
        record.Message = string.IsNullOrWhiteSpace(message) ? "Sync failed." : message;
        _logger.LogWarning("Sync failed for {Item}: {Message}", record.ItemName, record.Message);
        SafeUpsert(record);
        return record;
    }

    // Returns null so a stage can hand its failure straight back to the pipeline.
    private string? FailStage(SyncRecord record, string? message, SubtitleStageKind kind)
    {
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = null;
        record.Message = string.IsNullOrWhiteSpace(message) ? "The OCR step failed." : message;
        _logger.LogWarning("{Kind} failed for {Item}: {Message}", kind, record.ItemName, record.Message);
        SafeUpsert(record, kind);
        return null;
    }

    private static SyncRecord NewRecord(SubtitleTarget target) => new()
    {
        Id = Guid.NewGuid(),
        ItemId = target.ItemId,
        ItemName = target.ItemName,
        TargetKey = target.Key,
        Origin = target.Origin,
        VideoPath = target.VideoPath,
        SourceSubtitlePath = target.SubtitlePath,
        Status = SyncStatus.Pending,
        CreatedUtc = DateTime.UtcNow
    };

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to clean up {Path}", path);
        }
    }
}
