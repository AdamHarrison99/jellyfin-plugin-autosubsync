using Jellyfin.Plugin.AutoSubSync.Configuration;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Pure argv construction for assy-cli.
public static class AssyArgumentBuilder
{
    public readonly record struct Invocation(string FileName, IReadOnlyList<string> Arguments);

    public static Invocation BuildSync(
        PluginConfiguration config,
        string executablePath,
        string videoPath,
        string subtitlePath,
        string outputPath,
        string tool)
    {
        var args = new List<string>();
        AppendGlobalOptions(config, args);

        args.Add("sync");
        args.Add(videoPath);
        args.Add(subtitlePath);
        args.Add("-o");
        args.Add(outputPath);
        args.Add("-t");
        args.Add(tool);
        args.Add("--json");

        if (!string.IsNullOrWhiteSpace(config.OutputEncoding))
        {
            args.Add("--encoding");
            args.Add(config.OutputEncoding);
        }

        // The plugin sets the output filename.
        args.Add("--no-prefix");

        return new Invocation(executablePath, args);
    }

    public static Invocation BuildShift(
        PluginConfiguration config,
        string executablePath,
        string subtitlePath,
        int milliseconds,
        string outputPath)
    {
        var args = new List<string>();
        AppendGlobalOptions(config, args);

        args.Add("shift");
        args.Add(subtitlePath);
        args.Add(milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-o");
        args.Add(outputPath);
        args.Add("--json");

        return new Invocation(executablePath, args);
    }

    private static void AppendGlobalOptions(PluginConfiguration config, List<string> args)
    {
        // ! Keep --no-color: stderr is parsed.
        args.Add("--no-color");

        if (!string.IsNullOrWhiteSpace(config.AssyConfigFilePath))
        {
            args.Add("--config-file");
            args.Add(config.AssyConfigFilePath);
        }
    }
}
