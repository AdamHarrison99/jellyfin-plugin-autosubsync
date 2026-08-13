using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync;

// Owns the plugin's on-disk home, and the one-time move of it out of the Jellyfin plugin tree.
public class PluginPaths
{
    // ! Never under PluginConfigurationsPath. Jellyfin enumerates every directory in plugins/,
    //   globs *.dll through it, and deletes the ones that fail to load.
    public PluginPaths(IApplicationPaths applicationPaths, ILogger<PluginPaths> logger)
    {
        Home = Path.Combine(applicationPaths.DataPath, "AutoSubSync");

        var configurations = applicationPaths.PluginConfigurationsPath;
        Migrate(Path.Combine(configurations, "AutoSubSync"), Home, logger);

        // ! Jellyfin writes this once it decides the folder is a malfunctioning plugin, and reads
        //   it back on the next start to justify deleting the folder. Moving the data is not enough.
        RemoveStrandedManifest(Path.Combine(configurations, "meta.json"), logger);
    }

    public string Home { get; }

    // ! A move, never a copy-then-delete; the vault and the record store are in here.
    private static void Migrate(string legacy, string home, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(legacy))
            {
                return;
            }

            if (Directory.Exists(home))
            {
                logger.LogWarning(
                    "Both {Legacy} and {Home} exist; leaving the older copy alone", legacy, home);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(home)!);
            Directory.Move(legacy, home);
            logger.LogInformation("Moved the plugin data from {Legacy} to {Home}", legacy, home);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to move the plugin data out of {Legacy}", legacy);
        }
    }

    private static void RemoveStrandedManifest(string manifestPath, ILogger logger)
    {
        try
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
                logger.LogInformation("Removed the plugin manifest at {Path}", manifestPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove the plugin manifest at {Path}", manifestPath);
        }
    }
}
