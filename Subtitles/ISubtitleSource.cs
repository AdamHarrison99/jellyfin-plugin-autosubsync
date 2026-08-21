using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// One installed provider: whether this plugin will download through it, and whether it is enabled.
public readonly record struct ProviderInfo(string Name, bool IsDownloader, bool IsEnabled);

// The narrow seam over Jellyfin's subtitle stack. The acquire loop is driven through this alone.
public interface ISubtitleSource
{
    // Every installed provider, asked order first. Null where the library holds no video to ask about.
    IReadOnlyList<ProviderInfo>? Survey(PluginConfiguration config);

    // Downloaders this item may be searched with, in the order they will be asked.
    IReadOnlyList<string> Downloaders(Guid itemId, PluginConfiguration config);

    // ! One provider per call. A search across all of them cannot be attributed or ordered.
    Task<IReadOnlyList<RemoteSubtitleInfo>> SearchAsync(
        Guid itemId,
        string provider,
        string language,
        CancellationToken cancellationToken);

    // ! The only fetch used. DownloadSubtitles writes an unmarked sidecar into the media folder.
    Task<SubtitleResponse?> FetchAsync(string subtitleId, CancellationToken cancellationToken);
}
