namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Pulls cue text out of a subtitle file without a full parse.
public static class SubtitleContent
{
    private const int MaxLinesScanned = 4000;

    // ! Bounds a corrupt or mislabelled file. Real subtitles are far below this.
    private const int MaxLinesRead = 400_000;

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

    // Null when the format is unknown; equal keys mean same format and same extension.
    public static string? FormatKey(string path)
    {
        var format = FormatOf(path);
        return format == SubtitleFormat.Unknown
            ? null
            : format + "|" + Path.GetExtension(path).ToLowerInvariant();
    }

    // One element per styling decision: style definitions, per-cue style, and inline markup.
    public static IEnumerable<string> ReadFormatting(string path)
        => FormatOf(path) switch
        {
            SubtitleFormat.Advanced => ReadAdvancedFormatting(path),
            SubtitleFormat.Block => ReadBlockFormatting(path),
            SubtitleFormat.MicroDvd => ReadMicroDvdFormatting(path),
            _ => Enumerable.Empty<string>()
        };

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

    // Style definitions plus, per cue, the style it names and the overrides it applies.
    private static IEnumerable<string> ReadAdvancedFormatting(string path)
    {
        var inStyles = false;

        foreach (var raw in ReadLinesSafe(path))
        {
            var line = raw.Trim();

            if (line.StartsWith('['))
            {
                inStyles = line.Contains("Styles", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inStyles && line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                yield return "style=" + Condense(line["Style:".Length..]);
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line["Dialogue:".Length..].Split(',', 10);
            if (fields.Length < 10)
            {
                continue;
            }

            yield return "cue=" + Condense(fields[3]);
            yield return "margin=" + Condense(fields[5]) + ',' + Condense(fields[6]) + ',' + Condense(fields[7]);
            yield return "effect=" + Condense(fields[8]);

            foreach (var block in Blocks(fields[9], '{', '}'))
            {
                yield return "override=" + block;
            }
        }
    }

    // Inline markup, and the cue settings VTT puts after its timestamps.
    private static IEnumerable<string> ReadBlockFormatting(string path)
    {
        foreach (var raw in ReadLinesSafe(path))
        {
            var line = raw.Trim();

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                var settings = Condense(line[(line.IndexOf("-->", StringComparison.Ordinal) + 3)..]);
                var space = settings.IndexOf(' ', StringComparison.Ordinal);
                if (space >= 0)
                {
                    yield return "settings=" + settings[(space + 1)..];
                }

                continue;
            }

            foreach (var tag in Blocks(line, '<', '>'))
            {
                yield return "tag=" + tag;
            }

            foreach (var block in Blocks(line, '{', '}'))
            {
                yield return "override=" + block;
            }
        }
    }

    private static IEnumerable<string> ReadMicroDvdFormatting(string path)
    {
        foreach (var line in ReadLinesSafe(path))
        {
            if (SplitMicroDvd(line) is not string text)
            {
                continue;
            }

            foreach (var block in Blocks(text, '{', '}'))
            {
                yield return "override=" + block;
            }
        }
    }

    // ! Nested braces are one block. ASS writes {\pos(1,2)} but also {\k1}{\k2} on one line.
    private static IEnumerable<string> Blocks(string value, char open, char close)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == open)
            {
                if (depth == 0)
                {
                    start = i + 1;
                }

                depth++;
            }
            else if (value[i] == close && depth > 0 && --depth == 0)
            {
                yield return Condense(value[start..i]);
            }
        }
    }

    private static string Condense(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToLowerInvariant(c));
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
            var read = 0;

            while (read++ < MaxLinesRead)
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
