using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Spawns the OCR and text-rewrite tool, and judges it by what it wrote.
public class SeConvRunner : ISeConvRunner
{
    private const int BoundedSlackChars = 64 * 1024;

    private const int StandardErrorKeepChars = 512 * 1024;

    private const int StandardErrorTailChars = 4000;
    private const string OutputFormat = "subrip";

    private readonly SeConvRuntime _runtime;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SeConvRunner> _logger;

    public SeConvRunner(
        SeConvRuntime runtime,
        IMediaEncoder mediaEncoder,
        ILogger<SeConvRunner> logger)
    {
        _runtime = runtime;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public async Task<ToolUnavailable?> EnsureOcrReadyAsync(CancellationToken cancellationToken)
    {
        var status = await _runtime.EnsureOcrReadyAsync(cancellationToken).ConfigureAwait(false);
        return Describe(status);
    }

    public async Task<ToolUnavailable?> EnsureConverterReadyAsync(CancellationToken cancellationToken)
    {
        var status = await _runtime.EnsureConverterReadyAsync(cancellationToken).ConfigureAwait(false);
        return Describe(status);
    }

    // ! A download in flight is the one reason worth coming back for.
    private static ToolUnavailable? Describe(SeConvStatus status)
        => status.IsReady
            ? null
            : new ToolUnavailable(status.Message, status.Readiness == PayloadReadiness.Fetching);

    public Task<SeConvResult> OcrAsync(
        string inputPath,
        string outputPath,
        string? language,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            inputPath,
            OutputFormat,
            "--ocr-engine:tesseract",
            "--fix-common-errors",
            "--outputfilename",
            outputPath
        };

        // tessdata names match ISO 639-2/T, which is what the allowlist already normalizes to.
        if (LanguageCodes.Normalize(language) is { } code)
        {
            arguments.Insert(3, "--ocr-language:" + code);
        }

        return RunAsync(arguments, outputPath, _runtime.GetOcrStatus(), cancellationToken);
    }

    public Task<SeConvResult> RemoveHearingImpairedAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            inputPath,
            OutputFormat,
            "--remove-text-for-hi",
            "--outputfilename",
            outputPath
        };

        // ! Text in, text out. Requiring Tesseract here would strand the strip on an OCR-less server.
        return RunAsync(arguments, outputPath, _runtime.GetConverterStatus(), cancellationToken);
    }

    private async Task<SeConvResult> RunAsync(
        List<string> arguments,
        string outputPath,
        SeConvStatus status,
        CancellationToken cancellationToken)
    {
        if (!status.IsReady || status.SeConvPath is not { } exe)
        {
            return new SeConvResult(null, status.Message, 0);
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo, status.TesseractDirectory);

        _logger.LogDebug("Running {File} {Args}", exe, string.Join(' ', arguments));

        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendBounded(stderr, e.Data, StandardErrorKeepChars);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {File}", exe);
            return new SeConvResult(null, ex.Message, stopwatch.ElapsedMilliseconds);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.PerSyncTimeoutMinutes > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(config.PerSyncTimeoutMinutes));
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested;
            KillProcessTree(process);
            TryDelete(outputPath);

            if (!timedOut)
            {
                throw;
            }

            return new SeConvResult(
                null,
                "Failed: the OCR tool timed out.",
                stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();

        // ! Exit code 0 is worthless here. A missing engine, an unreadable track and a format it
        //   cannot decode all report success and write nothing.
        if (!File.Exists(outputPath) || !SubtitleContent.HasCues(outputPath))
        {
            TryDelete(outputPath);
            var message = Tail(stderr.ToString());
            return new SeConvResult(
                null,
                string.IsNullOrWhiteSpace(message)
                    ? "Failed: the OCR tool produced no subtitle cues."
                    : message,
                stopwatch.ElapsedMilliseconds);
        }

        return new SeConvResult(outputPath, null, stopwatch.ElapsedMilliseconds);
    }

    private static readonly string[] PassThroughVariables =
    [
        "HOME", "TMPDIR", "TMP", "TEMP",
        "LANG", "LC_ALL", "LC_CTYPE",
        // ! Tesseract finds its language data here. Dropping it strands a relocated tessdata.
        "TESSDATA_PREFIX",
        "SystemRoot", "windir", "COMSPEC", "PATHEXT", "NUMBER_OF_PROCESSORS", "USERPROFILE"
    ];

    // Allowlisted, not inherited.
    private void ApplyEnvironment(ProcessStartInfo startInfo, string? tesseractDirectory)
    {
        startInfo.Environment.Clear();

        foreach (var name in PassThroughVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        var parts = new List<string>();

        if (!string.IsNullOrEmpty(tesseractDirectory))
        {
            parts.Add(tesseractDirectory);
        }

        if (Path.GetDirectoryName(_mediaEncoder.EncoderPath) is { Length: > 0 } encoderDir)
        {
            parts.Add(encoderDir);
        }

        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } systemPath)
        {
            parts.Add(systemPath);
        }

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, parts);
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill the OCR process tree");
        }
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
            _logger.LogDebug(ex, "Failed to clean up {Path}", path);
        }
    }

    // ! Trimmed as it grows, ¬only at the end. A chatty child holds its whole output in
    //   memory until its timeout fires; the slack keeps the trim amortized.
    private static void AppendBounded(StringBuilder builder, string line, int keep)
    {
        builder.AppendLine(line);

        if (builder.Length > keep + BoundedSlackChars)
        {
            builder.Remove(0, builder.Length - keep);
        }
    }

    private static string Tail(string value)
        => value.Length <= StandardErrorTailChars ? value : value[^StandardErrorTailChars..];
}
