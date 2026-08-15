using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// What the engine says about the alignment it chose, read off its own diagnostics.
public partial class EngineAlignment
{
    public double? Score { get; init; }

    public double? OffsetSeconds { get; init; }

    public double? RateFactor { get; init; }

    public static EngineAlignment? From(string diagnostics)
    {
        if (string.IsNullOrEmpty(diagnostics))
        {
            return null;
        }

        var score = Number(ScoreRegex(), diagnostics);
        var offset = Number(OffsetRegex(), diagnostics);
        var rate = Number(RateRegex(), diagnostics);

        return score is null && offset is null && rate is null
            ? null
            : new EngineAlignment { Score = score, OffsetSeconds = offset, RateFactor = rate };
    }

    // Per second of subtitle actually on screen, so two titles are comparable at all.
    public double? PerShownSecond(double shownSeconds)
        => Score is { } score && shownSeconds > 0 ? score / shownSeconds : null;

    private static double? Number(Regex pattern, string text)
    {
        // ! The last one. A retried or multi-pass run prints more than once.
        var matches = pattern.Matches(text);

        return matches.Count > 0
            && double.TryParse(
                matches[^1].Groups["v"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"score:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"offset seconds:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"framerate scale factor:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RateRegex();
}
