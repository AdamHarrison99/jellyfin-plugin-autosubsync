using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

public readonly record struct OcrReading(int Words, double MeanWordLength, double ShortWordShare)
{
    public bool IsJudgeable => Words >= OcrReadability.MinimumWords;

    // ! Both, ¬either. One signal alone puts the bound within reach of a language of short
    //   words; the two measured noise readings fail both by a wide margin.
    public bool IsNoise
        => IsJudgeable
           && MeanWordLength < OcrReadability.MinimumMeanWordLength
           && ShortWordShare > OcrReadability.MaximumShortWordShare;
}

// Decides whether OCR output is words or noise, from the shape of its text alone.
public static class OcrReadability
{
    // ! Below the lowest real reading, ¬between the populations. Five real subtitles and two
    //   usable reads run 3.93 to 7.39; the two noise readings are 2.53 and 2.72.
    public const double MinimumMeanWordLength = 3.5;

    // Real text runs 16.6% to 24.0% here; the two noise readings are 58.0% and 56.1%.
    public const double MaximumShortWordShare = 0.35;

    // ! A gate that fires on a handful of tokens would refuse a forced track of six captions.
    public const int MinimumWords = 200;

    private const int ShortWordLength = 2;

    public static OcrReading Read(string path)
    {
        try
        {
            return Measure(File.ReadLines(path));
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    // ! Latin words only. A CJK track carries no spaced words at all, and it falls under
    //   MinimumWords and is left unjudged.
    public static OcrReading Measure(IEnumerable<string> lines)
    {
        var words = 0;
        var letters = 0L;
        var shortWords = 0;

        foreach (var line in lines)
        {
            if (line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var length = Alphabetic(token);

                if (length == 0)
                {
                    continue;
                }

                words++;
                letters += length;

                if (length <= ShortWordLength)
                {
                    shortWords++;
                }
            }
        }

        return words == 0
            ? default
            : new OcrReading(words, (double)letters / words, (double)shortWords / words);
    }

    // Punctuation and stray marks are not what makes a word long or short.
    private static int Alphabetic(string token)
    {
        var count = 0;

        foreach (var character in token)
        {
            if (char.IsAsciiLetter(character))
            {
                count++;
            }
        }

        return count;
    }
}
