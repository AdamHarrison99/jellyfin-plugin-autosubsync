using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Extracts embedded tracks with the ffmpeg Jellyfin already ships.
public class FfmpegSubtitleExtractor : ISubtitleExtractor
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "microdvd", "subviewer"
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<FfmpegSubtitleExtractor> _logger;

    public FfmpegSubtitleExtractor(
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths,
        ILogger<FfmpegSubtitleExtractor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public bool IsExtractableCodec(string? codec)
        => !string.IsNullOrWhiteSpace(codec) && TextCodecs.Contains(codec);

    public async Task<string?> ExtractAsync(
        string videoPath,
        int streamIndex,
        string? codec,
        CancellationToken cancellationToken)
    {
        if (!IsExtractableCodec(codec))
        {
            _logger.LogDebug("Refusing to extract non-text subtitle codec {Codec}", codec);
            return null;
        }

        var workDir = Path.Combine(_applicationPaths.TempDirectory, "AutoSubSync");
        Directory.CreateDirectory(workDir);

        // ASS/SSA keep their styling; everything else is normalized to SRT.
        var isAdvanced = string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase);
        var extension = isAdvanced ? ".ass" : ".srt";
        var outputPath = Path.Combine(workDir, Guid.NewGuid().ToString("N") + extension);

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);

        // MediaStream.Index is the absolute container index.
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:" + streamIndex.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c:s");
        startInfo.ArgumentList.Add(isAdvanced ? "copy" : "srt");
        startInfo.ArgumentList.Add(outputPath);

        FfmpegOutcome outcome;

        try
        {
            outcome = await FfmpegProcess.RunAsync(startInfo, _logger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ffmpeg extraction threw for {Path}", videoPath);
            TryDelete(outputPath);
            return null;
        }

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg failed to extract stream {Index} from {Path}: {Error}",
                streamIndex,
                videoPath,
                outcome.StandardError);
            TryDelete(outputPath);
            return null;
        }

        // ! ffmpeg exits 0 on an empty track, and an empty ASS still carries its headers.
        if (!File.Exists(outputPath) || !SubtitleContent.HasCues(outputPath))
        {
            _logger.LogWarning(
                "Stream {Index} of {Path} extracted with no subtitle cues",
                streamIndex,
                videoPath);
            TryDelete(outputPath);
            return null;
        }

        return outputPath;
    }

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
            _logger.LogDebug(ex, "Failed to clean up temp file {Path}", path);
        }
    }
}
