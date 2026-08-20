using System.Xml.Serialization;
using ConfigCheck;
using Jellyfin.Plugin.AutoSubSync.Configuration;

// Does a 1.1.0.0 config file survive being read by the current type?
//
// Jellyfin's BasePlugin.LoadConfiguration catches ANY deserialization exception, constructs a
// default configuration, and saves it over the user's file. So a single incompatibility between
// the stored XML and the current type is a silent wipe of every setting. This harness writes the
// old shape and reads it with the new one.

var failures = new List<string>();

// A server with real settings on it: libraries picked, languages filtered, dry run turned off,
// side-by-side output, a custom marker, OCR and SDH on.
var legacy = new LegacyPluginConfiguration
{
    DryRunMode = false,
    SyncToolChain = new List<string> { "ffsubsync", "alass" },
    MaxAttempts = 3,
    EnabledLibraryIds = new List<Guid>
    {
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222")
    },
    LanguageAllowList = new List<string> { "eng", "spa" },
    ProcessExternalSubtitles = true,
    ProcessEmbeddedSubtitles = true,
    SkipEmbeddedWhenExternalExists = true,
    ConvertImageSubtitles = true,
    RemoveHearingImpairedTags = true,
    DeduplicateSubtitles = true,
    ExternalWriteMode = ExternalWriteMode.SideBySide,
    OutputEncoding = "utf-8",
    MarkerSuffix = "synced",
    MaxConcurrentSyncs = 4,
    PerSyncTimeoutMinutes = 45,
    AutoSyncOnItemAdded = false,
    RefreshItemAfterSync = false
};

var path = Path.Combine(AppContext.BaseDirectory, "v1.1.0.0-config.xml");

using (var writer = new StreamWriter(path))
{
    new XmlSerializer(typeof(LegacyPluginConfiguration)).Serialize(writer, legacy);
}

Console.WriteLine("Wrote the 1.1.0.0 config shape to " + path);
Console.WriteLine();
Console.WriteLine(File.ReadAllText(path));
Console.WriteLine();

PluginConfiguration? current = null;

try
{
    using var reader = new StreamReader(path);
    current = (PluginConfiguration?)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader);
    Console.WriteLine("Deserialized into the current type without throwing.");
}
catch (Exception ex)
{
    failures.Add("Deserializing a 1.1.0.0 config threw: " + ex.GetBaseException().Message);
    Console.WriteLine("THREW: " + ex.GetBaseException().Message);
}

if (current is not null)
{
    Check("DryRunMode", legacy.DryRunMode, current.DryRunMode);
    Check("ProcessExternalSubtitles", legacy.ProcessExternalSubtitles, current.ProcessExternalSubtitles);
    Check("ProcessEmbeddedSubtitles", legacy.ProcessEmbeddedSubtitles, current.ProcessEmbeddedSubtitles);
    // The 1.4.0.0 rename inverted the sense. AdoptLegacySettings is what carries the old
    // element across, and skipping the call is a silent flip of the setting on upgrade.
    current.AdoptLegacySettings();
    Check(
        "SkipEmbeddedWhenExternalExists -> ProcessEmbeddedWhenExternalExists",
        !legacy.SkipEmbeddedWhenExternalExists,
        current.ProcessEmbeddedWhenExternalExists);
    Check("ConvertImageSubtitles", legacy.ConvertImageSubtitles, current.ConvertImageSubtitles);
    Check("RemoveHearingImpairedTags", legacy.RemoveHearingImpairedTags, current.RemoveHearingImpairedTags);
    Check("DeduplicateSubtitles", legacy.DeduplicateSubtitles, current.DeduplicateSubtitles);
    Check("ExternalWriteMode", legacy.ExternalWriteMode, current.ExternalWriteMode);
    Check("OutputEncoding", legacy.OutputEncoding, current.OutputEncoding);
    Check("MarkerSuffix", legacy.MarkerSuffix, current.MarkerSuffix);
    Check("MaxConcurrentSyncs", legacy.MaxConcurrentSyncs, current.MaxConcurrentSyncs);
    Check("PerSyncTimeoutMinutes", legacy.PerSyncTimeoutMinutes, current.PerSyncTimeoutMinutes);
    Check("AutoSyncOnItemAdded", legacy.AutoSyncOnItemAdded, current.AutoSyncOnItemAdded);
    Check("RefreshItemAfterSync", legacy.RefreshItemAfterSync, current.RefreshItemAfterSync);

    Check("EnabledLibraryIds", string.Join(",", legacy.EnabledLibraryIds), string.Join(",", current.EnabledLibraryIds));
    Check("LanguageAllowList", string.Join(",", legacy.LanguageAllowList), string.Join(",", current.LanguageAllowList));
}

// SyncToolChain was retired when the chain collapsed to one engine, so the stored file carries an
// element the current type has no property for. Reaching here at all is the assertion: an unknown
// element must be ignored, because a throw is a silent wipe of every setting beside it.
if (current is not null && !File.ReadAllText(path).Contains("<SyncToolChain>", StringComparison.Ordinal))
{
    failures.Add("The legacy file no longer carries <SyncToolChain>, so nothing proves a retired element is ignored");
}

// Restart: the current type writes the file, the current type reads it back. Load does NOT call
// Normalize, so whatever survives this round trip is what a restarted server actually runs on.
Console.WriteLine();
Console.WriteLine("--- restart round trip (current type -> file -> current type) ---");

var saved = new PluginConfiguration
{
    DryRunMode = false,
    EnabledLibraryIds = [Guid.Parse("33333333-3333-3333-3333-333333333333")],
    LanguageAllowList = ["eng"],
    ProcessEmbeddedSubtitles = true,
    ConvertImageSubtitles = true,
    RemoveHearingImpairedTags = true,
    DeduplicateSubtitles = true,
    ExternalWriteMode = ExternalWriteMode.SideBySide,
    MarkerSuffix = "synced",
    MaxConcurrentSyncs = 2,
    AutoSyncOnItemAdded = false
};

var roundTripPath = Path.Combine(AppContext.BaseDirectory, "restart-config.xml");
var currentSerializer = new XmlSerializer(typeof(PluginConfiguration));

using (var writer = new StreamWriter(roundTripPath))
{
    currentSerializer.Serialize(writer, saved);
}

// Once adopted the legacy field is null, and a null must leave no element behind. An xsi:nil
// element would sit in every saved file forever, reading as a setting that is still there.
var savedXml = File.ReadAllText(roundTripPath);
Console.WriteLine($"  legacy element written back: {savedXml.Contains("SkipEmbeddedWhenExternalExists", StringComparison.Ordinal)}");
Check("adopted legacy element is not written back", false, savedXml.Contains("SkipEmbeddedWhenExternalExists", StringComparison.Ordinal));

using (var reader = new StreamReader(roundTripPath))
{
    var reloaded = (PluginConfiguration)currentSerializer.Deserialize(reader)!;

    Check("restart DryRunMode", saved.DryRunMode, reloaded.DryRunMode);
    Check("restart EnabledLibraryIds", string.Join(",", saved.EnabledLibraryIds), string.Join(",", reloaded.EnabledLibraryIds));
    Check("restart LanguageAllowList", string.Join(",", saved.LanguageAllowList), string.Join(",", reloaded.LanguageAllowList));
    Check("restart ConvertImageSubtitles", saved.ConvertImageSubtitles, reloaded.ConvertImageSubtitles);
    Check("restart RemoveHearingImpairedTags", saved.RemoveHearingImpairedTags, reloaded.RemoveHearingImpairedTags);
    Check("restart ExternalWriteMode", saved.ExternalWriteMode, reloaded.ExternalWriteMode);
    Check("restart MarkerSuffix", saved.MarkerSuffix, reloaded.MarkerSuffix);
    Check("restart AutoSyncOnItemAdded", saved.AutoSyncOnItemAdded, reloaded.AutoSyncOnItemAdded);
}

// A config the admin never saved after upgrading has no element for a property added later.
// XmlSerializer leaves the initializer alone in that case, which is the one path where a default
// legitimately wins.
Console.WriteLine();
Console.WriteLine("--- element absent from the file ---");

var partial = "<?xml version=\"1.0\"?>\n<PluginConfiguration>\n  <DryRunMode>false</DryRunMode>\n</PluginConfiguration>";
using (var reader = new StringReader(partial))
{
    var loaded = (PluginConfiguration)currentSerializer.Deserialize(reader)!;
    Console.WriteLine($"  EnabledLibraryIds with no element: '{string.Join(",", loaded.EnabledLibraryIds)}'");
    Console.WriteLine($"  LanguageAllowList with no element: '{string.Join(",", loaded.LanguageAllowList)}'");
    Console.WriteLine($"  RequireAudioConfirmation with no element: {loaded.RequireAudioConfirmation}");
    Console.WriteLine($"  RunOcrWhenTextExists with no element: {loaded.RunOcrWhenTextExists}");
    Console.WriteLine($"  ProcessEmbeddedWhenExternalExists with no element: {loaded.ProcessEmbeddedWhenExternalExists}");

    // An upgraded install that never saved keeps the behaviour it already had.
    Check("absent RequireAudioConfirmation", true, loaded.RequireAudioConfirmation);
    Check("absent RunOcrWhenTextExists", false, loaded.RunOcrWhenTextExists);
    Check("absent ProcessEmbeddedWhenExternalExists", false, loaded.ProcessEmbeddedWhenExternalExists);
}

// The two inclusive options replaced exclusive ones in 1.4.0.0. A stored file still carries the
// old element name, and reading it as the new default is a setting the user never changed.
Console.WriteLine();
Console.WriteLine("--- the inverted 1.3.0.0 element ---");

foreach (var stored in new[] { true, false })
{
    var xml = "<?xml version=\"1.0\"?><PluginConfiguration><SkipEmbeddedWhenExternalExists>"
        + (stored ? "true" : "false")
        + "</SkipEmbeddedWhenExternalExists></PluginConfiguration>";

    using var reader = new StringReader(xml);
    var loaded = (PluginConfiguration)currentSerializer.Deserialize(reader)!;
    loaded.AdoptLegacySettings();

    Console.WriteLine($"  Skip={stored} -> Process={loaded.ProcessEmbeddedWhenExternalExists}");
    Check($"adopted Skip={stored}", !stored, loaded.ProcessEmbeddedWhenExternalExists);

    // ! Adopting twice must not toggle. Load runs whenever the plugin is constructed.
    loaded.AdoptLegacySettings();
    Check($"re-adopted Skip={stored}", !stored, loaded.ProcessEmbeddedWhenExternalExists);
}

// A file already carrying the new element must not be reached by the fold at all.
using (var reader = new StringReader(
    "<?xml version=\"1.0\"?><PluginConfiguration><ProcessEmbeddedWhenExternalExists>true"
    + "</ProcessEmbeddedWhenExternalExists></PluginConfiguration>"))
{
    var loaded = (PluginConfiguration)currentSerializer.Deserialize(reader)!;
    loaded.AdoptLegacySettings();
    Check("new element survives the fold", true, loaded.ProcessEmbeddedWhenExternalExists);
}

// A real file off a real server, when one is dropped next to the harness. Jellyfin's
// LoadConfiguration catches every exception and saves defaults over the file, so this is the
// difference between "settings reset" being a load failure and being something else.
Console.WriteLine();
Console.WriteLine("--- real-config.xml ---");

var realPath = Path.Combine(AppContext.BaseDirectory, "real-config.xml");

if (!File.Exists(realPath))
{
    Console.WriteLine("  not present, skipped");
}
else
{
    try
    {
        using var reader = new StreamReader(realPath);
        var real = (PluginConfiguration)currentSerializer.Deserialize(reader)!;
        Console.WriteLine("  deserialized without throwing");
        Console.WriteLine($"  EnabledLibraryIds: {real.EnabledLibraryIds.Length} entries");
        Console.WriteLine($"  LanguageAllowList: '{string.Join(",", real.LanguageAllowList)}'");
        Console.WriteLine($"  ConvertImageSubtitles: {real.ConvertImageSubtitles}");
        Console.WriteLine($"  RemoveHearingImpairedTags: {real.RemoveHearingImpairedTags}");
        Console.WriteLine($"  DeduplicateSubtitles: {real.DeduplicateSubtitles}");
    }
    catch (Exception ex)
    {
        failures.Add("The real config file failed to deserialize: " + ex.GetBaseException().Message);
        Console.WriteLine("  THREW: " + ex.GetBaseException().Message);
    }
}

Console.WriteLine();

if (failures.Count == 0)
{
    Console.WriteLine("configcheck: clean - a 1.1.0.0 config upgrades with every setting intact");
    return 0;
}

foreach (var failure in failures)
{
    Console.WriteLine("FAIL: " + failure);
}

Console.WriteLine($"configcheck: {failures.Count} failure(s)");
return 1;

void Check(string name, object? expected, object? actual)
{
    var same = Equals(expected, actual);
    Console.WriteLine($"  {(same ? "ok  " : "LOST")} {name}: stored '{expected}' -> loaded '{actual}'");

    if (!same)
    {
        failures.Add($"{name}: stored '{expected}' but loaded '{actual}'");
    }
}
