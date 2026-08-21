using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// The shipping ISubtitleSource, over Jellyfin's own provider stack.
public class JellyfinSubtitleSource : ISubtitleSource
{
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinSubtitleSource> _logger;

    private readonly Lock _warnLock = new();
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private string _warnedFor = string.Empty;

    public JellyfinSubtitleSource(
        ISubtitleManager subtitleManager,
        ILibraryManager libraryManager,
        ILogger<JellyfinSubtitleSource> logger)
    {
        _subtitleManager = subtitleManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public IReadOnlyList<ProviderInfo>? Survey(PluginConfiguration config)
    {
        if (SampleVideo() is not { } sample)
        {
            return null;
        }

        var installed = InstalledNames(sample);
        var options = _libraryManager.GetLibraryOptions(sample);

        var ordered = DownloadProviders.Order(
            installed,
            options.DisabledSubtitleFetchers,
            options.SubtitleFetcherOrder,
            config.AdditionalDownloadProviders);

        // ! Disabled fetchers stay on the list and are marked. Dropping them makes a name the
        //   admin typed look misspelled when it is only switched off.
        var disabled = installed.Where(name => !DownloadProviders.IsListed(name, ordered));

        return ordered
            .Select(name => Describe(name, config, true))
            .Concat(disabled.Select(name => Describe(name, config, false)))
            .ToList();
    }

    private static ProviderInfo Describe(string name, PluginConfiguration config, bool enabled)
        => new(
            name,
            DownloadProviders.IsKnownDownloader(name, config.AdditionalDownloadProviders),
            enabled);

    public IReadOnlyList<string> Downloaders(Guid itemId, PluginConfiguration config)
    {
        if (Resolve(itemId) is not { } video)
        {
            return [];
        }

        var installed = InstalledNames(video);
        WarnUnresolved(installed, config);

        return Enabled(video, installed, config)
            .Where(name => DownloadProviders.IsKnownDownloader(name, config.AdditionalDownloadProviders))
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteSubtitleInfo>> SearchAsync(
        Guid itemId,
        string provider,
        string language,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || Resolve(itemId) is not { } video)
        {
            return [];
        }

        var request = BuildRequest(video, provider, language, config);

        // ! Never filter these on ProviderName. subbuzz reports its internal source there, so the
        //   filter drops every one of its answers; the request scoping is what narrows the search.
        return await _subtitleManager.SearchSubtitles(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubtitleResponse?> FetchAsync(string subtitleId, CancellationToken cancellationToken)
        => await _subtitleManager.GetRemoteSubtitles(subtitleId, cancellationToken).ConfigureAwait(false);

    // ! Built by hand. The Video overload ignores the fetcher fields and asks every provider.
    private SubtitleSearchRequest BuildRequest(
        Video video,
        string provider,
        string language,
        PluginConfiguration config)
    {
        // ! Resolved once. GetLibraryOptions walks to the collection folder on every call.
        var options = _libraryManager.GetLibraryOptions(video);
        var others = Enabled(video, InstalledNames(video), config, options)
            .Where(name => !DownloadProviders.Matches(name, provider))
            .Concat(options.DisabledSubtitleFetchers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var episode = video as Episode;

        return new SubtitleSearchRequest
        {
            Language = language,
            TwoLetterISOLanguageName = LanguageCodes.TwoLetterForm(language) ?? language,
            ContentType = episode is null ? VideoContentType.Movie : VideoContentType.Episode,
            MediaPath = video.Path,
            SeriesName = episode?.SeriesName,
            Name = episode is null ? video.Name : episode.SeriesName ?? video.Name,
            IndexNumber = video.IndexNumber,
            IndexNumberEnd = episode?.IndexNumberEnd,
            ParentIndexNumber = video.ParentIndexNumber,
            ProductionYear = video.ProductionYear,
            RuntimeTicks = video.RunTimeTicks,
            IsPerfectMatch = options.RequirePerfectSubtitleMatch,
            ProviderIds = new Dictionary<string, string>(video.ProviderIds, StringComparer.OrdinalIgnoreCase),

            // ! Both, together. SearchAllProviders alone leaves the choice to the admin's order.
            SearchAllProviders = false,
            SubtitleFetcherOrder = [provider],
            DisabledSubtitleFetchers = others,

            // Marks the request as unattended, which providers use for their own rate limits.
            IsAutomated = true
        };
    }

    private List<string> Enabled(
        Video video,
        List<string> installed,
        PluginConfiguration config,
        LibraryOptions? options = null)
    {
        options ??= _libraryManager.GetLibraryOptions(video);

        return DownloadProviders.Order(
            installed,
            options.DisabledSubtitleFetchers,
            options.SubtitleFetcherOrder,
            config.AdditionalDownloadProviders);
    }

    private List<string> InstalledNames(BaseItem item)
        => _subtitleManager
            .GetSupportedProviders(item)
            .Select(p => p.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

    private Video? Resolve(Guid itemId)
        => _libraryManager.GetItemById(itemId) as Video;

    private BaseItem? SampleVideo()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            Limit = 1
        }).FirstOrDefault();

    // A name the admin typed that matches no installed provider, reported once per spelling.
    private void WarnUnresolved(IReadOnlyList<string> installed, PluginConfiguration config)
    {
        if (config.AdditionalDownloadProviders.Length == 0)
        {
            return;
        }

        var unresolved = DownloadProviders.Unresolved(config.AdditionalDownloadProviders, installed);

        lock (_warnLock)
        {
            var setting = string.Join('|', config.AdditionalDownloadProviders);
            if (!string.Equals(setting, _warnedFor, StringComparison.Ordinal))
            {
                _warned.Clear();
                _warnedFor = setting;
            }

            foreach (var name in unresolved.Where(name => _warned.Add(name)))
            {
                _logger.LogWarning(
                    "No subtitle provider named {Provider} is installed; it will never be asked",
                    name);
            }
        }
    }
}
