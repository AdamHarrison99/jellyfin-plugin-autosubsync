namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Pulls cue text out of a subtitle file without a full parse.
public static class SubtitleContent
{
    private const int MaxLinesScanned = 4000;

    private static readonly string[] VttBlockHeaders = { "WEBVTT", "NOTE", "STYLE", "REGION" };

    private enum SubtitleFormat
    {
        Unknown,
        Advanced,
        Block,
        MicroDvd
    }

    // ! Unknown formats return true. Never assert emptiness blind.
    public static bool HasCues(string path)
    {
        var format = FormatOf(path);

        try
        {
            var scanned = 0;

            foreach (var line in File.ReadLines(path))
            {
                if (++scanned > MaxLinesScanned)
                {
                    return true;
                }

                if (IsCueMarker(line, format))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return format == SubtitleFormat.Unknown;
    }

    // One element per cue.
    public static IEnumerable<string> ReadCues(string path)
        => FormatOf(path) switch
        {
            SubtitleFormat.Advanced => ReadAdvancedCues(path),
            SubtitleFormat.Block => ReadBlockCues(path),
            SubtitleFormat.MicroDvd => ReadMicroDvdCues(path),
            _ => Enumerable.Empty<string>()
        };

    private static SubtitleFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ass" or ".ssa" => SubtitleFormat.Advanced,
        ".srt" or ".vtt" => SubtitleFormat.Block,
        ".sub" => SubtitleFormat.MicroDvd,
        _ => SubtitleFormat.Unknown
    };

    private static bool IsCueMarker(string line, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Advanced => line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase),
        SubtitleFormat.Block => line.Contains("-->", StringComparison.Ordinal),
        SubtitleFormat.MicroDvd => SplitMicroDvd(line) is not null,
        _ => false
    };

    private static IEnumerable<string> ReadAdvancedCues(string path)
    {
        foreach (var line in ReadLinesSafe(path))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text
            var fields = line["Dialogue:".Length..].Split(',', 10);
            if (fields.Length < 10)
            {
                continue;
            }

            var text = StripOverrides(fields[9]).Replace("\\N", "\n", StringComparison.Ordinal);
            if (text.Trim().Length > 0)
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> ReadBlockCues(string path)
    {
        var buffer = new List<string>();
        var skipping = false;
        var timed = false;

        foreach (var raw in ReadLinesSafe(path))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                if (!skipping && buffer.Count > 0)
                {
                    yield return string.Join('\n', buffer);
                }

                buffer.Clear();
                skipping = false;
                timed = false;
                continue;
            }

            if (buffer.Count == 0 && !timed && IsVttBlockHeader(line))
            {
                skipping = true;
            }

            if (skipping)
            {
                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                timed = true;
                continue;
            }

            // ! Only the line ahead of the timing is the index. A later one is dialogue.
            if (!timed && line.All(char.IsAsciiDigit))
            {
                continue;
            }

            buffer.Add(line);
        }

        if (!skipping && buffer.Count > 0)
        {
            yield return string.Join('\n', buffer);
        }
    }

    private static IEnumerable<string> ReadMicroDvdCues(string path)
    {
        foreach (var line in ReadLinesSafe(path))
        {
            if (SplitMicroDvd(line) is string text && text.Trim().Length > 0)
            {
                yield return text.Replace('|', '\n');
            }
        }
    }

    private static bool IsVttBlockHeader(string line)
        => VttBlockHeaders.Any(h =>
            line.StartsWith(h, StringComparison.Ordinal)
            && (line.Length == h.Length || !char.IsLetter(line[h.Length])));

    // Null when the line is not {start}{end}text.
    private static string? SplitMicroDvd(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return null;
        }

        var first = trimmed.IndexOf('}', StringComparison.Ordinal);
        if (first < 2 || !trimmed[1..first].All(char.IsAsciiDigit))
        {
            return null;
        }

        var rest = trimmed[(first + 1)..];
        if (rest.Length == 0 || rest[0] != '{')
        {
            return null;
        }

        var second = rest.IndexOf('}', StringComparison.Ordinal);
        return second < 0 ? null : rest[(second + 1)..];
    }

    // Drops {\an8}-style override blocks.
    private static string StripOverrides(string text)
    {
        if (!text.Contains('{', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var depth = 0;

        foreach (var c in text)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> ReadLinesSafe(string path)
    {
        IEnumerator<string> enumerator;

        try
        {
            enumerator = File.ReadLines(path).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }
}
