using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Names the payload build this machine can execute.
public static class PlatformRid
{
    public static string? Current { get; } = Detect();

    public static string Describe()
        => Current ?? $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}";

    private static string? Detect()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        if (arch is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return "win-" + arch;
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux-" + arch;
        }

        return OperatingSystem.IsMacOS() ? "osx-" + arch : null;
    }
}
