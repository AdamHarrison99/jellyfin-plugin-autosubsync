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
                    "{Item} ({Key}) is out of attempts and unchanged",
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
            record.Status = SyncStatus.Pending;
            record.Message = "Cancelled.";
            SafeUpsert(record);
            return record;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error syncing {Item} ({Key})", target.ItemName, target.Key);
            record.Status = SyncStatus.Failed;
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
            var chain = SyncToolCapabilities.SelectChain(config.SyncToolChain, extension)
                .Take(Math.Max(1, config.MaxAttempts))
                .ToList();

            if (chain.Count == 0)
            {
                record.Status = SyncStatus.Unsupported;
                record.Message = $"No configured engine reads {extension} subtitles.";
                SafeUpsert(record);
                return record;
            }

            var attempt = await RunChainAsync(target, record, chain, inputPath, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.ProducedPath is null)
            {
                return Fail(record, attempt.Message);
            }

            if (IsBelowMinimumOffset(inputPath, attempt.ProducedPath, config))
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

        if (await _seConv.EnsureReadyAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
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

        // ! Checked only once the file is known to need it; OCR is a large download.
        if (await _seConv.EnsureReadyAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
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

    private record ChainAttempt(string? ProducedPath, string? Message);

    private async Task<ChainAttempt> RunChainAsync(
        SubtitleTarget target,
        SyncRecord record,
        IReadOnlyList<string> chain,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var scratchDir = ScratchDirectory();
        string? lastMessage = null;

        foreach (var tool in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scratchOutput = Path.Combine(
                scratchDir,
                Guid.NewGuid().ToString("N") + Path.GetExtension(inputPath));

            AssyInvocationResult invocation;

            try
            {
                invocation = await _runner
                    .SyncAsync(target.VideoPath, inputPath, scratchOutput, tool, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(scratchOutput);
                throw;
            }

            record.AttemptCount++;
            record.ToolUsed = invocation.Result?.Tool ?? tool;
            record.ReferenceUsed = target.VideoPath;
            record.ElapsedMs = invocation.Result?.ElapsedMs ?? 0;
            record.ReturnCode = invocation.ExitCode;

            if (invocation.Succeeded && invocation.Result?.Ok == true)
            {
                // ! Only trust a path we handed the engine.
                var produced = IsWithin(scratchDir, invocation.Result.Output) ? invocation.Result.Output! : scratchOutput;
                if (File.Exists(produced))
                {
                    return new ChainAttempt(produced, null);
                }

                lastMessage = "The engine reported success but wrote no file.";
            }
            else
            {
                var message = invocation.Result?.Message;
                lastMessage = string.IsNullOrWhiteSpace(message) ? invocation.StandardError : message;
            }

            TryDelete(scratchOutput);
            _logger.LogDebug("{Tool} failed for {Item}: {Message}", tool, target.ItemName, lastMessage);
        }

        return new ChainAttempt(null, lastMessage);
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

    // A failed record retries only once its inputs change, or the user asks for it.
    internal static bool IsExhausted(SyncRecord record, SubtitleTarget target, PluginConfiguration config)
        => record.Status == SyncStatus.Failed
           && record.AttemptCount >= config.MaxAttempts
           && FingerprintMatches(record, target, target.SubtitlePath);

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

    private static bool IsBelowMinimumOffset(string inputPath, string outputPath, PluginConfiguration config)
    {
        if (config.MinimumOffsetMs <= 0)
        {
            return false;
        }

        var before = SubtitleOffsetProbe.TryGetFirstCueMs(inputPath);
        var after = SubtitleOffsetProbe.TryGetFirstCueMs(outputPath);

        // Unparseable timings: keep the result.
        if (before is null || after is null)
        {
            return false;
        }

        return Math.Abs(after.Value - before.Value) < config.MinimumOffsetMs;
    }

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

    private SyncRecord Fail(SyncRecord record, string? message)
    {
        record.Status = SyncStatus.Failed;
        record.Message = string.IsNullOrWhiteSpace(message) ? "Sync failed." : message;
        _logger.LogWarning("Sync failed for {Item}: {Message}", record.ItemName, record.Message);
        SafeUpsert(record);
        return record;
    }

    // Returns null so a stage can hand its failure straight back to the pipeline.
    private string? FailStage(SyncRecord record, string? message, SubtitleStageKind kind)
    {
        record.Status = SyncStatus.Failed;
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
