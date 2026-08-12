using System.Text;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Content is what the cues say; formatting is how they are styled. A duplicate matches on both.
public readonly record struct SimilarityScore(double Content, double Formatting)
{
    public bool Matches(double threshold) => Content >= threshold && Formatting >= threshold;
}

// Scores how much two subtitle files share, ignoring case, whitespace and cue boundaries.
public static class SubtitleSimilarity
{
    // Below this a match is coincidence: forced tracks are a handful of cues.
    public const int MinimumCues = 10;

    // ! A .srt and a .ass are never duplicates, whatever their cues say. One carries styling
    //   the other cannot express, so collapsing them loses it.
    public static SimilarityScore Compare(string leftPath, string rightPath)
    {
        if (!SubtitleContent.SameFormat(leftPath, rightPath))
        {
            return new SimilarityScore(0, 0);
        }

        var left = SubtitleContent.ReadCues(leftPath).ToList();
        var right = SubtitleContent.ReadCues(rightPath).ToList();

        var content = left.Count >= MinimumCues && right.Count >= MinimumCues
            ? Compare(Tally(Bigrams(left), Keep), Tally(Bigrams(right), Keep))
            : 0;

        return new SimilarityScore(
            content,
            CompareFormatting(
                SubtitleContent.ReadFormatting(leftPath).ToList(),
                SubtitleContent.ReadFormatting(rightPath).ToList()));
    }

    // ! Word pairs run across cue boundaries, so one cue re-split into two scores unchanged.
    //   Single words do not work here: two unrelated subtitles share most of a language.
    private static IEnumerable<string> Bigrams(List<string> cues)
    {
        var stream = cues
            .SelectMany(cue => NormalizeCue(cue).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        for (var i = 0; i + 1 < stream.Count; i++)
        {
            yield return stream[i] + ' ' + stream[i + 1];
        }
    }

    // ! The worse of the two, never a blend. One style definition against a cue token per cue
    //   is outvoted 180 to 1, and the definition is the part that carries the styling.
    private static double CompareFormatting(List<string> left, List<string> right)
    {
        var declarations = Compare(
            Tally(left.Where(IsDeclaration), Keep),
            Tally(right.Where(IsDeclaration), Keep));

        var usage = Compare(
            Tally(left.Where(t => !IsDeclaration(t)), Keep),
            Tally(right.Where(t => !IsDeclaration(t)), Keep));

        return Math.Min(declarations, usage);
    }

    private static bool IsDeclaration(string token)
        => token.StartsWith("style=", StringComparison.Ordinal);

    // Sørensen-Dice over token multisets. Order-insensitive, and a few extra tokens cost little.
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

    public static Dictionary<string, int> Tally(IEnumerable<string> values, Func<string, string> normalize)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            var key = normalize(value);
            if (key.Length == 0)
            {
                continue;
            }

            counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    private static string Keep(string value) => value;

    // ! Keeps letters and digits only. Punctuation and line breaks differ between rips of
    //   the same subtitle far more often than the words do. Styling is scored separately.
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
