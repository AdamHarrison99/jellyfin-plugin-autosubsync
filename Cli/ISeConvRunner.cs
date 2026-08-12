namespace Jellyfin.Plugin.AutoSubSync.Cli;

public record SeConvResult(string? OutputPath, string? Message, long ElapsedMs)
{
    public bool Succeeded => OutputPath is not null;
}

public interface ISeConvRunner
{
    // Null once the toolchain is usable, else what is missing.
    Task<string?> EnsureReadyAsync(CancellationToken cancellationToken);

    Task<SeConvResult> OcrAsync(
        string inputPath,
        string outputPath,
        string? language,
        CancellationToken cancellationToken);

    Task<SeConvResult> RemoveHearingImpairedAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken);
}
