namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

public interface ISubtitleExtractor
{
    // False for image-based codecs.
    bool IsExtractableCodec(string? codec);

    // Returns a temp file path the caller owns and must delete, or null on failure.
    Task<string?> ExtractAsync(
        string videoPath,
        int streamIndex,
        string? codec,
        CancellationToken cancellationToken);
}
