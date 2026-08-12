using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// Decides whether rollback restores a backup or deletes the file.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleProvenance
{
    // A user file realigned in place. Rollback restores BackupPath.
    Retimed = 0,

    // A file the plugin created. Rollback deletes it; there is no original.
    Created = 1,

    // A duplicate the plugin removed after backing it up. Rollback restores BackupPath.
    Superseded = 2
}
