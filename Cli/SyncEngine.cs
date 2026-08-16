namespace Jellyfin.Plugin.AutoSubSync.Cli;

// The alignment engine the plugin runs, and the subtitle formats it reads.
public static class SyncEngine
{
    public const string Name = "ffsubsync";

    // From upstream main/constants.py: SYNC_TOOLS["ffsubsync"].
    // ! Re-check on every assy-cli pin bump.
    private static readonly string[] Formats = [".srt", ".ass", ".ssa", ".vtt"];

    public static bool Supports(string extension)
        => Formats.Contains(extension, StringComparer.OrdinalIgnoreCase);

    // ! A ".sub" reaching here carries no ".idx"; the pair is the only thing naming it VobSub.
    public static string UnsupportedReason(string extension)
        => string.Equals(extension, ".sub", StringComparison.OrdinalIgnoreCase)
            ? "Unsupported: this .sub has no .idx beside it, so it cannot be identified as VobSub."
            : $"Unsupported: the sync engine does not read {extension} subtitles.";
}
