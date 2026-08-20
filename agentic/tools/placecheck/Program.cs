using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "placecheck-" + Guid.NewGuid().ToString("N"));
var media = Path.Combine(sandbox, "media");
Directory.CreateDirectory(media);

var paths = new Jellyfin.Plugin.AutoSubSync.PluginPaths(
    new StubPaths(sandbox),
    NullLogger<Jellyfin.Plugin.AutoSubSync.PluginPaths>.Instance);
var placer = new SubtitlePlacer(
    new BackupVault(paths, NullLogger<BackupVault>.Instance),
    NullLogger<SubtitlePlacer>.Instance);

var video = Path.Combine(media, "Movie (2001).mkv");
File.WriteAllText(video, "not really a video");

Check("an external text subtitle is overwritten in place, with a backup", () =>
{
    var original = Write("Movie (2001).eng.srt", "old");
    var target = External(original, "eng");
    var record = NewRecord();

    var result = placer.Place(target, record, Scratch(".srt", "new"), Config(ExternalWriteMode.Overwrite));

    Expect(result is not null, "placement returned null");
    Expect(result!.OutputPath == original, $"wrote to {result.OutputPath}, wanted the original");
    Expect(result.Provenance == SubtitleProvenance.Retimed, $"provenance was {result.Provenance}");
    Expect(result.BackupPath is not null && File.Exists(result.BackupPath), "no backup was taken");
    Expect(File.ReadAllText(original) == "new", "the original was not replaced");
});

// ! The guard this harness exists for. Text over a bitmap destroys the user's subtitle.
Check("an OCR'd source is never overwritten, even in Overwrite mode", () =>
{
    var original = Write("Movie (2001).eng.sub", "bitmap bytes");
    var target = External(original, "eng");
    target.RequiresOcr = true;

    var result = placer.Place(target, NewRecord(), Scratch(".srt", "ocr text"), Config(ExternalWriteMode.Overwrite));

    Expect(result is not null, "placement returned null");
    Expect(result!.OutputPath != original, "the bitmap source was overwritten with text");
    Expect(result.Provenance == SubtitleProvenance.Created, $"provenance was {result.Provenance}");
    Expect(result.BackupPath is null, "a sidecar write took a backup it does not need");
    Expect(File.ReadAllText(original) == "bitmap bytes", "the bitmap source was modified");
    Expect(Path.GetExtension(result.OutputPath) == ".srt", $"landed as {Path.GetExtension(result.OutputPath)}");
});

// ! Stripping rewrites ASS as SubRip. Overwriting would leave SubRip in a file named .ass.
Check("a format change is never written over the original's name", () =>
{
    var original = Write("Movie (2001).ita.ass", "[Script Info]");
    var target = External(original, "ita");

    var result = placer.Place(target, NewRecord(), Scratch(".srt", "stripped"), Config(ExternalWriteMode.Overwrite));

    Expect(result is not null, "placement returned null");
    Expect(result!.OutputPath != original, "SubRip content was written over an .ass filename");
    Expect(Path.GetExtension(result.OutputPath) == ".srt", $"landed as {Path.GetExtension(result.OutputPath)}");
    Expect(result.Provenance == SubtitleProvenance.Created, $"provenance was {result.Provenance}");
    Expect(File.ReadAllText(original) == "[Script Info]", "the original was modified");
});

Check("a same-format overwrite still overwrites", () =>
{
    var original = Write("Movie (2001).deu.ass", "old");
    var target = External(original, "deu");

    var result = placer.Place(target, NewRecord(), Scratch(".ass", "new"), Config(ExternalWriteMode.Overwrite));

    Expect(result is not null, "placement returned null");
    Expect(result!.OutputPath == original, $"wrote to {result.OutputPath}, wanted the original");
    Expect(File.ReadAllText(original) == "new", "the original was not replaced");
});

Check("an embedded track always lands beside the media", () =>
{
    var target = new SubtitleTarget
    {
        ItemName = "Movie",
        VideoPath = video,
        Origin = SubtitleOrigin.Embedded,
        StreamIndex = 2,
        Language = "eng",
        Key = SubtitleTarget.EmbeddedKey(2, "subrip")
    };

    var result = placer.Place(target, NewRecord(), Scratch(".srt", "text"), Config(ExternalWriteMode.Overwrite));

    Expect(result is not null, "placement returned null");
    Expect(result!.Provenance == SubtitleProvenance.Created, $"provenance was {result.Provenance}");
    Expect(File.Exists(result.OutputPath), "nothing was written");
});

Check("a stripped track loses its sdh token", () =>
{
    var marked = placer.Place(
        Flagged(hearingImpaired: true, "eng"), NewRecord(), Scratch(".srt", "a"), Config(ExternalWriteMode.SideBySide));

    var stripped = placer.Place(
        Flagged(hearingImpaired: false, "spa"), NewRecord(), Scratch(".srt", "b"), Config(ExternalWriteMode.SideBySide));

    Expect(marked is not null && stripped is not null, "placement returned null");
    Expect(
        Path.GetFileName(marked!.OutputPath).Contains(".sdh.", StringComparison.Ordinal),
        $"a flagged track was named {Path.GetFileName(marked.OutputPath)}");
    Expect(
        !Path.GetFileName(stripped!.OutputPath).Contains(".sdh.", StringComparison.Ordinal),
        $"a stripped track kept its token: {Path.GetFileName(stripped.OutputPath)}");
});

Check("a scratch file that cannot be placed leaves the original alone", () =>
{
    var original = Write("Movie (2001).fra.srt", "old");
    var target = External(original, "fra");

    var result = placer.Place(target, NewRecord(), Path.Combine(sandbox, "missing.srt"), Config(ExternalWriteMode.Overwrite));

    Expect(result is null, "placement claimed success with no scratch file");
    Expect(File.ReadAllText(original) == "old", "the original was modified by a failed placement");
});

TryCleanup();

if (failures > 0)
{
    Console.Error.WriteLine($"\nplacecheck: {failures} failure(s)");
    return 1;
}

Console.WriteLine("placecheck: all cases pass");
return 0;

void Check(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL  {name}: {ex.Message}");
        failures++;
    }
}

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

string Write(string name, string content)
{
    var path = Path.Combine(media, name);
    File.WriteAllText(path, content);
    return path;
}

string Scratch(string extension, string content)
{
    var path = Path.Combine(sandbox, Guid.NewGuid().ToString("N") + extension);
    File.WriteAllText(path, content);
    return path;
}

SubtitleTarget External(string subtitlePath, string language) => new()
{
    ItemName = "Movie",
    VideoPath = video,
    Origin = SubtitleOrigin.External,
    SubtitlePath = subtitlePath,
    Language = language,
    Key = SubtitleTarget.ExternalKey(video, subtitlePath)
};

SubtitleTarget Flagged(bool hearingImpaired, string language) => new()
{
    ItemName = "Movie",
    VideoPath = video,
    Origin = SubtitleOrigin.Embedded,
    StreamIndex = 3,
    Language = language,
    IsHearingImpaired = hearingImpaired,
    Key = SubtitleTarget.EmbeddedKey(3, "subrip")
};

static SyncRecord NewRecord() => new() { Id = Guid.NewGuid(), ItemName = "Movie" };

static PluginConfiguration Config(ExternalWriteMode mode)
{
    var config = new PluginConfiguration { ExternalWriteMode = mode };
    config.Normalize();
    return config;
}

void TryCleanup()
{
    try
    {
        Directory.Delete(sandbox, recursive: true);
    }
    catch (IOException)
    {
        Console.Error.WriteLine($"  note  left {sandbox} behind");
    }
}

internal sealed class StubPaths(string root) : IApplicationPaths
{
    public string ProgramDataPath => root;

    public string WebPath => Path.Combine(root, "web");

    public string ProgramSystemPath => root;

    public string DataPath => Path.Combine(root, "data");

    public string ImageCachePath => Path.Combine(root, "cache", "images");

    public string PluginsPath => Path.Combine(root, "plugins");

    public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

    public string LogDirectoryPath => Path.Combine(root, "log");

    public string ConfigurationDirectoryPath => Path.Combine(root, "config");

    public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

    public string CachePath => Path.Combine(root, "cache");

    public string TempDirectory => Path.Combine(root, "temp");

    public string VirtualDataPath => Path.Combine(root, "data");

    public string TrickplayPath => Path.Combine(root, "trickplay");

    public string BackupPath => Path.Combine(root, "backup");

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}
