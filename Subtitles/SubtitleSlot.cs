namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// The identity of one output file: at most one subtitle per video, language, and flag set.
public readonly record struct SubtitleSlot(string Language, bool IsForced, bool IsHearingImpaired);

// Which source serves a slot when several could. Lower wins.
public enum SubtitleSourceRank
{
    ExternalText = 0,
    EmbeddedText = 1,
    ExternalImage = 2,
    EmbeddedImage = 3
}
