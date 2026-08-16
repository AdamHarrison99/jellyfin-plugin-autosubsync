using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AutoSubSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("6dbc32c5-1c68-481e-b066-9521552a2615");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // ! An upgrade must keep the choice made under the inverted 1.3.0.0 element.
        Configuration.AdoptLegacySettings();
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "AutoSubSync";

    public override Guid Id => PluginId;

    public override string Description =>
        "Automatically synchronizes external and embedded subtitles across your library using the AutoSubSync CLI (assy-cli).";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration typed)
        {
            typed.Normalize();
        }

        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "plugins",
            MenuIcon = "subtitles",
            DisplayName = "Auto Sub Sync"
        };
    }
}
