using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Reads a hearing-impaired marker out of the name a provider offers a subtitle under.
public static partial class SdhNaming
{
    public static bool IsHearingImpaired(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Cleaned().IsMatch(name))
        {
            return false;
        }

        return Sdh().IsMatch(name) || Hi().IsMatch(name);
    }

    // ! The token says the annotations are gone. That names the best candidate on the list.
    [GeneratedRegex(
        @"(?:no|non|without)[^A-Za-z0-9]{0,3}(?:sdh|hi)\b|\b(?:sdh|hi)[^A-Za-z0-9]{0,3}(?:removed|stripped)",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex Cleaned();

    // No English word spells it, so any delimiter is safe.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:sdh|hearing[^A-Za-z0-9]{0,3}impaired)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex Sdh();

    // ! Upper case and punctuation on one side. Addic7ed puts the episode title in this field,
    //   and a lower-case "Hi" between spaces is a word.
    [GeneratedRegex(
        @"(?<=[^A-Za-z0-9\s])HI(?![A-Za-z0-9])|(?<![A-Za-z0-9])HI(?=[^A-Za-z0-9\s])",
        RegexOptions.None, 200)]
    private static partial Regex Hi();
}
