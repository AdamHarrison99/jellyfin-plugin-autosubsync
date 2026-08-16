namespace Jellyfin.Plugin.AutoSubSync.Cli;

public record SeConvResult(string? OutputPath, string? Message, long ElapsedMs)
{
    public bool Succeeded => OutputPath is not null;
}

// Why a tool cannot run. Transient means it is downloading and the target is worth retrying.
public readonly record struct ToolUnavailable(string Message, bool IsTransient);

public interface ISeConvRunner
{
    // Both return null once usable, else what is missing.
    Task<ToolUnavailable?> EnsureOcrReadyAsync(CancellationToken cancellationToken);

    Task<ToolUnavailable?> EnsureConverterReadyAsync(CancellationToken cancellationToken);

    Task<SeConvResult> OcrAsync(
        string inputPath,
        string outputPath,
        string? language,
        string? codec,
        CancellationToken cancellationToken);

    Task<SeConvResult> RemoveHearingImpairedAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken);
}
