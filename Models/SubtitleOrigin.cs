using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoSubSync.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleOrigin
{
    External = 0,
    Embedded = 1
}
