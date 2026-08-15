using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// What the engine did, split in two: a constant move, and a change of span from rate correction.
public readonly record struct OffsetChange(long? ConstantMs, long? DriftMs, double? RateRatio);

// Reads cue timings, and measures what moved between two versions of one subtitle.
public static partial class SubtitleOffsetProbe
{
    private const int MaxLinesScanned = 200;

    // Far past any real subtitle; the cap only stops a corrupt file exhausting memory.
    private const int MaxLinesRead = 200_000;

    // ! Too short to read a rate off. A few seconds of cues make any ratio noise.
    internal const long MinimumSpanMs = 60_000;

    // Below this the matched set says more about coincidence than about the retime.
    internal const int MinimumPairs = 8;

    // A key this short is rarely unique across one subtitle.
    private const int MinimumKeyLength = 8;

    internal readonly record struct Cue(long StartMs, long EndMs, string Key);

    public static long? TryGetFirstCueMs(string path)
    {
        try
        {
            var scanned = 0;

            foreach (var line in File.ReadLines(path))
            {
                if (++scanned > MaxLinesScanned)
                {
                    break;
                }

                var match = TimestampRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                return ParseGroups(match);
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    // ! No line cap here; the last cue is only findable by reading to the end.
    public static long? TryGetLastCueMs(string path)
    {
        try
        {
            long? last = null;

            foreach (var line in File.ReadLines(path))
            {
                var match = TimestampRegex().Match(line);
                if (match.Success)
                {
                    last = ParseGroups(match);
                }
            }

            return last;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // ! Cue identity, never cue position. The engine drops cues it cannot place.
    public static OffsetChange Measure(string inputPath, string outputPath)
    {
        var before = TryReadCues(inputPath);
        var after = TryReadCues(outputPath);

        if (before is null || after is null || before.Count == 0 || after.Count == 0)
        {
            return Endpoints(inputPath, outputPath);
        }

        var pairs = Match(before, after);
        if (pairs.Count < MinimumPairs)
        {
            return Endpoints(inputPath, outputPath);
        }

        var firstBefore = before[0].StartMs;
        var lastBefore = before[^1].StartMs;
        var matchedSpan = pairs[^1].Before - pairs[0].Before;

        if (matchedSpan < MinimumSpanMs)
        {
            var median = MedianDelta(pairs);
            return new OffsetChange(Math.Abs(median), null, null);
        }

        var line = Fit(pairs);
        var constant = (long)Math.Round(line.Intercept + (line.Slope * firstBefore));
        var ratio = 1 + line.Slope;
        var spanBefore = lastBefore - firstBefore;

        if (spanBefore < MinimumSpanMs)
        {
            return new OffsetChange(Math.Abs(constant), null, null);
        }

        var drift = (long)Math.Round(Math.Abs(line.Slope) * spanBefore);
        return new OffsetChange(Math.Abs(constant), drift, ratio);
    }

    // The pre-matching measurement, kept for subtitles whose cues cannot be paired.
    private static OffsetChange Endpoints(string inputPath, string outputPath)
    {
        var firstBefore = TryGetFirstCueMs(inputPath);
        var firstAfter = TryGetFirstCueMs(outputPath);

        var constant = firstBefore is null || firstAfter is null
            ? (long?)null
            : Math.Abs(firstAfter.Value - firstBefore.Value);

        var lastBefore = TryGetLastCueMs(inputPath);
        var lastAfter = TryGetLastCueMs(outputPath);

        if (firstBefore is null || firstAfter is null || lastBefore is null || lastAfter is null)
        {
            return new OffsetChange(constant, null, null);
        }

        var spanBefore = lastBefore.Value - firstBefore.Value;
        var spanAfter = lastAfter.Value - firstAfter.Value;

        if (spanBefore < MinimumSpanMs)
        {
            return new OffsetChange(constant, null, null);
        }

        return new OffsetChange(
            constant,
            Math.Abs(spanAfter - spanBefore),
            (double)spanAfter / spanBefore);
    }

    internal readonly record struct Pair(long Before, long After);

    // Keys occurring once on each side. A repeated line cannot say which cue it is.
    internal static List<Pair> Match(List<Cue> before, List<Cue> after)
    {
        var source = Unique(before);
        var result = Unique(after);
        var pairs = new List<Pair>();

        foreach (var entry in source)
        {
            if (result.TryGetValue(entry.Key, out var end))
            {
                pairs.Add(new Pair(entry.Value, end));
            }
        }

        pairs.Sort((a, b) => a.Before.CompareTo(b.Before));
        return pairs;
    }

    private static Dictionary<string, long> Unique(List<Cue> cues)
    {
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cue in cues)
        {
            if (cue.Key.Length < MinimumKeyLength)
            {
                continue;
            }

            if (!seen.TryAdd(cue.Key, cue.StartMs))
            {
                repeated.Add(cue.Key);
            }
        }

        foreach (var key in repeated)
        {
            seen.Remove(key);
        }

        return seen;
    }

    internal readonly record struct Line(double Slope, double Intercept);

    // Least squares over the deltas, refit once without the worst tenth of the residuals.
    internal static Line Fit(List<Pair> pairs)
    {
        var sample = pairs;
        var slope = 0d;
        var intercept = 0d;

        for (var pass = 0; pass < 2; pass++)
        {
            var n = sample.Count;
            var meanT = sample.Average(p => (double)p.Before);
            var meanD = sample.Average(p => (double)(p.After - p.Before));

            var numerator = 0d;
            var denominator = 0d;

            foreach (var pair in sample)
            {
                var t = pair.Before - meanT;
                numerator += t * ((pair.After - pair.Before) - meanD);
                denominator += t * t;
            }

            slope = denominator == 0 ? 0 : numerator / denominator;
            intercept = meanD - (slope * meanT);

            if (pass == 1 || n < MinimumPairs * 2)
            {
                break;
            }

            var residuals = sample
                .Select(p => Math.Abs((p.After - p.Before) - (intercept + (slope * p.Before))))
                .OrderBy(r => r)
                .ToList();

            var cutoff = Math.Max(250d, residuals[(int)(residuals.Count * 0.9)]);
            var trimmed = sample
                .Where(p => Math.Abs((p.After - p.Before) - (intercept + (slope * p.Before))) <= cutoff)
                .ToList();

            if (trimmed.Count < MinimumPairs)
            {
                break;
            }

            sample = trimmed;
        }

        return new Line(slope, intercept);
    }

    private static long MedianDelta(List<Pair> pairs)
    {
        var deltas = pairs.Select(p => p.After - p.Before).OrderBy(d => d).ToList();
        return deltas[deltas.Count / 2];
    }

    internal static List<Cue>? TryReadCues(string path)
    {
        try
        {
            // ! Bounded read. This is a file from the media tree, not something the plugin wrote.
            var lines = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                lines.Add(line);
                if (lines.Count >= MaxLinesRead)
                {
                    break;
                }
            }

            var cues = new List<Cue>();

            for (var i = 0; i < lines.Count; i++)
            {
                var stamps = TimestampRegex().Matches(lines[i]);
                if (stamps.Count == 0)
                {
                    continue;
                }

                var start = ParseGroups(stamps[0]);

                // A line with no second timing is a cue with no duration to speak of.
                var end = stamps.Count > 1 ? ParseGroups(stamps[1]) : start;

                cues.Add(new Cue(start, Math.Max(start, end), KeyFor(lines, i)));
            }

            return cues;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Text after the timings on the cue's own line, then the lines under it.
    private static string KeyFor(List<string> lines, int index)
    {
        var builder = new StringBuilder();
        var tail = lines[index];

        // ! Past the last timing on the line. Both timings change on a retime and are not identity.
        var stamps = TimestampRegex().Matches(tail);
        if (stamps.Count > 0)
        {
            var last = stamps[^1];
            tail = tail[(last.Index + last.Length)..];
        }

        Append(builder, tail);

        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line) || TimestampRegex().IsMatch(line))
            {
                break;
            }

            // A bare number ahead of the next cue's timings is its index, not this cue's text.
            if (IsIndexLine(lines, i))
            {
                break;
            }

            Append(builder, line);
        }

        return builder.ToString();
    }

    private static bool IsIndexLine(List<string> lines, int index)
    {
        if (!long.TryParse(lines[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        return index + 1 < lines.Count && TimestampRegex().IsMatch(lines[index + 1]);
    }

    private static void Append(StringBuilder builder, string text)
    {
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
    }

    private static long ParseGroups(Match match)
    {
        var hours = long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = long.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);

        // SRT/VTT use 3-digit milliseconds; ASS/SSA use 2-digit centiseconds.
        var fractionText = match.Groups["f"].Value;
        var fraction = long.Parse(fractionText, CultureInfo.InvariantCulture);
        var milliseconds = fractionText.Length == 2 ? fraction * 10 : fraction;

        return (((hours * 60) + minutes) * 60 + seconds) * 1000 + milliseconds;
    }

    // Matches SRT "00:01:23,456", VTT "00:01:23.456", and ASS "0:01:23.45".
    [GeneratedRegex(@"(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})[,.](?<f>\d{2,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();
}
