using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Recognizes hearing-impaired annotations from a subtitle's text.
public static partial class SdhDetector
{
    // ! Stripping damages a non-SDH track. An uncertain verdict must be no.
    private const int MinimumMarkedCues = 5;
    private const double MinimumMarkedRatio = 0.02;

    public sealed record Result(int CueCount, int MarkedCueCount)
    {
        public double Ratio => CueCount == 0 ? 0d : (double)MarkedCueCount / CueCount;

        public bool IsHearingImpaired
            => MarkedCueCount >= MinimumMarkedCues && Ratio >= MinimumMarkedRatio;
    }

    public static Result Inspect(string path) => Analyze(SubtitleContent.ReadCues(path));

    public static Result Analyze(IEnumerable<string> cues)
    {
        var total = 0;
        var marked = 0;

        foreach (var cue in cues)
        {
            total++;
            if (IsMarked(cue))
            {
                marked++;
            }
        }

        return new Result(total, marked);
    }

    public static bool IsMarked(string cue)
    {
        var text = FormattingTag().Replace(cue, string.Empty);

        if (SoundEffect().IsMatch(text))
        {
            return true;
        }

        foreach (var line in text.Split('\n'))
        {
            if (SpeakerLabel().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"<[^>]{1,20}>", RegexOptions.None, 200)]
    private static partial Regex FormattingTag();

    // ! The lookahead demands a Latin letter. Arabic tracks parenthesize proper nouns.
    [GeneratedRegex(@"\[(?=[^\]\n]{0,78}[A-Za-z])[^\]\n]{2,80}\]|\((?=[^)\n]{0,78}[A-Za-z])[^)\n]{2,80}\)", RegexOptions.None, 200)]
    private static partial Regex SoundEffect();

    // ! Caps, plus 'l' for the OCR of 'I'. Wider lowercase matches every mid-sentence colon.
    [GeneratedRegex(@"^\s*(?:-|>>|-\s*>>)?\s*[A-Z][A-Z0-9l .'#\-]{1,24}:(?:\s|$)", RegexOptions.None, 200)]
    private static partial Regex SpeakerLabel();
}
