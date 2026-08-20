using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace ConfigCheck;

// The 1.1.0.0 shape, verbatim from commit babcadd, so the XML this harness reads is the XML a
// real 1.1.0.0 server wrote and not a guess at it. XmlRoot pins the element name the current
// type will look for.
[XmlRoot("PluginConfiguration")]
public class LegacyPluginConfiguration : BasePluginConfiguration
{
    public bool DryRunMode { get; set; } = true;

    public string AssyConfigFilePath { get; set; } = string.Empty;

    public List<string> SyncToolChain { get; set; } = new() { "ffsubsync", "alass" };

    public int MaxAttempts { get; set; } = 2;

    public List<Guid> EnabledLibraryIds { get; set; } = new();

    public List<string> LanguageAllowList { get; set; } = new();

    public bool ProcessExternalSubtitles { get; set; } = true;

    public bool ProcessEmbeddedSubtitles { get; set; }

    public bool SkipEmbeddedWhenExternalExists { get; set; }

    public bool ConvertImageSubtitles { get; set; }

    public bool RemoveHearingImpairedTags { get; set; }

    public bool DeduplicateSubtitles { get; set; }

    public Jellyfin.Plugin.AutoSubSync.Configuration.ExternalWriteMode ExternalWriteMode { get; set; }

    public string OutputEncoding { get; set; } = "same_as_input";

    public string MarkerSuffix { get; set; } = "autosubsync";

    public int MinimumOffsetMs { get; set; } = 50;

    public int MaxConcurrentSyncs { get; set; }

    public int PerSyncTimeoutMinutes { get; set; } = 20;

    public bool AutoSyncOnItemAdded { get; set; } = true;

    public bool RefreshItemAfterSync { get; set; } = true;
}
