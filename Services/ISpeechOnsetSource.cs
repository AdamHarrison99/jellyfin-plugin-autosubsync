namespace Jellyfin.Plugin.AutoSubSync.Services;

// Speech onsets read by a voice detector, and the windows they were read from.
public readonly record struct SpeechOnsets(IReadOnlyList<long> Onsets, int Windows);

// The audio check's second reading of the same windows, from a detector trained on speech.
public interface ISpeechOnsetSource
{
    Task<SpeechOnsets?> ReadAsync(
        string videoPath,
        IReadOnlyList<SyncVerifier.Window> windows,
        CancellationToken cancellationToken);
}
