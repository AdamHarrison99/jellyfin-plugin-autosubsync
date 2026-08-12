namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Subtitle formats each assy-cli engine accepts, from upstream main/constants.py: SYNC_TOOLS.
// ! Re-check on every assy-cli pin bump.
public static class SyncToolCapabilities
{
    private static readonly Dictionary<string, string[]> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ffsubsync"] = [".srt", ".ass", ".ssa", ".vtt"],
        ["alass"] = [".srt", ".ass", ".ssa", ".sub", ".idx"],
        ["autosubsync"] = [".srt"]
    };

    public static bool Supports(string tool, string extension)
        => Formats.TryGetValue(tool, out var supported)
           && supported.Contains(extension, StringComparer.OrdinalIgnoreCase);

    // Configured engines that accept the format, in the configured order.
    public static IReadOnlyList<string> SelectChain(IEnumerable<string> toolChain, string extension)
        => toolChain.Where(t => Supports(t, extension)).ToList();
}
