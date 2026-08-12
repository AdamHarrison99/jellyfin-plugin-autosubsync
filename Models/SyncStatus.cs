using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncStatus
{
    Pending = 0,
    DryRun = 1,
    Synced = 2,
    Failed = 3,

    // Processed, but the result was discarded as a no-op.
    Skipped = 4,

    // The plugin cannot process this track at all.
    Unsupported = 5
}
