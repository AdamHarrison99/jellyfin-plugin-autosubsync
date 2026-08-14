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
}
