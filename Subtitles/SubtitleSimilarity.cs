using System.Text;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Content is what the cues say; formatting is how they are styled. A duplicate matches on both.
public readonly record struct SimilarityScore(double Content, double Formatting)
{
    public bool Matches(double threshold) => Content >= threshold && Formatting >= threshold;
}

// Everything scoring needs from one file, read once.
public sealed class SubtitleProfile
{
    public required string FormatKey { get; init; }

    public required int CueCount { get; init; }

    public required Dictionary<string, int> Bigrams { get; init; }

    public required Dictionary<string, int> Declarations { get; init; }

    public required Dictionary<string, int> Usage { get; init; }

    public static SubtitleProfile? Read(string path)
    {
        if (SubtitleContent.FormatKey(path) is not string key)
        {
            return null;
        }

        var cues = SubtitleContent.ReadCues(path).ToList();
        var formatting = SubtitleContent.ReadFormatting(path).ToList();

        return new SubtitleProfile
        {
            FormatKey = key,
            CueCount = cues.Count,
            Bigrams = SubtitleSimilarity.Tally(SubtitleSimilarity.Bigrams(cues)),
            Declarations = SubtitleSimilarity.Tally(formatting.Where(SubtitleSimilarity.IsDeclaration)),
            Usage = SubtitleSimilarity.Tally(formatting.Where(t => !SubtitleSimilarity.IsDeclaration(t)))
        };
    }
}

// Scores how much two subtitle files share, ignoring case, whitespace and cue boundaries.
public static class SubtitleSimilarity
{
    // Below this a match is coincidence: forced tracks are a handful of cues.
    public const int MinimumCues = 10;

    public static SimilarityScore Compare(string leftPath, string rightPath)
        => Compare(SubtitleProfile.Read(leftPath), SubtitleProfile.Read(rightPath));

    // ! A .srt and a .ass are never duplicates, whatever their cues say.
    public static SimilarityScore Compare(SubtitleProfile? left, SubtitleProfile? right)
    {
        if (left is null || right is null || !string.Equals(left.FormatKey, right.FormatKey, StringComparison.Ordinal))
        {
            return new SimilarityScore(0, 0);
        }

        var content = left.CueCount >= MinimumCues && right.CueCount >= MinimumCues
            ? Compare(left.Bigrams, right.Bigrams)
            : 0;

        return new SimilarityScore(content, CompareFormatting(left, right));
    }

    // ! The worse of the two, never a blend. One style definition is outvoted per-cue.
    private static double CompareFormatting(SubtitleProfile left, SubtitleProfile right)
        => Math.Min(
            Compare(left.Declarations, right.Declarations),
            Compare(left.Usage, right.Usage));

    internal static bool IsDeclaration(string token)
        => token.StartsWith("style=", StringComparison.Ordinal);

    // ! Pairs run across cue boundaries; a re-split cue has to score the same.
    internal static IEnumerable<string> Bigrams(List<string> cues)
    {
        var stream = cues
            .SelectMany(cue => NormalizeCue(cue).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        for (var i = 0; i + 1 < stream.Count; i++)
        {
            yield return stream[i] + ' ' + stream[i + 1];
        }
    }

    // Sørensen-Dice over token multisets.
    public static double Compare(Dictionary<string, int> left, Dictionary<string, int> right)
    {
        var leftTotal = left.Values.Sum();
        var rightTotal = right.Values.Sum();

        // Two files that style nothing are styled the same way.
        if (leftTotal == 0 && rightTotal == 0)
        {
            return 1;
        }

        if (leftTotal == 0 || rightTotal == 0)
        {
            return 0;
        }

        var shared = 0;
        foreach (var pair in left)
        {
            if (right.TryGetValue(pair.Key, out var count))
            {
                shared += Math.Min(pair.Value, count);
            }
        }

        return 2.0 * shared / (leftTotal + rightTotal);
    }

    internal static Dictionary<string, int> Tally(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                continue;
            }

            counts[value] = counts.TryGetValue(value, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    // ! Letters and digits only. Punctuation and line breaks are not content here.
    private static string NormalizeCue(string cue)
    {
        var builder = new StringBuilder(cue.Length);
        var depth = 0;
        var space = false;

        foreach (var c in cue)
        {
            if (c is '<' or '{')
            {
                depth++;
                continue;
            }

            if (c is '>' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }
}
