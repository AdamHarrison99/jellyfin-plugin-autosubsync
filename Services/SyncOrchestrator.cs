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

    // ! Admits every framerate conversion in use; 23.976 to 30 is the widest at 25.1%.
    private const double MaximumRateDrift = 0.30;

    // Under this the engine did nothing worth writing a file for.
    private const int MinimumMovementMs = 100;

    // ! Cannot-align scores ≈10 a second, can-align 40 and up. Set at the lowest real reading:
    //   an unmeasurable title must clear what a genuine alignment scores.
    private const double MinimumEngineScore = 40;

    // Ceiling on a move nothing verified. Only reachable w/ audio confirmation turned off.
    private const long MaximumUnverifiedShiftMs = 60_000;

    private readonly IAssyCliRunner _runner;
    private readonly ISubtitleExtractor _extractor;
    private readonly ImageSubtitleExtractor _imageExtractor;
    private readonly ISeConvRunner _seConv;
    private readonly ISyncStore _store;
    private readonly SyncQueue _queue;
    private readonly TargetLocks _targets;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SyncVerifier _verifier;
    private readonly SubtitlePlacer _placer;
    private readonly VobSubStaging _vobSub;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IAssyCliRunner runner,
        ISubtitleExtractor extractor,
        ImageSubtitleExtractor imageExtractor,
        ISeConvRunner seConv,
        ISyncStore store,
        SyncQueue queue,
        TargetLocks targets,
        SyncVerifier verifier,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        SubtitlePlacer placer,
        VobSubStaging vobSub,
        ILogger<SyncOrchestrator> logger)
    {
        _applicationPaths = applicationPaths;
        _vobSub = vobSub;
        _runner = runner;
        _extractor = extractor;
        _imageExtractor = imageExtractor;
        _seConv = seConv;
        _store = store;
        _queue = queue;
        _targets = targets;
        _verifier = verifier;
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
        // ! Take the lease before reading the record; the read is only current while it is held.
        using var lease = await _targets
            .AcquireAsync(target.ItemId, target.Key, cancellationToken)
            .ConfigureAwait(false);

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
                record.Message = "Dry run: this subtitle would be synced.";
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
            record.Message = "Cancelled: the sync was stopped before it finished.";
            SafeUpsert(record);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error syncing {Item} ({Key})", target.ItemName, target.Key);
            record.Status = SyncStatus.Failed;
            record.RejectedOffsetMs = null;
            record.AppliedOffsetMs = null;
            record.RefusedByAudio = false;
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
            record.SettingsStamp = Plugin.Instance?.Configuration.OutcomeStamp();
            record.MeasurementVersion = SyncRecord.CurrentMeasurementVersion;
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
            if (IsStillCurrent(record, target, target.SubtitlePath, config))
            {
                _logger.LogDebug("{Item} ({Key}) is unchanged since the last sync", target.ItemName, target.Key);
                return record;
            }

            // ! A sidecar removed since the last scan has nothing to hand the engine.
            if (target.Origin == SubtitleOrigin.External
                && target.SubtitlePath is { } source
                && !File.Exists(source))
            {
                record.Status = SyncStatus.Skipped;
                record.AppliedOffsetMs = null;
                record.SkippedMovementMs = null;

                // ! The measurement described a file that is gone. Leaving it retries the record
                //   forever through ToleranceWouldNowSync and counts it as already in sync.
                record.AlignedAtMs = null;
                record.Message = "Skipped: the subtitle file is no longer on disk.";
                _logger.LogDebug("{Item} ({Key}) is gone from disk", target.ItemName, target.Key);
                SafeUpsert(record);
                return record;
            }

            // ! Must precede every stage that can fail. IsExhausted needs this on a failed record.
            CaptureFingerprint(record, target, target.SubtitlePath);

            if (SettledTwin(record, target, config) is { } twin)
            {
                return Adopt(record, target, twin);
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
                    return Fail(record, "Failed: no subtitle file could be produced to sync.");
                }
            }

            var extension = Path.GetExtension(inputPath);

            if (!SyncEngine.Supports(extension))
            {
                record.Status = SyncStatus.Unsupported;
                record.Message = SyncEngine.UnsupportedReason(extension);
                SafeUpsert(record);
                return record;
            }

            var starts = SyncVerifier.Starts(inputPath);
            var sample = starts is not null
                ? await _verifier
                    .SampleAsync(target.VideoPath, starts, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            if (sample is not null && starts is not null)
            {
                var before = SyncVerifier.Score(sample, starts);
                record.RecordStage(SubtitleStageKind.Verify, StageFor(before.Verdict));

                if (before is { Verdict: SyncVerdict.Aligned, BestShiftMs: { } sits })
                {
                    record.Status = SyncStatus.Skipped;
                    record.AlignedAtMs = sits;

                    // ! An OCR'd track has no text on disk yet. Dropping it here leaves the user
                    //   nothing and points deduplication at a bitmap it cannot read.
                    if (target.RequiresOcr)
                    {
                        var converted = config.RemoveHearingImpairedTags
                            ? await TransformAsync(target, record, inputPath, scratch, cancellationToken)
                                .ConfigureAwait(false)
                            : inputPath;

                        if (_placer.Place(target, record, converted, config) is not { } kept)
                        {
                            return Fail(
                                record,
                                "Failed: the converted subtitle could not be written into the library.");
                        }

                        record.OutputPath = kept.OutputPath;
                        record.BackupPath = kept.BackupPath;
                        record.Provenance = kept.Provenance;
                    }

                    // ! The file stands as it is, so deduplication has to be told where it is.
                    //   Without a path the slot reads as unsynced and its duplicates survive.
                    else if (target.Origin == SubtitleOrigin.External)
                    {
                        record.OutputPath ??= target.SubtitlePath;
                    }

                    record.Message = $"Skipped: already aligned with the audio ({sits} ms).";
                    _logger.LogInformation(
                        "Skipped {Item} ({Key}): its cues sit {Sits} ms from the speech, "
                        + "{Windows} windows, peak {Strength:F2}x",
                        target.ItemName,
                        target.Key,
                        sits,
                        before.Windows,
                        before.Strength);
                    SafeUpsert(record, SubtitleStageKind.Verify);
                    return record;
                }
            }

            var attempt = await RunEngineAsync(target, record, inputPath, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.ProducedPath is null)
            {
                return Fail(record, attempt.Message);
            }

            var change = SubtitleOffsetProbe.Measure(inputPath, attempt.ProducedPath);

            if (change.RateRatio is { } ratio && Math.Abs(ratio - 1) > MaximumRateDrift)
            {
                TryDelete(attempt.ProducedPath);
                return Fail(
                    record,
                    "Failed: the sync engine rescaled the subtitle by a factor that matches "
                    + "no known framerate conversion.");
            }

            var moved = Math.Max(change.ConstantMs ?? 0, change.DriftMs ?? 0);

            if (change.ConstantMs is not null && moved < MinimumMovementMs)
            {
                TryDelete(attempt.ProducedPath);
                record.Status = SyncStatus.Skipped;

                // ! Returns before the post-sync stamp. The pre-check's Misaligned would stand,
                //   and a discarded result is no verification failure.
                record.RecordStage(SubtitleStageKind.Verify, StageOutcome.Skipped);

                record.AppliedOffsetMs = change.ConstantMs;
                record.SkippedMovementMs = moved;
                record.Message =
                    $"Skipped: the sync engine moved the subtitle less than the "
                    + $"{MinimumMovementMs} ms minimum ({moved} ms).";
                _logger.LogInformation(
                    "Skipped {Item} ({Key}): {Moved} ms is below the {Minimum} ms minimum",
                    target.ItemName,
                    target.Key,
                    moved,
                    MinimumMovementMs);
                SafeUpsert(record);
                return record;
            }

            // ! Before the strip. Verification needs the cues the engine placed, not rewritten text.
            var verdict = sample is not null && SyncVerifier.Starts(attempt.ProducedPath) is { } placed
                ? SyncVerifier.Score(sample, placed)
                : await _verifier
                    .VerifyAsync(target.VideoPath, attempt.ProducedPath, cancellationToken)
                    .ConfigureAwait(false);

            record.RecordStage(SubtitleStageKind.Verify, StageFor(verdict.Verdict));

            if (verdict.Verdict == SyncVerdict.Misaligned)
            {
                TryDelete(attempt.ProducedPath);

                var drifting = verdict.DriftMs is { } spread
                    && Math.Abs(spread) > SyncVerifier.AlignedWithinMs;
                var miss = Math.Abs(drifting ? verdict.DriftMs!.Value : verdict.BestShiftMs ?? 0);

                _logger.LogWarning(
                    "Rejected the sync for {Item} ({Key}): {Miss} ms off the speech, drifting {Drifting}, "
                    + "{Windows} windows, peak {Strength:F2}x",
                    target.ItemName,
                    target.Key,
                    miss,
                    drifting,
                    verdict.Windows,
                    verdict.Strength);

                return Fail(
                    record,
                    drifting
                        ? "Rejected: the audio check found the offset drifting across the runtime."
                        : "Rejected: the audio check found the subtitle out of alignment.",
                    miss,
                    SubtitleStageKind.Verify);
            }

            // ! Drift goes unmeasured on an Inconclusive verdict and on any title too short for
            //   six windows. Hold an unchecked stretch to the tolerance the check applies.
            if (verdict.DriftMs is null
                && change.DriftMs is { } stretch
                && Math.Abs(stretch) > SyncVerifier.AlignedWithinMs)
            {
                TryDelete(attempt.ProducedPath);

                _logger.LogWarning(
                    "Rejected the sync for {Item} ({Key}): it stretches the subtitle by {Drift} ms "
                    + "across the runtime and the audio check never measured drift ({Windows} windows)",
                    target.ItemName,
                    target.Key,
                    stretch,
                    verdict.Windows);

                return Fail(
                    record,
                    "Rejected: the sync engine rescaled the subtitle across the runtime — the "
                    + "audio check did not measure that change.",
                    Math.Abs(stretch),
                    SubtitleStageKind.Verify);
            }

            // ! Backstop for a check that confirmed nothing. ¬a tight leash: reaching here means
            //   audio confirmation is off, and a sidecar for another release is legitimately late.
            if (verdict.Verdict == SyncVerdict.Inconclusive
                && change.ConstantMs is { } shift
                && Math.Abs(shift) > MaximumUnverifiedShiftMs)
            {
                TryDelete(attempt.ProducedPath);

                _logger.LogWarning(
                    "Rejected the sync for {Item} ({Key}): it moves the subtitle {Shift} ms and the "
                    + "audio check confirmed nothing",
                    target.ItemName,
                    target.Key,
                    shift);

                return Fail(
                    record,
                    "Rejected: the audio check reached no verdict and the sync engine moved the "
                    + "subtitle too far to accept unconfirmed.",
                    Math.Abs(shift),
                    SubtitleStageKind.Verify);
            }

            // ! Costs a parse of the produced file, so only where the gate below reads it or the
            //   debug line below reports it.
            var confidence = verdict.Verdict == SyncVerdict.Inconclusive
                || _logger.IsEnabled(LogLevel.Debug)
                    ? EngineConfidence(attempt)
                    : null;

            // ! Only where our own check could not measure the title, and only to refuse. The
            //   engine scoring its own alignment is ¬evidence that it is right.
            if (verdict.Verdict == SyncVerdict.Inconclusive)
            {
                // ! The check ran and returned no answer, which is not the same as a pass. Where
                //   confirmation is required that ends it, and the engine's score is not consulted.
                if (config.RequireAudioConfirmation)
                {
                    TryDelete(attempt.ProducedPath);

                    // ! The three numbers separate the gates: hits under the floor is a title
                    //   whose audio yielded too little, a low peak is a flat sweep.
                    _logger.LogWarning(
                        "Rejected the sync for {Item} ({Key}): the audio check could not confirm it "
                        + "({Windows} windows, peak {Strength:F2}x, {Hits} hits against a floor of "
                        + "{Floor}, {Onsets} onsets)",
                        target.ItemName,
                        target.Key,
                        verdict.Windows,
                        verdict.Strength,
                        verdict.Hits,
                        verdict.Floor,
                        verdict.Onsets);

                    return Fail(
                        record,
                        SyncOutcome.NoVerdictRefusal,
                        null,
                        SubtitleStageKind.Verify);
                }

                if (confidence is not { } tooLow)
                {
                    TryDelete(attempt.ProducedPath);

                    _logger.LogWarning(
                        "Rejected the sync for {Item} ({Key}): the audio check could not measure it "
                        + "and the engine never scored its own alignment",
                        target.ItemName,
                        target.Key);

                    return Fail(
                        record,
                        "Rejected: the audio check could not measure this title and the sync engine "
                        + "never scored its alignment.",
                        null,
                        SubtitleStageKind.Verify);
                }

                if (tooLow < MinimumEngineScore)
                {
                    TryDelete(attempt.ProducedPath);

                    _logger.LogWarning(
                        "Rejected the sync for {Item} ({Key}): the audio check could not measure it "
                        + "and the engine scored its own alignment at {Confidence:F1} a second",
                        target.ItemName,
                        target.Key,
                        tooLow);

                    return Fail(
                        record,
                        "Rejected: the audio check could not measure this title and the sync engine "
                        + "found no usable alignment.",
                        null,
                        SubtitleStageKind.Verify);
                }
            }

            if (confidence is { } accepted)
            {
                _logger.LogDebug(
                    "Verify passed for {Item} ({Key}): {Verdict}, {Windows} windows, peak "
                    + "{Strength:F2}x, the engine scored its own alignment at {Confidence:F1} a second",
                    target.ItemName,
                    target.Key,
                    verdict.Verdict,
                    verdict.Windows,
                    verdict.Strength,
                    accepted);
            }

            var finalPath = config.RemoveHearingImpairedTags
                ? await TransformAsync(target, record, attempt.ProducedPath, scratch, cancellationToken)
                    .ConfigureAwait(false)
                : attempt.ProducedPath;

            var placement = _placer.Place(target, record, finalPath, config);
            if (placement is null)
            {
                TryDelete(finalPath);
                return Fail(record, "Failed: the synced subtitle could not be written into the library.");
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
            record.AppliedOffsetMs = change.ConstantMs;
            record.SkippedMovementMs = null;
            record.Message = null;
            SafeUpsert(record);

            _logger.LogInformation(
                "Synced {Item} ({Key}): shifted {Shift}, rate correction {Drift}, {Elapsed}ms, wrote {Path} ({Provenance})",
                target.ItemName,
                target.Key,
                Describe(change.ConstantMs),
                Describe(change.DriftMs),
                record.ElapsedMs,
                placement.OutputPath,
                placement.Provenance);

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
            // ! Downloading is not unsupported. Pending leaves the target for the next sweep.
            record.Status = unavailable.IsTransient ? SyncStatus.Pending : SyncStatus.Unsupported;
            record.Message = unavailable.Message;
            SafeUpsert(record, SubtitleStageKind.Convert);
            return null;
        }

        var source = target.Origin == SubtitleOrigin.External
            ? target.SubtitlePath
            : Track(scratch, await _imageExtractor
                .ExtractAsync(target.VideoPath, target.StreamIndex!.Value, target.Codec, cancellationToken)
                .ConfigureAwait(false));

        // ! One stream of a multi-stream index, never the whole payload. Handing over the pair
        //   converts every language it declares into one file.
        if (source is not null && target.VobSubStream is { } vobSubStream)
        {
            source = await _vobSub.StageAsync(source, vobSubStream, cancellationToken).ConfigureAwait(false);

            if (source is null)
            {
                return FailStage(
                    record,
                    "Failed: the VobSub stream could not be prepared for reading.",
                    SubtitleStageKind.Convert);
            }
        }

        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            return FailStage(
                record,
                "Failed: the image subtitle track could not be read.",
                SubtitleStageKind.Convert);
        }

        var output = Track(scratch, ScratchPath(".srt"))!;
        var result = await _seConv
            .OcrAsync(source, output, target.Language, target.Codec, cancellationToken)
            .ConfigureAwait(false);

        record.ElapsedMs = result.ElapsedMs;

        if (!result.Succeeded)
        {
            return FailStage(record, result.Message, SubtitleStageKind.Convert);
        }

        // ! Every later gate judges timing, and OCR timings come off the index. Nothing else
        //   here would notice that the text is unreadable.
        var reading = OcrReadability.Read(result.OutputPath!);

        if (reading.IsNoise)
        {
            _logger.LogWarning(
                "Rejected the OCR for {Item} ({Key}): {Words} words averaging {Mean:F2} characters "
                + "with {Short:P0} of them under three, which is text the reader did not resolve",
                target.ItemName,
                target.Key,
                reading.Words,
                reading.MeanWordLength,
                reading.ShortWordShare);

            return FailStage(
                record,
                "Failed: the OCR tool could not read this track well enough to use.",
                SubtitleStageKind.Convert);
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
            RecordStage(record, SubtitleStageKind.Transform, StageOutcome.Skipped, unavailable.Message, 0);
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

    private record EngineAttempt(string? ProducedPath, string? Message, EngineAlignment? Alignment = null);

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
                return new EngineAttempt(produced, null, invocation.Alignment);
            }

            TryDelete(scratchOutput);
            return new EngineAttempt(null, "The engine reported success but wrote no file.");
        }

        // ! The engine can finish the work and then die printing its own result. A file it wrote
        //   in full is still the answer, and the rate bound and the audio check still judge it.
        if (!invocation.TimedOut && invocation.Result is null && Complete(inputPath, scratchOutput))
        {
            _logger.LogWarning(
                "The sync engine exited {Code} without reporting a result for {Item} but wrote a complete subtitle, which was kept.",
                invocation.ExitCode,
                target.ItemName);

            return new EngineAttempt(scratchOutput, null, invocation.Alignment);
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
            _logger.LogWarning("The sync engine failed for {Item}: {Message}", target.ItemName, reason);
        }

        // ! The engine's own output is logged above and kept out of the record. It is a stderr
        //   dump, and the panel groups records by message.
        return new EngineAttempt(
            null,
            invocation.TimedOut
                ? "Failed: the sync engine timed out."
                : "Failed: the sync engine did not complete.");
    }

    // The engine's own score for what it produced, per second of subtitle on screen.
    private static double? EngineConfidence(EngineAttempt attempt)
    {
        if (attempt.Alignment is not { } alignment || attempt.ProducedPath is not { } path)
        {
            return null;
        }

        if (SubtitleOffsetProbe.TryReadCues(path) is not { Count: > 0 } cues)
        {
            return null;
        }

        var shown = cues.Sum(cue => Math.Max(0, cue.EndMs - cue.StartMs)) / 1000.0;
        return alignment.PerShownSecond(shown);
    }

    // Cues lost against the input mean the write was cut short, not that the sync is done.
    private static bool Complete(string inputPath, string producedPath)
    {
        if (!File.Exists(producedPath))
        {
            return false;
        }

        var before = SubtitleOffsetProbe.TryReadCues(inputPath)?.Count ?? 0;
        var after = SubtitleOffsetProbe.TryReadCues(producedPath)?.Count ?? 0;

        return before > 0 && after >= before - (before / 20);
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

    private void CaptureFingerprint(SyncRecord record, SubtitleTarget target, string? subtitlePath)
    {
        // A changed input starts the attempt budget over.
        if (!FingerprintMatches(record, target, subtitlePath))
        {
            record.AttemptCount = 0;
        }

        try
        {
            var videoInfo = new FileInfo(target.VideoPath);
            record.VideoLength = videoInfo.Length;
            record.VideoPartialHash = FileFingerprint.TryComputePartial(target.VideoPath);

            // The video hash already covers an embedded track.
            if (target.Origin == SubtitleOrigin.Embedded || subtitlePath is null)
            {
                record.SourceLength = 0;
                record.SourceLastWriteUtc = default;
                record.SourceSha256 = null;
                return;
            }

            var subtitleInfo = new FileInfo(subtitlePath);
            record.SourceLength = subtitleInfo.Length;
            record.SourceLastWriteUtc = subtitleInfo.LastWriteTimeUtc;
            record.SourceSha256 = FileFingerprint.TryComputeSource(subtitlePath, target.VobSubStream);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not fingerprint {Path}", subtitlePath);
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

    // Another sidecar on this item holding the same bytes, already measured against this video.
    private SyncRecord? SettledTwin(SyncRecord record, SubtitleTarget target, PluginConfiguration config)
    {
        if (record.SourceSha256 is null || record.VideoPartialHash is null)
        {
            return null;
        }

        var stamp = config.OutcomeStamp();

        return _store.GetByItemId(target.ItemId).Find(other =>
            !string.Equals(other.TargetKey, target.Key, StringComparison.Ordinal)
            && other.SourceSha256 == record.SourceSha256
            && other.VideoPartialHash == record.VideoPartialHash
            && other.SettingsStamp == stamp
            && WroteNothing(other));
    }

    // ! Only a bound the plugin measured. A tool failure can be transient and must be retried.
    private static bool WroteNothing(SyncRecord record)
        => (record.Status == SyncStatus.Failed && record.RejectedOffsetMs is not null)
           || (record.Status == SyncStatus.Skipped && record.SkippedMovementMs is not null);

    private SyncRecord Adopt(SyncRecord record, SubtitleTarget target, SyncRecord twin)
    {
        record.Status = twin.Status;
        record.Message = twin.Message;
        record.AppliedOffsetMs = twin.AppliedOffsetMs;
        record.RejectedOffsetMs = twin.RejectedOffsetMs;
        // ! Carry it w/ the status. A failure adopted without it reads as a tool failure.
        record.RefusedByAudio = twin.RefusedByAudio;
        record.SkippedMovementMs = twin.SkippedMovementMs;
        record.ToolUsed = twin.ToolUsed;
        record.ReferenceUsed = target.VideoPath;
        record.ElapsedMs = 0;

        _logger.LogInformation(
            "Took the {Twin} result for {Item} ({Key}): identical subtitle text, same video",
            twin.TargetKey,
            target.ItemName,
            target.Key);

        SafeUpsert(record);
        return record;
    }

    internal static bool IsStillCurrent(
        SyncRecord record,
        SubtitleTarget target,
        string? subtitlePath,
        PluginConfiguration config)
        => (record.Status == SyncStatus.Synced || record.Status == SyncStatus.Skipped)
           && SettingsUnchanged(record, config)
           && !MinimumWouldNowSync(record, config)
           && !ToleranceWouldNowSync(record, config)
           && FingerprintMatches(record, target, subtitlePath);

    // ! The engine ran already. Identical inputs fail identically; only a change retries.
    internal static bool IsExhausted(SyncRecord record, SubtitleTarget target, PluginConfiguration config)
        => record.Status == SyncStatus.Failed
           && SettingsUnchanged(record, config)
           && !ToleranceWouldNowAccept(record, config)
           && FingerprintMatches(record, target, target.SubtitlePath);

    // ! A refusal the audio caused is not an engine failure. Widening the tolerance retries it.
    private static bool ToleranceWouldNowAccept(SyncRecord record, PluginConfiguration config)
        => record.RejectedOffsetMs is { } rejected
           && rejected <= SyncVerifier.AlignedWithinMs;

    // ! A subtitle the audio agreed with, never handed to the engine. Tightening retries it.
    private static bool ToleranceWouldNowSync(SyncRecord record, PluginConfiguration config)
        => record.Status == SyncStatus.Skipped
           && record.AlignedAtMs is { } sits
           && Math.Abs(sits) > SyncVerifier.AlignedWithinMs;

    // ! The mirror of the rejection rule. Lowering the minimum has to retry what it skipped.
    private static bool MinimumWouldNowSync(SyncRecord record, PluginConfiguration config)
        => record.Status == SyncStatus.Skipped
           && (record.SkippedMovementMs ?? record.AppliedOffsetMs) is { } moved
           && moved >= MinimumMovementMs;

    // An unstamped record predates stamping and is taken at face value.
    private static bool SettingsUnchanged(SyncRecord record, PluginConfiguration config)
        => record.SettingsStamp is null || record.SettingsStamp == config.OutcomeStamp();

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

        // ! Streams of one index share a payload. Without the stream, every one of them
        //   fingerprints identically and SettledTwin adopts another language's result.
        return record.SourceSha256 is not null
               && subtitlePath is not null
               && record.SourceSha256 == FileFingerprint.TryComputeSource(subtitlePath, target.VobSubStream);
    }

    private static string Describe(long? ms) => ms is { } value ? $"{value}ms" : "an unmeasured amount";

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

    // ! An inconclusive check is a skipped stage, not a failed one. Nothing was refused.
    private static StageOutcome StageFor(SyncVerdict verdict)
        => verdict switch
        {
            SyncVerdict.Aligned => StageOutcome.Succeeded,
            SyncVerdict.Misaligned => StageOutcome.Failed,
            _ => StageOutcome.Skipped
        };

    private SyncRecord Fail(
        SyncRecord record,
        string? message,
        long? rejectedOffsetMs = null,
        SubtitleStageKind kind = SubtitleStageKind.Sync)
    {
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = rejectedOffsetMs;
        record.AppliedOffsetMs = null;
        record.SkippedMovementMs = null;

        // ! The Verify stage is the audio check, so the kind names the refusal exactly. Written
        //   on every failure, ¬only the refusals, so it can never describe an earlier run.
        record.RefusedByAudio = kind == SubtitleStageKind.Verify;
        record.Message = string.IsNullOrWhiteSpace(message)
            ? "Failed: the sync did not complete."
            : message;
        LogOutcome(record);
        SafeUpsert(record, kind);
        return record;
    }

    // ! A refusal is not a tool failure, and the status panel already counts them apart. Lead the
    //   log line with the word the message and the panel both use.
    private void LogOutcome(SyncRecord record)
    {
        var message = record.Message ?? string.Empty;

        if (message.StartsWith("Rejected:", StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected the sync for {Item}: {Reason}", record.ItemName, Reason(message));
            return;
        }

        _logger.LogWarning("Sync failed for {Item}: {Reason}", record.ItemName, Reason(message));
    }

    // ! The message without its leading action word, which the log line supplies itself. Only a
    //   single bare word counts; an engine dump carries colons of its own and is left whole.
    private static string Reason(string? message)
    {
        var text = message ?? string.Empty;
        var split = text.IndexOf(": ", StringComparison.Ordinal);

        return split > 0 && text.AsSpan(0, split).IndexOfAny(' ', '\r', '\n') < 0
            ? text[(split + 2)..]
            : text;
    }

    // Returns null so a stage can hand its failure straight back to the pipeline.
    private string? FailStage(SyncRecord record, string? message, SubtitleStageKind kind)
    {
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = null;
        record.AppliedOffsetMs = null;
        record.SkippedMovementMs = null;
        record.RefusedByAudio = kind == SubtitleStageKind.Verify;
        record.Message = string.IsNullOrWhiteSpace(message)
            ? "Failed: the OCR step did not complete."
            : message;
        _logger.LogWarning("{Kind} failed for {Item}: {Reason}", kind, record.ItemName, Reason(record.Message));
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
