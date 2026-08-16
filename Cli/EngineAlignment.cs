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

        // ! Best, not last: a framerate search prints one score per candidate, and the gate only
        //   refuses, so reading low is what costs a good sync.
        var score = Numbers(ScoreRegex(), diagnostics) is { Count: > 0 } scores
            ? scores.Max()
            : (double?)null;
        var offset = Last(OffsetRegex(), diagnostics);
        var rate = Last(RateRegex(), diagnostics);

        return score is null && offset is null && rate is null
            ? null
            : new EngineAlignment { Score = score, OffsetSeconds = offset, RateFactor = rate };
    }

    // Per second of subtitle actually on screen, so two titles are comparable at all.
    public double? PerShownSecond(double shownSeconds)
        => Score is { } score && shownSeconds > 0 ? score / shownSeconds : null;

    private static List<double> Numbers(Regex pattern, string text)
    {
        var values = new List<double>();

        foreach (Match match in pattern.Matches(text))
        {
            if (double.TryParse(
                    match.Groups["v"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    // The applied figure, which is whatever the engine printed last.
    private static double? Last(Regex pattern, string text)
        => Numbers(pattern, text) is { Count: > 0 } values ? values[^1] : null;

    [GeneratedRegex(@"score:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"offset seconds:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"framerate scale factor:\s*(?<v>-?[\d.]+)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex RateRegex();
}
