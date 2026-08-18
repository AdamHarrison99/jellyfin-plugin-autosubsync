using Jellyfin.Plugin.AutoSubSync.Configuration;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Pure argv construction for assy-cli.
public static class AssyArgumentBuilder
{
    public readonly record struct Invocation(string FileName, IReadOnlyList<string> Arguments);

    public static Invocation BuildSync(
        PluginConfiguration config,
        string executablePath,
        string configFilePath,
        string videoPath,
        string subtitlePath,
        string outputPath)
    {
        var args = new List<string>();
        AppendGlobalOptions(configFilePath, args);

        args.Add("sync");
        args.Add(videoPath);
        args.Add(subtitlePath);
        args.Add("-o");
        args.Add(outputPath);

        // ! Always named. An unnamed engine lets assy-cli pick one of the other two.
        args.Add("-t");
        args.Add(SyncEngine.Name);
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

    // ! The subcommand comes first and no global option precedes it. The payload dispatches on
    //   the first argument, and anything ahead of it hands the call to the upstream parser.
    public static Invocation BuildVad(
        string executablePath,
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<VadWindow> windows)
    {
        var args = new List<string>();

        args.Add("vad");
        args.Add(videoPath);

        // ! Named, never inherited from PATH. The engine's own ffmpeg is not the one to read with.
        args.Add("--ffmpeg");
        args.Add(ffmpegPath);

        foreach (var window in windows)
        {
            args.Add("--window");
            args.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{window.StartMs}:{window.LengthMs}"));
        }

        args.Add("--json");

        return new Invocation(executablePath, args);
    }

    public static Invocation BuildShift(
        string executablePath,
        string configFilePath,
        string subtitlePath,
        int milliseconds,
        string outputPath)
    {
        var args = new List<string>();
        AppendGlobalOptions(configFilePath, args);

        args.Add("shift");
        args.Add(subtitlePath);
        args.Add(milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-o");
        args.Add(outputPath);
        args.Add("--json");

        return new Invocation(executablePath, args);
    }

    private static void AppendGlobalOptions(string configFilePath, List<string> args)
    {
        // ! Keep --no-color: stderr is parsed.
        args.Add("--no-color");

        // ! Always passed. Omitting it makes assy-cli read the desktop app's own config from the
        //   user config directory, so engine behaviour would depend on state the plugin cannot see.
        args.Add("--config-file");
        args.Add(configFilePath);
    }
}
