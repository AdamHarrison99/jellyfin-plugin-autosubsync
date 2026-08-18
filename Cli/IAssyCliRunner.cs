using Jellyfin.Plugin.AutoSubSync.Models;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

public class AssyInvocationResult
{
    // 0 ok, 1 sync failed, 2 usage, 130 interrupted.
    public int ExitCode { get; set; }

    public AssyResult? Result { get; set; }

    public string StandardError { get; set; } = string.Empty;

    public string StandardOutput { get; set; } = string.Empty;

    public bool TimedOut { get; set; }

    // Null when the engine printed no diagnostics to read.
    public EngineAlignment? Alignment { get; set; }

    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

// One window of audio to read, in milliseconds from the start of the video.
public readonly record struct VadWindow(long StartMs, long LengthMs);

// The single seam between the plugin and assy-cli.
public interface IAssyCliRunner
{
    Task<AssyInvocationResult> SyncAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        CancellationToken cancellationToken);

    // Speech onsets from the voice detector inside the payload. JSON arrives on standard output.
    Task<AssyInvocationResult> VadAsync(
        string videoPath,
        IReadOnlyList<VadWindow> windows,
        CancellationToken cancellationToken);

    Task<AssyInvocationResult> ShiftAsync(
        string subtitlePath,
        int milliseconds,
        string outputPath,
        CancellationToken cancellationToken);
}
