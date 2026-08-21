using System.Text;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// The internal source behind one search result, where the provider is an aggregator.
public static class SubtitleSourceKey
{
    // ! A token this short can prefix a source it has nothing to do with.
    private const int MinimumTokenLength = 3;

    // ! An aggregator names its sources by host. A shapeless token lets an ordinary provider
    //   invent one and be under-retired.
    private static bool Shaped(string token)
        => token.Length >= MinimumTokenLength
           && token.Contains('.', StringComparison.Ordinal)
           && !token.Any(char.IsWhiteSpace);

    // Null wherever the result names no source both a label and its id agree on.
    public static string? For(RemoteSubtitleInfo info)
    {
        if (info.Id is not { Length: > 0 } id)
        {
            return null;
        }

        // ! The guid ahead of it is hex, so the first underscore is the one the source follows.
        var cut = id.IndexOf('_', StringComparison.Ordinal);

        if (cut < 0 || cut == id.Length - 1)
        {
            return null;
        }

        var tail = id[(cut + 1)..];

        foreach (var token in Tokens(info))
        {
            if (Shaped(token) && tail.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return token.ToLowerInvariant();
            }
        }

        return null;
    }

    // ! Every candidate is confirmed against the id before it becomes a key. A label is free text
    //   and the id is what the fetch that hit the wall was made through.
    private static IEnumerable<string> Tokens(RemoteSubtitleInfo info)
    {
        // ! Name is left out on purpose: an aggregator sets it from the candidate's filename, so
        //   it names a release and never a source.
        foreach (var label in new[] { info.Comment, info.ProviderName })
        {
            if (label is not { Length: > 0 })
            {
                continue;
            }

            var plain = Strip(label);
            var open = plain.IndexOf('[', StringComparison.Ordinal);
            var close = open < 0 ? -1 : plain.IndexOf(']', open);

            // ! Either half can hold the source: one aggregator brackets it, another brackets its
            //   own name and writes the source after. The id says which.
            if (close > open)
            {
                if (plain[(open + 1)..close].Trim() is { Length: > 0 } inside)
                {
                    yield return inside;
                }

                if (plain[(close + 1)..].Trim() is { Length: > 0 } after)
                {
                    yield return after;
                }

                continue;
            }

            if (plain.Trim() is { Length: > 0 } whole)
            {
                yield return whole;
            }
        }
    }

    // ! Labels carry markup. A tag left in matches no id, and the source is silently lost.
    private static string Strip(string label)
    {
        if (!label.Contains('<', StringComparison.Ordinal))
        {
            return label;
        }

        var built = new StringBuilder(label.Length);
        var inside = false;

        foreach (var c in label)
        {
            if (c == '<')
            {
                inside = true;
            }
            else if (c == '>')
            {
                inside = false;
            }
            else if (!inside)
            {
                built.Append(c);
            }
        }

        return built.ToString();
    }
}
