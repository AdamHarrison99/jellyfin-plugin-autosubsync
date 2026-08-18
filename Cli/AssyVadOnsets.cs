using System.Text.Json;
using Jellyfin.Plugin.AutoSubSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Reads speech onsets out of the payload's voice detector.
public class AssyVadOnsets : ISpeechOnsetSource
{
    private readonly IAssyCliRunner _runner;
    private readonly ILogger<AssyVadOnsets> _logger;

    public AssyVadOnsets(IAssyCliRunner runner, ILogger<AssyVadOnsets> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<SpeechOnsets?> ReadAsync(
        string videoPath,
        IReadOnlyList<SyncVerifier.Window> windows,
        CancellationToken cancellationToken)
    {
        if (windows.Count == 0)
        {
            return null;
        }

        var planned = windows.Select(w => new VadWindow(w.StartMs, w.LengthMs)).ToList();
        var result = await _runner.VadAsync(videoPath, planned, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Voice detection failed for {Video} (exit {Exit}): {Error}",
                videoPath,
                result.ExitCode,
                result.StandardError);
            return null;
        }

        return Parse(result.StandardOutput);
    }

    internal static SpeechOnsets? Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!root.TryGetProperty("onsets", out var found) || found.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var onsets = new List<long>(found.GetArrayLength());
            foreach (var onset in found.EnumerateArray())
            {
                if (onset.TryGetInt64(out var at))
                {
                    onsets.Add(at);
                }
            }

            var windows = root.TryGetProperty("windowsRead", out var read) && read.TryGetInt32(out var count)
                ? count
                : 0;

            return onsets.Count == 0 || windows == 0 ? null : new SpeechOnsets(onsets, windows);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
