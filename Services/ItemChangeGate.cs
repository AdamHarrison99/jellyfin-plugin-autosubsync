using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Decides whether a library change is worth opening an item for.
public class ItemChangeGate
{
    // ! A bound, not a policy. Clearing costs one redundant pass; leaking costs memory forever.
    private const int MaxTracked = 20_000;

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ConcurrentDictionary<Guid, string> _processed = new();

    public ItemChangeGate(IMediaSourceManager mediaSourceManager)
    {
        _mediaSourceManager = mediaSourceManager;
    }

    public bool HasWorkToDo(BaseItem item, PluginConfiguration config)
        => HasWorkToDo(item.Id, item.Path, config);

    // ! Call after the item is fully processed, so the stamp carries the writes just made.
    //   That is what makes the refresh those writes trigger compare equal.
    public void Commit(BaseItem item, PluginConfiguration config)
        => Commit(item.Id, item.Path, config);

    public void Forget(Guid itemId) => _processed.TryRemove(itemId, out _);

    // ! An unreadable signature always opens the item. Never guess that nothing changed.
    internal bool HasWorkToDo(Guid itemId, string? videoPath, PluginConfiguration config)
        => Signature(itemId, videoPath, config) is not { } signature
           || !_processed.TryGetValue(itemId, out var last)
           || !string.Equals(last, signature, StringComparison.Ordinal);

    internal void Commit(Guid itemId, string? videoPath, PluginConfiguration config)
    {
        if (Signature(itemId, videoPath, config) is not { } signature)
        {
            return;
        }

        if (_processed.Count >= MaxTracked)
        {
            _processed.Clear();
        }

        _processed[itemId] = signature;
    }

    // Overridden by the harness so the signature rules can be checked without a media server.
    internal virtual IEnumerable<string> ExternalSubtitlePaths(Guid itemId)
        => _mediaSourceManager
            .GetMediaStreams(itemId)
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal)
            .Select(s => s.Path)
            .Where(p => !string.IsNullOrEmpty(p));

    private string? Signature(Guid itemId, string? videoPath, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(videoPath))
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder();
            builder.Append(GateStamp(config)).Append('\n');
            Append(builder, videoPath);

            foreach (var path in ExternalSubtitlePaths(itemId).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Append(builder, path);
            }

            // ! Digested, not kept. The raw signature is hundreds of bytes per item and this
            //   map holds thousands of them.
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Size and write time only; hashing here would cost more than the work being avoided.
    private static void Append(StringBuilder builder, string path)
    {
        var info = new FileInfo(path);
        var exists = info.Exists;

        builder
            .Append(path)
            .Append('\t')
            .Append(exists ? info.Length : -1L)
            .Append('\t')
            .Append((exists ? info.LastWriteTimeUtc.Ticks : 0L).ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    // ! Wider than OutcomeStamp: anything changing which targets exist, or which results are
    //   kept, has to reopen an item the gate already closed.
    private static string GateStamp(PluginConfiguration config)
        => string.Join(
            '|',
            config.OutcomeStamp(),
            config.ProcessExternalSubtitles,
            config.ProcessEmbeddedSubtitles,
            !config.ProcessEmbeddedWhenExternalExists,
            config.DeduplicateSubtitles,
            string.Join(',', config.LanguageAllowList));
}
