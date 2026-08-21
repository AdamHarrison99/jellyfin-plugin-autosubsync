using Jellyfin.Plugin.AutoSubSync.Subtitles;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Providers, and the internal sources of one, that answered with a wall this sweep. Neither is
// asked again until the next sweep.
public class ProviderRetirement
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noted = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (_gate)
        {
            _retired.Clear();
            _sources.Clear();
            _noted.Clear();
        }
    }

    // True the first time this sweep. A provider failing every item logs once, not once per item.
    public bool NoteFailure(string provider)
    {
        lock (_gate)
        {
            return _noted.Add(provider.Trim());
        }
    }

    // ! Neither cause clears within a sweep. A spent allowance resets on the provider's own clock,
    //   and credentials the provider refused will be refused again.
    public void Retire(string provider, string reason)
    {
        lock (_gate)
        {
            _retired[provider.Trim()] = reason;
        }
    }

    // One internal source of an aggregator. Its siblings go on being asked.
    public void RetireSource(string provider, string source, string reason)
    {
        lock (_gate)
        {
            _sources[Key(provider, source)] = reason;
        }
    }

    public string? ReasonFor(string provider)
    {
        lock (_gate)
        {
            return _retired.GetValueOrDefault(provider.Trim());
        }
    }

    // ! A walled provider carries every source with it, so the whole-provider key is read first.
    public string? ReasonFor(string provider, string? source)
    {
        lock (_gate)
        {
            return _retired.GetValueOrDefault(provider.Trim())
                ?? (source is null ? null : _sources.GetValueOrDefault(Key(provider, source)));
        }
    }

    public IReadOnlyList<string> Live(IEnumerable<string> providers)
    {
        lock (_gate)
        {
            return _retired.Count == 0
                ? providers.ToList()
                : providers.Where(p => !_retired.ContainsKey(p.Trim())).ToList();
        }
    }

    // What the record says when every downloader has stopped answering.
    public string Summary()
    {
        lock (_gate)
        {
            return _retired.Count == 0
                ? "no subtitle provider answered"
                : string.Join(", ", _retired.Select(pair => $"{pair.Key} {pair.Value}"));
        }
    }

    // ! Type names, walked through the whole chain. The provider plugins own these exceptions and
    //   this repo cannot reference them.
    public static string? RetirementReason(Exception error)
    {
        var name = error.GetType().Name;

        if (name.Contains("RateLimit", StringComparison.Ordinal)
            || name.Contains("TooManyRequests", StringComparison.Ordinal))
        {
            return "has spent its download allowance";
        }

        if (name.Contains("Authentication", StringComparison.Ordinal)
            || name.Contains("Unauthorized", StringComparison.Ordinal))
        {
            return "refused the credentials it was given";
        }

        // ! An AggregateException holds several. Following InnerException alone reads the first.
        foreach (var cause in Causes(error))
        {
            if (RetirementReason(cause) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    private static IEnumerable<Exception> Causes(Exception error)
        => error is AggregateException group
            ? group.InnerExceptions
            : error.InnerException is { } inner ? [inner] : [];

    // ! Provider names hold slashes and spaces. Joining on a character no name can carry is what
    //   stops one pair's key colliding with another's.
    private static string Key(string provider, string source)
        => provider.Trim() + '\n' + source.Trim();
}
