using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Reads the first cue timestamp. Approximate.
public static partial class SubtitleOffsetProbe
{
    private const int MaxLinesScanned = 200;

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
