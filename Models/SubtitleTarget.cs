using System;

namespace Jellyfin.Plugin.AutoSubSync.Models;

// One unit of work: a single subtitle track on a single media item.
public class SubtitleTarget
{
    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string VideoPath { get; set; } = string.Empty;

    public SubtitleOrigin Origin { get; set; }

    // External targets only.
    public string? SubtitlePath { get; set; }

    // Embedded targets only: the absolute container stream index.
    public int? StreamIndex { get; set; }

    public string? Language { get; set; }

    public string? Codec { get; set; }

    public bool IsForced { get; set; }

    public bool IsHearingImpaired { get; set; }

    public string? Title { get; set; }

    // Set only when one item carries several tracks sharing this language and flag set.
    public string? Variant { get; set; }

    // Stable store key, unique within an item and stable across rescans.
    public string Key { get; set; } = string.Empty;

    // Set when the track was discovered but cannot be processed.
    public string? UnsupportedReason { get; set; }

    // Bitmap track needing OCR before any engine can read it.
    public bool RequiresOcr { get; set; }

    public static string ExternalKey(string videoPath, string subtitlePath)
    {
        var dir = System.IO.Path.GetDirectoryName(videoPath) ?? string.Empty;
        var relative = System.IO.Path.GetRelativePath(dir, subtitlePath);
        return "ext:" + relative.Replace('\\', '/');
    }

    public static string EmbeddedKey(int streamIndex, string? codec)
        => $"emb:{streamIndex}:{codec ?? "unknown"}";
}
