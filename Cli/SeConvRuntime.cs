using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

public record OcrRuntimeStatus(bool IsReady, string? SeConvPath, string? TesseractDirectory, string Message);

// Resolves the OCR toolchain: the pinned converter, and the Tesseract the admin installed.
public class SeConvRuntime : PayloadRuntime
{
    private const string InstallDocsUrl = "https://tesseract-ocr.github.io/tessdoc/Installation.html";

    // ! No configuration setting resolves these. A settable path is arbitrary code execution.
    private static readonly string[] TesseractProbePaths =
    [
        @"C:\Program Files\Tesseract-OCR",
        @"C:\Program Files (x86)\Tesseract-OCR",
        "/usr/bin",
        "/usr/local/bin",
        "/snap/bin",
        "/opt/homebrew/bin"
    ];

    private readonly ILogger<SeConvRuntime> _logger;
    private readonly Lock _reportGate = new();

    private bool _reportedTesseract;

    public SeConvRuntime(PayloadStore store, PayloadFetcher fetcher, ILogger<SeConvRuntime> logger)
        : base(PayloadManifest.Seconv, store, fetcher, logger)
    {
        _logger = logger;
    }

    public static string TesseractExecutableName
        => OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";

    public OcrRuntimeStatus GetOcrStatus()
    {
        var payload = GetStatus();
        if (!payload.IsReady || payload.ExecutablePath is not { } seconv)
        {
            return new OcrRuntimeStatus(false, null, null, payload.Message);
        }

        var tesseract = ResolveTesseractDirectory();
        if (tesseract is null)
        {
            ReportMissingTesseract();
            return new OcrRuntimeStatus(
                false,
                seconv,
                null,
                $"Tesseract is not installed on this server, and OCR cannot run without it. Install it, then restart Jellyfin: {InstallDocsUrl}");
        }

        return new OcrRuntimeStatus(true, seconv, tesseract, "OCR is ready.");
    }

    public async Task<OcrRuntimeStatus> EnsureOcrReadyAsync(CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return GetOcrStatus();
    }

    // Tesseract has no official Windows build, so it is the admin's install, never ours.
    internal static string? ResolveTesseractDirectory()
    {
        foreach (var directory in EnumerateSearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                if (File.Exists(Path.Combine(directory, TesseractExecutableName)))
                {
                    return directory;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return entry.Trim();
            }
        }

        foreach (var probe in TesseractProbePaths)
        {
            yield return probe;
        }
    }

    private static IEnumerable<string> PlatformProbePaths
        => TesseractProbePaths.Where(p => p.StartsWith('/') != OperatingSystem.IsWindows());

    private void ReportMissingTesseract()
    {
        lock (_reportGate)
        {
            if (_reportedTesseract)
            {
                return;
            }

            _reportedTesseract = true;
            _logger.LogError(
                "\"Convert image-based subtitles to text\" is turned on, but Tesseract is not installed on this "
                + "server, and OCR cannot run without it. Install Tesseract and the language data for the "
                + "subtitles you want read, then restart Jellyfin. Installation instructions: {InstallDocs}. "
                + "Looked for '{Executable}' on PATH and in: {Locations}. "
                + "Until then image subtitles are reported as unsupported and everything else keeps working.",
                InstallDocsUrl,
                TesseractExecutableName,
                string.Join(", ", PlatformProbePaths));
        }
    }
}
