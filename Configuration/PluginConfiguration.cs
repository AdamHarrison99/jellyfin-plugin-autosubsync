using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
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

    // ! The audio check runs either way. This decides what happens when it cannot reach a
    //   confident answer: on, the subtitle is left as it was; off, the engine's own score decides.
    public bool RequireAudioConfirmation { get; set; } = true;

    // ---- Scope ----

    // ! Empty means no library is processed. Opt in, never opt out.
    public Guid[] EnabledLibraryIds { get; set; } = [];

    // ISO 639-2 codes. Empty means all.
    public string[] LanguageAllowList { get; set; } = [];

    public bool ProcessExternalSubtitles { get; set; } = true;

    public bool ProcessEmbeddedSubtitles { get; set; }

    // Off, an embedded track a sidecar already covers is dropped.
    // ! On, signs-and-songs tracks come back with everything else.
    public bool ProcessEmbeddedWhenExternalExists { get; set; }

    // ! The element 1.3.0.0 and earlier wrote, with the opposite sense. Read only.
    [XmlElement("SkipEmbeddedWhenExternalExists")]
    public bool? LegacySkipEmbeddedWhenExternalExists { get; set; }

    // ! Never written back. A null nullable is stamped xsi:nil, which outlives the upgrade
    //   and reads as a setting that is still there.
    public bool ShouldSerializeLegacySkipEmbeddedWhenExternalExists() => false;

    // Image-based tracks (PGS, VobSub, DVB) are OCR'd to text before syncing.
    public bool ConvertImageSubtitles { get; set; }

    // ! Reachable only while ConvertImageSubtitles is on; a bitmap is unsupported without it.
    public bool RunOcrWhenTextExists { get; set; }

    public bool RemoveHearingImpairedTags { get; set; }

    // Collapses same-language duplicates once every one of them has synced.
    public bool DeduplicateSubtitles { get; set; }

    // ---- Download ----

    // Fetches a subtitle for a wanted language the item has nothing in.
    public bool AcquireMissingSubtitles { get; set; }

    // ! Reachable only while AcquireMissingSubtitles is on.
    public bool AcquireWhenEmbeddedExists { get; set; }

    // On, a hearing-impaired candidate is acceptable; it is never preferred.
    public bool AcquireHearingImpaired { get; set; }

    // ! Zero means unlimited, never disabled. The master toggle is what disables the feature.
    public int MaxDownloadsPerItem { get; set; } = DefaultMaxDownloadsPerItem;

    public const int DefaultMaxDownloadsPerItem = 3;

    // Provider names in the order they are asked, and the only way to name an unknown downloader.
    public string[] AdditionalDownloadProviders { get; set; } = [];

    // ---- Output ----

    // Applies to external subtitles only; embedded tracks always become new sidecars.
    public ExternalWriteMode ExternalWriteMode { get; set; } = ExternalWriteMode.Overwrite;

    public string OutputEncoding { get; set; } = "same_as_input";

    private static readonly string[] SupportedEncodings =
    [
        "same_as_input", "utf-8", "utf-8-sig", "utf-16", "utf-16-le", "utf-16-be",
        "latin-1", "cp1252", "ascii"
    ];

    // ! Changing this orphans output written under the old marker.
    public string MarkerSuffix { get; set; } = "autosubsync";

    // ---- Throttling ----

    public const int AutoConcurrency = 0;

    // A backstop against a runaway, not a tuned limit. The probe settles far below it.
    public const int MaxConcurrency = 64;

    public int MaxConcurrentSyncs { get; set; } = AutoConcurrency;

    // Hung-process guard, not a throttle.
    public int PerSyncTimeoutMinutes { get; set; } = 20;

    // ---- Behavior ----

    // Covers ItemAdded and ItemUpdated.
    public bool AutoSyncOnItemAdded { get; set; }

    public bool RefreshItemAfterSync { get; set; }

    // ! Bump on any change to what the audio check would decide. Nothing else reopens a record
    //   when the logic moves and no setting does.
    public const string CheckRevision = "check3";

    // ! Everything that changes what gets written, settings and check revision alike; throttling
    //   is absent by design. A record stamped with anything else is stale and runs again.
    public string OutcomeStamp()
        => string.Join(
            '|',
            CheckRevision,
            DryRunMode ? "dry" : "live",
            RequireAudioConfirmation ? "confirmed" : "scored",
            RemoveHearingImpairedTags ? "hi-" : "hi+",
            ConvertImageSubtitles ? "ocr+" : "ocr-",
            ExternalWriteMode,
            OutputEncoding,
            MarkerSuffix);

    // Resolves AutoConcurrency to the ceiling the probe climbs towards.
    public int ResolveMaxConcurrentSyncs()
        => MaxConcurrentSyncs > 0 ? MaxConcurrentSyncs : MaxConcurrency;

    // ! Called on load, never on save. Folding after a save would overwrite the fresh choice.
    public void AdoptLegacySettings()
    {
        if (LegacySkipEmbeddedWhenExternalExists is { } skip)
        {
            ProcessEmbeddedWhenExternalExists = !skip;
            LegacySkipEmbeddedWhenExternalExists = null;
        }
    }

    // Called on every save; the API accepts arbitrary JSON.
    public void Normalize()
    {
        MaxConcurrentSyncs = Math.Clamp(MaxConcurrentSyncs, AutoConcurrency, MaxConcurrency);
        PerSyncTimeoutMinutes = Math.Clamp(PerSyncTimeoutMinutes, 1, 240);

        // ! Floor of zero, which reads as unlimited.
        MaxDownloadsPerItem = Math.Max(MaxDownloadsPerItem, 0);

        // ! Must never be empty.
        MarkerSuffix = SanitizeMarker(MarkerSuffix);

        // ! The API accepts a null for either of these; every use below assumes it is not.
        LanguageAllowList ??= [];
        EnabledLibraryIds ??= [];
        AdditionalDownloadProviders ??= [];

        // ! Reaches the engine as --encoding. An unknown value fails every sync, and the failure
        //   names the engine, not this setting.
        if (Array.IndexOf(SupportedEncodings, OutputEncoding?.Trim().ToLowerInvariant()) < 0)
        {
            OutputEncoding = "same_as_input";
        }
        else
        {
            OutputEncoding = OutputEncoding!.Trim().ToLowerInvariant();
        }

        LanguageAllowList = LanguageAllowList
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToArray();

        EnabledLibraryIds = EnabledLibraryIds.Distinct().ToArray();

        // ! Order is the search order. The first spelling of a name wins.
        AdditionalDownloadProviders = AdditionalDownloadProviders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SanitizeMarker(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "autosubsync" : cleaned;
    }
}
