using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Decides which library items the plugin may touch.
public class LibraryScopeResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryScopeResolver> _logger;

    public LibraryScopeResolver(ILibraryManager libraryManager, ILogger<LibraryScopeResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public IReadOnlyList<BaseItem> GetItemsInScope(PluginConfiguration config)
    {
        if (config.EnabledLibraryIds.Length == 0)
        {
            _logger.LogInformation("No libraries are enabled; nothing to do");
            return [];
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true
        });

        // Fetched once for the whole sweep.
        var folders = _libraryManager.GetVirtualFolders();

        return items.Where(item => IsInScope(item, config, folders)).ToList();
    }

    public bool IsInScope(BaseItem item, PluginConfiguration config)
        => IsInScope(item, config, null);

    private bool IsInScope(BaseItem item, PluginConfiguration config, IReadOnlyList<VirtualFolderInfo>? folders)
    {
        // Excludes stubs, .strm entries, and unmounted storage.
        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return false;
        }

        // ! Empty means none, never all.
        if (config.EnabledLibraryIds.Length == 0)
        {
            return false;
        }

        var libraryId = GetLibraryId(item, folders);
        return libraryId is not null && config.EnabledLibraryIds.Contains(libraryId.Value);
    }

    public Guid? GetLibraryId(BaseItem item, IReadOnlyList<VirtualFolderInfo>? folders = null)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        folders ??= _libraryManager.GetVirtualFolders();

        var match = folders
            .SelectMany(f => f.Locations.Select(loc => (Folder: f, Location: loc)))
            .Where(x => IsUnder(item.Path, x.Location))
            .OrderByDescending(x => x.Location.Length)
            .Select(x => x.Folder)
            .FirstOrDefault();

        return match is not null && Guid.TryParse(match.ItemId, out var id) ? id : null;
    }

    // ! Boundary check: /media/movies must not match /media/movies-4k.
    internal static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');

        // ! Ignoring case off Windows would accept a sibling library as this one, and this
        //   decides whether an unticked library gets synced.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedPath.StartsWith(normalizedRoot, comparison))
        {
            return false;
        }

        return normalizedPath.Length == normalizedRoot.Length
               || normalizedPath[normalizedRoot.Length] == '/';
    }

}
