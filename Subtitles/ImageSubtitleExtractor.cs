using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Lifts an embedded bitmap track into a file the OCR tool will read.
public class ImageSubtitleExtractor
{
    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub"
    };

    // ! seconv reads a bitmap MKV but produces an empty file for DVB. ffmpeg re-encodes it
    //   bitmap-to-bitmap, which costs nothing and is the only reason DVB works at all.
    private static readonly HashSet<string> NeedsTranscode = new(StringComparer.OrdinalIgnoreCase)
    {
        "dvb_subtitle", "dvbsub"
    };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ImageSubtitleExtractor> _logger;

    public ImageSubtitleExtractor(
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths,
        ILogger<ImageSubtitleExtractor> logger)
    {
        _mediaEncoder = mediaEncoder;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public static bool IsImageCodec(string? codec)
        => !string.IsNullOrWhiteSpace(codec) && ImageCodecs.Contains(codec);

    public async Task<string?> ExtractAsync(
        string videoPath,
        int streamIndex,
        string? codec,
        CancellationToken cancellationToken)
    {
        if (!IsImageCodec(codec))
        {
            _logger.LogDebug("Refusing to extract non-image subtitle codec {Codec}", codec);
            return null;
        }

        var workDir = Path.Combine(_applicationPaths.TempDirectory, "AutoSubSync");
        Directory.CreateDirectory(workDir);

        // A single-track container needs no track selection.
        var outputPath = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".mkv");
        var subtitleCodec = NeedsTranscode.Contains(codec!) ? "dvdsub" : "copy";

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
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:" + streamIndex.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-c:s");
        startInfo.ArgumentList.Add(subtitleCodec);
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
            _logger.LogError(ex, "Bitmap extraction threw for {Path}", videoPath);
            TryDelete(outputPath);
            return null;
        }

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg failed to extract bitmap stream {Index} from {Path}: {Error}",
                streamIndex,
                videoPath,
                outcome.StandardError);
            TryDelete(outputPath);
            return null;
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            _logger.LogWarning(
                "Bitmap extraction produced nothing for stream {Index} of {Path}",
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
