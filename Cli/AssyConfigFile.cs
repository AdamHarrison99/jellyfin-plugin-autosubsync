using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// The assy-cli config handed to every child process.
public class AssyConfigFile
{
    public const string FileName = "assy-config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<AssyConfigFile> _logger;
    private readonly object _gate = new();
    private string? _lastWritten;

    public AssyConfigFile(PluginPaths paths, ILogger<AssyConfigFile> logger)
    {
        _path = Path.Combine(paths.Home, FileName);
        _logger = logger;
    }

    public string FilePath => _path;

    // Returns null when the file could not be written; the caller must not spawn without it.
    public string? Ensure()
    {
        var content = Render();

        lock (_gate)
        {
            if (string.Equals(_lastWritten, content, StringComparison.Ordinal)
                && File.Exists(_path))
            {
                return _path;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, content);
                _lastWritten = content;
                return _path;
            }
            catch (Exception ex)
            {
                _lastWritten = null;
                _logger.LogError(ex, "Failed to write the assy-cli config at {Path}", _path);
                return null;
            }
        }
    }

    // Global options only, merged by assy-cli over its own defaults. The content never varies.
    internal static string Render()
    {
        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            // ! Pinned despite -o: the save mode is validated before -o is read.
            ["automatic_save_location"] = "save_next_to_input_subtitle",
            ["add_tool_prefix"] = false,
            ["custom_suffix"] = string.Empty,
            ["backup_subtitles_before_overwriting"] = false,
            ["keep_extracted_subtitles"] = false,
            ["keep_converted_subtitles"] = false,
            ["skip_previously_processed_videos"] = false,
            ["check_updates_startup"] = false,
            ["keep_log_records"] = false
        };

        return JsonSerializer.Serialize(options, SerializerOptions);
    }
}
