using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoSubSync.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalWriteMode
{
    Overwrite = 0,
    SideBySide = 1
}

public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Safety ----

    public bool DryRunMode { get; set; } = true;

    // ---- assy-cli execution ----

    // The binary is pinned to one build; there is no path to configure.
    public string AssyConfigFilePath { get; set; } = string.Empty;

    // ---- Engines ----

    public static readonly string[] KnownTools = ["ffsubsync", "alass", "autosubsync"];

    // ! Arrays, not List. Jellyfin stores config as XML, and XmlSerializer appends to a
    //   collection property it finds already populated.
    public string[] SyncToolChain { get; set; } = ["ffsubsync", "alass", "autosubsync"];

    // ---- Scope ----

    // ! Empty means no library is processed. Opt in, never opt out.
    public Guid[] EnabledLibraryIds { get; set; } = [];

    // ISO 639-2 codes. Empty means all.
    public string[] LanguageAllowList { get; set; } = [];

    public bool ProcessExternalSubtitles { get; set; } = true;

    public bool ProcessEmbeddedSubtitles { get; set; }

    // ! On, this drops signs-and-songs tracks. Defaults off.
    public bool SkipEmbeddedWhenExternalExists { get; set; }

    // Image-based tracks (PGS, VobSub, DVB) are OCR'd to text before syncing.
    public bool ConvertImageSubtitles { get; set; }

    public bool RemoveHearingImpairedTags { get; set; }

    // Collapses same-language duplicates once every one of them has synced.
    public bool DeduplicateSubtitles { get; set; }

    // ---- Output ----

    // Applies to external subtitles only; embedded tracks always become new sidecars.
    public ExternalWriteMode ExternalWriteMode { get; set; } = ExternalWriteMode.Overwrite;

    public string OutputEncoding { get; set; } = "same_as_input";

    // ! Changing this orphans output written under the old marker.
    public string MarkerSuffix { get; set; } = "autosubsync";

    public int MinimumOffsetMs { get; set; } = 50;

    // ---- Throttling ----

    public const int AutoConcurrency = 0;

    public int MaxConcurrentSyncs { get; set; } = AutoConcurrency;

    // Hung-process guard, not a throttle.
    public int PerSyncTimeoutMinutes { get; set; } = 20;

    // ---- Behavior ----

    // Covers ItemAdded and ItemUpdated.
    public bool AutoSyncOnItemAdded { get; set; } = true;

    public bool RefreshItemAfterSync { get; set; } = true;

    // Resolves AutoConcurrency to a real thread count.
    public int ResolveMaxConcurrentSyncs()
        => MaxConcurrentSyncs > 0
            ? MaxConcurrentSyncs
            : AutoConcurrencyFor(Environment.ProcessorCount);

    internal static int AutoConcurrencyFor(int processorCount)
    {
        if (processorCount <= 4)
        {
            return 1;
        }

        return Math.Clamp(processorCount / 2, 1, 8);
    }

    // Called on every save; the API accepts arbitrary JSON.
    public void Normalize()
    {
        MaxConcurrentSyncs = Math.Clamp(MaxConcurrentSyncs, AutoConcurrency, 8);
        PerSyncTimeoutMinutes = Math.Clamp(PerSyncTimeoutMinutes, 1, 240);
        MinimumOffsetMs = Math.Clamp(MinimumOffsetMs, 0, 600_000);

        // ! Must never be empty.
        MarkerSuffix = SanitizeMarker(MarkerSuffix);

        // ! The API accepts a null for any of these; every use below assumes it is not.
        SyncToolChain ??= [];
        LanguageAllowList ??= [];
        EnabledLibraryIds ??= [];

        SyncToolChain = SyncToolChain
            .Where(t => KnownTools.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (SyncToolChain.Length == 0)
        {
            SyncToolChain = ["ffsubsync", "alass", "autosubsync"];
        }

        if (string.IsNullOrWhiteSpace(OutputEncoding))
        {
            OutputEncoding = "same_as_input";
        }

        LanguageAllowList = LanguageAllowList
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToArray();

        EnabledLibraryIds = EnabledLibraryIds.Distinct().ToArray();
    }

    private static string SanitizeMarker(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "autosubsync" : cleaned;
    }
}
