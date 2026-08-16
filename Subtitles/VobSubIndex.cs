using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// One language stream declared by a VobSub index.
public readonly record struct VobSubTrack(int Index, string? Language, int Images);

// Reads the language streams a VobSub .idx declares, and splits one out for conversion.
public static partial class VobSubIndex
{
    // ! Far past any real index. Hitting it refuses; a truncated block still parses as a
    //   valid subtitle.
    private const int MaxLinesRead = 500_000;

    [GeneratedRegex(@"^id:\s*(?<lang>[A-Za-z]{2,3})\s*,\s*index:\s*(?<index>\d+)", RegexOptions.None)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^timestamp:", RegexOptions.None)]
    private static partial Regex ImageRegex();

    public static bool IsIndexPath(string path)
        => Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase);

    // The .idx a VobSub .sub is paired with, which carries the timings and the language list.
    public static string IndexFor(string subPath)
        => Path.ChangeExtension(subPath, ".idx");

    public static IReadOnlyList<VobSubTrack> Read(string idxPath)
    {
        var tracks = new List<VobSubTrack>();

        try
        {
            var index = -1;
            string? language = null;
            var images = 0;
            var scanned = 0;

            foreach (var line in File.ReadLines(idxPath))
            {
                if (++scanned > MaxLinesRead)
                {
                    return [];
                }

                var id = IdRegex().Match(line);

                if (id.Success)
                {
                    if (index >= 0)
                    {
                        tracks.Add(new VobSubTrack(index, language, images));
                    }

                    index = int.Parse(id.Groups["index"].ValueSpan, provider: null);
                    language = id.Groups["lang"].Value;
                    images = 0;
                    continue;
                }

                if (index >= 0 && ImageRegex().IsMatch(line))
                {
                    images++;
                }
            }

            if (index >= 0)
            {
                tracks.Add(new VobSubTrack(index, language, images));
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return tracks;
    }

    // ! The block is copied byte for byte. Rewriting its index or langidx changes nothing the
    //   converter reads, and both were measured against a mid-list track.
    public static bool TryWriteSingle(string idxPath, int trackIndex, string outputPath)
    {
        try
        {
            var header = new List<string>();
            var block = new List<string>();
            var inside = false;
            var seenAny = false;
            var scanned = 0;

            foreach (var line in File.ReadLines(idxPath))
            {
                if (++scanned > MaxLinesRead)
                {
                    return false;
                }

                var id = IdRegex().Match(line);

                if (id.Success)
                {
                    seenAny = true;
                    inside = int.Parse(id.Groups["index"].ValueSpan, provider: null) == trackIndex;
                }
                else if (!seenAny)
                {
                    header.Add(line);
                }

                if (inside)
                {
                    block.Add(line);
                }
            }

            if (block.Count == 0)
            {
                return false;
            }

            header.AddRange(block);
            File.WriteAllLines(outputPath, header);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
