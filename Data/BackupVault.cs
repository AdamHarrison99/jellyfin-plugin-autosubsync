using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Data;

// Holds pre-overwrite copies of user subtitles, outside the media folders.
public class BackupVault
{
    private readonly string _root;
    private readonly ILogger<BackupVault> _logger;

    public BackupVault(PluginPaths paths, ILogger<BackupVault> logger)
    {
        _logger = logger;
        _root = Path.Combine(paths.Home, "backups");
    }

    public string Root => _root;

    // ! Never place a backup beside the media; Jellyfin indexes sidecars by filename.
    // ! Unlabelled, a second copy collides with the pre-overwrite original and is dropped.
    public string? Store(Guid recordId, string originalPath, string? label = null)
    {
        try
        {
            if (!File.Exists(originalPath))
            {
                return null;
            }

            var directory = Path.Combine(_root, recordId.ToString("N"));
            Directory.CreateDirectory(directory);

            var name = label is null
                ? Path.GetFileName(originalPath)
                : label + "-" + Path.GetFileName(originalPath);

            var destination = Path.Combine(directory, name);

            if (File.Exists(destination))
            {
                return destination;
            }

            File.Copy(originalPath, destination);
            return destination;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to back up {Path}", originalPath);
            return null;
        }
    }

    public bool Restore(string backupPath, string originalPath)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                _logger.LogWarning("Backup missing, cannot restore {Path}", originalPath);
                return false;
            }

            var directory = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(backupPath, originalPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore {Path}", originalPath);
            return false;
        }
    }

    public void Discard(Guid recordId)
    {
        try
        {
            var directory = Path.Combine(_root, recordId.ToString("N"));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discard backup for record {RecordId}", recordId);
        }
    }

    public long GetTotalBytes()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }

            return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to measure the backup vault");
            return 0;
        }
    }
}
