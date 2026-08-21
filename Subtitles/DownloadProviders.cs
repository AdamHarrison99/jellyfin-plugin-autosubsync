namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Which installed subtitle providers download, and the order they are asked in.
public static class DownloadProviders
{
    // ! Every name is quoted from that plugin's own source, never from a listing or a README.
    //   The official plugin reports "Open Subtitles" with a space; the spaceless form matches nothing.
    public static readonly string[] Shipped =
    [
        "Open Subtitles",
        "Addic7ed/Gestdown Subtitles",
        "subbuzz"
    ];

    // ! Exact, case-insensitive, trimmed. A substring rule would pass "Local Subs (OpenSubtitles naming)".
    public static bool Matches(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsListed(string? name, IEnumerable<string> names)
        => names.Any(candidate => Matches(name, candidate));

    // A name the plugin ships, or one the admin added.
    public static bool IsKnownDownloader(string? name, IEnumerable<string> additional)
        => IsListed(name, Shipped) || IsListed(name, additional);

    // Installed providers the admin has not disabled, in the order they will be asked.
    public static List<string> Order(
        IEnumerable<string> installed,
        IEnumerable<string> disabled,
        IReadOnlyList<string> fetcherOrder,
        IReadOnlyList<string> priority)
    {
        var enabled = installed
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsListed(name, disabled))
            .ToList();

        // ! The priority box wins over the admin's order. It can never re-enable a disabled provider.
        var ordered = priority
            .Select(wanted => enabled.Find(name => Matches(name, wanted)))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();

        var rest = enabled
            .Where(name => !IsListed(name, ordered))
            .OrderBy(name => FetcherRank(name, fetcherOrder))
            .ToList();

        ordered.AddRange(rest);
        return ordered;
    }

    // A provider the admin never ordered sorts after every one they did.
    private static int FetcherRank(string name, IReadOnlyList<string> fetcherOrder)
    {
        for (var i = 0; i < fetcherOrder.Count; i++)
        {
            if (Matches(name, fetcherOrder[i]))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // Names in the box that match no installed provider, in the order they were typed.
    public static List<string> Unresolved(IEnumerable<string> priority, IReadOnlyList<string> installed)
        => priority
            .Where(wanted => !string.IsNullOrWhiteSpace(wanted) && !IsListed(wanted, installed))
            .Select(wanted => wanted.Trim())
            .ToList();
}
