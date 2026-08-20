using System.Globalization;
using System.Text;
using Jellyfin.Plugin.AutoSubSync;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Measures SubtitleSimilarity against pairs whose relationship is known.
// Given two paths it scores those instead.

const double Threshold = 0.85;

var sandbox = Path.Combine(Path.GetTempPath(), "dedupecheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);

if (args.Length == 2)
{
    var measured = SubtitleSimilarity.Compare(args[0], args[1]);
    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "content {0:P1}, formatting {1:P1}  {2} vs {3}  =>  {4}",
        measured.Content,
        measured.Formatting,
        Path.GetFileName(args[0]),
        Path.GetFileName(args[1]),
        measured.Matches(Threshold) ? "WOULD COLLAPSE" : "kept apart"));
    return 0;
}

var failures = 0;

var vocabulary = ("the a and of to in that was it for on with as his they at be this from have or by one had not but what all "
    + "were when we there can an your which their said if do will each about how up out them then she many some so these "
    + "would other into has more her two like him see time could no make than first been its who now people my over did "
    + "down way only find water long little very after words called just where most know get through back much before go "
    + "good new write our man too any day same right look think also around another came come work three must does part "
    + "even place well such here take why things help put years different away again off went old number great tell men "
    + "say small every found still between name should home big give air line set own under read last never us left end "
    + "along while might next sound below saw something thought both few those always looked show large often together "
    + "asked house world going want school important until form food keep children feet land side without boy once animal "
    + "life enough took sometimes four head above kind began almost live page got earth need far hand high year mother "
    + "light country father let night following picture being study second eye soon times story boys since white days ever "
    + "paper hard near sentence better best across during today others however sure means knew try told young miles sun "
    + "ways thing whole hear example heard several change answer room sea against top turned learn point city play toward "
    + "five himself usually money seen car morning body upon family later turn move face door cut done group true half red "
    + "fish plants living black eat short run book gave order open ground cold really table remember tree course front "
    + "space inside ability").Split(' ', StringSplitOptions.RemoveEmptyEntries);

// A 200-cue subtitle standing in for a feature-length track.
var baseline = Cues(200, new Lcg(4111));

Console.WriteLine("-- collapse --");

Collapses("the same file compared with itself", Srt(baseline), Srt(baseline));

Collapses(
    "identical text at different timings, which is what an unsynced copy looks like",
    Srt(baseline),
    Srt(baseline, shiftSeconds: 12));

Collapses(
    "the same subtitle reflowed onto two lines per cue",
    Srt(baseline),
    Srt(baseline.Select(Reflow).ToArray()));

Collapses(
    "the same subtitle re-split into twice as many cues",
    Srt(baseline),
    Srt(Resplit(baseline)));

Collapses(
    "the same subtitle with different punctuation and capitalisation",
    Srt(baseline),
    Srt(baseline.Select(c => c.ToUpperInvariant().Replace(" ", ", ", StringComparison.Ordinal)).ToArray()));

Collapses(
    "a re-release that reworded 5% of its cues",
    Srt(baseline),
    Srt(Reworded(baseline, everyNth: 20)));

Collapses(
    "a re-release that reworded 10% of its cues",
    Srt(baseline),
    Srt(Reworded(baseline, everyNth: 10)));

Collapses(
    "an OCR'd copy of the same subtitle, 1% of characters misread",
    Srt(baseline),
    Srt(Ocr(baseline, rate: 0.01)));

Collapses(
    "an OCR'd copy of the same subtitle, 2% of characters misread",
    Srt(baseline),
    Srt(Ocr(baseline, rate: 0.02)));

Collapses(
    "two ASS files with the same styling",
    Ass(baseline, "Default", "Arial,20,&H00FFFFFF"),
    Ass(baseline, "Default", "Arial,20,&H00FFFFFF"));

Console.WriteLine();
Console.WriteLine("-- keep apart --");

Kept(
    "the same text as SRT and as ASS",
    Srt(baseline),
    Ass(baseline, "Default", "Arial,20,&H00FFFFFF"));

Kept(
    "two ASS files whose style definitions differ",
    Ass(baseline, "Default", "Arial,20,&H00FFFFFF"),
    Ass(baseline, "Default", "Verdana,28,&H0000FFFF"));

Kept(
    "two ASS files using different style names per cue",
    Ass(baseline, "Default", "Arial,20,&H00FFFFFF"),
    Ass(baseline, "Top", "Arial,20,&H00FFFFFF"));

Kept(
    "the same subtitle plain and fully italicised",
    Srt(baseline),
    Srt(baseline.Select(c => "<i>" + c + "</i>").ToArray()));

Kept(
    "the same text with positioning overrides added",
    Srt(baseline),
    Srt(baseline.Select(c => "{\\an8}" + c).ToArray()));

Kept(
    "a re-release that reworded 20% of its cues, which is a different edition",
    Srt(baseline),
    Srt(Reworded(baseline, everyNth: 5)));

Kept(
    "a bad scan, 5% of characters misread",
    Srt(baseline),
    Srt(Ocr(baseline, rate: 0.05)));

Kept(
    "a different translation of the same film",
    Srt(baseline),
    Srt(Cues(200, new Lcg(9377))));

Kept(
    "a forced track against the full subtitle it was cut from",
    Srt(baseline),
    Srt(baseline.Take(8).ToArray()));

Kept(
    "two short forced tracks that happen to share their few cues",
    Srt(baseline.Take(6).ToArray()),
    Srt(baseline.Take(6).ToArray()));

Console.WriteLine();
Console.WriteLine("-- grouping --");

Grouping(
    "one file named by two targets survives",
    twoTargetsOneFile: true,
    expectRemoved: 0,
    expectSurvivors: 1);

Grouping(
    "two identical files still collapse to one",
    twoTargetsOneFile: false,
    expectRemoved: 1,
    expectSurvivors: 1);

RetiredDoesNotPoison();

// The survivor should not keep a discriminator only its duplicates made necessary.
void Naming(string path, string? expected)
{
    var actual = SubtitleDeduplicator.CanonicalPath(path);
    var got = actual is null ? "<none>" : Path.GetFileName(actual);
    var want = expected ?? "<none>";
    var ok = string.Equals(got, want, StringComparison.Ordinal);

    if (!ok)
    {
        failures++;
    }

    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {Path.GetFileName(path),-32} -> {got,-28} {what(want, ok)}");

    static string what(string want, bool ok) => ok ? string.Empty : $"(wanted {want})";
}

Console.WriteLine();
Naming(@"C:\m\Movie (2001).eng.0.srt", "Movie (2001).eng.srt");
Naming(@"C:\m\Movie (2001).eng.10.srt", "Movie (2001).eng.srt");
Naming(@"C:\m\Movie (2001).eng.147.srt", null);
Naming(@"C:\m\Movie (2001).eng.sdh.1.srt", "Movie (2001).eng.sdh.srt");
Naming(@"C:\m\Movie (2001).eng.srt", null);
Naming(@"C:\m\Movie.srt", null);

// A four-digit tail is a year, and stripping it renames the subtitle off its video.
Naming(@"C:\m\Some Film.2003.srt", null);
Naming(@"C:\m\Some Film.1999.eng.2003.srt", null);

Directory.Delete(sandbox, recursive: true);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "dedupecheck: clean" : $"dedupecheck: {failures} failure(s)");

return failures == 0 ? 0 : 1;

void Collapses(string what, string left, string right) => Measure(what, left, right, expected: true);

void Kept(string what, string left, string right) => Measure(what, left, right, expected: false);

void Measure(string what, string left, string right, bool expected)
{
    var score = SubtitleSimilarity.Compare(left, right);
    var collapses = score.Matches(Threshold);

    if (collapses != expected)
    {
        failures++;
    }

    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "{0}  content {1,7:P1}  formatting {2,7:P1}   {3}",
        collapses == expected ? "ok  " : "FAIL",
        score.Content,
        score.Formatting,
        what));
}

// Drives the real deduplicator, which decides what a group is before similarity is consulted.
void Grouping(string what, bool twoTargetsOneFile, int expectRemoved, int expectSurvivors)
{
    var root = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
    var media = Path.Combine(root, "media");
    Directory.CreateDirectory(media);

    var body = File.ReadAllText(Srt(baseline));
    var video = Path.Combine(media, "Movie (2001).mkv");
    File.WriteAllText(video, "not really a video");

    var first = Path.Combine(media, "Movie (2001).eng.srt");
    File.WriteAllText(first, body);

    var second = first;
    if (!twoTargetsOneFile)
    {
        second = Path.Combine(media, "Movie (2001).eng.0.srt");
        File.WriteAllText(second, body);
    }

    var itemId = Guid.NewGuid();
    var store = new FakeStore();
    var targets = new List<SubtitleTarget> { Target(itemId, video, first), Target(itemId, video, second) };

    foreach (var target in targets)
    {
        store.Upsert(new SyncRecord
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            ItemName = "Movie",
            TargetKey = target.Key,
            OutputPath = target.SubtitlePath,
            Status = SyncStatus.Synced,
            Provenance = SubtitleProvenance.Retimed
        });
    }

    var paths = new PluginPaths(new StubPaths(root), NullLogger<PluginPaths>.Instance);
    var deduplicator = new SubtitleDeduplicator(
        store,
        new BackupVault(paths, NullLogger<BackupVault>.Instance),
        NullLogger<SubtitleDeduplicator>.Instance);

    var config = new PluginConfiguration { DeduplicateSubtitles = true, DryRunMode = false };
    var report = deduplicator.ProcessItem(itemId, targets, config);

    var survivors = new[] { first, second }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count(File.Exists);

    // Whatever lived through it must not be carrying a discriminator any more.
    var left = Directory.GetFiles(media, "*.srt").Select(Path.GetFileName).ToList();
    var discriminated = left.Count(n => SubtitleDeduplicator.CanonicalPath(n!) is not null);

    // ! A removal the panel cannot report is the defect K1 fixed. Retired, ¬Stale, and carrying
    //   the stage that says so.
    var reportable = store.GetAll().Count(r =>
        r.Retired
        && !r.Stale
        && SyncOutcome.OnStageTable(r)
        && !SyncOutcome.OnCards(r)
        && r.Stages.Any(s =>
            s.Kind == SubtitleStageKind.Deduplicate && s.Outcome == StageOutcome.Succeeded));

    var ok = report.Removed == expectRemoved
             && survivors == expectSurvivors
             && discriminated == 0
             && reportable == expectRemoved;

    if (!ok)
    {
        failures++;
    }

    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "{0}  removed {1}  survivors {2}  reportable {3}   {4}",
        ok ? "ok  " : "FAIL",
        report.Removed,
        survivors,
        reportable,
        what));
}

// A row this deduplicator retired names a file it deleted itself. Reading that absence as an
// unknown poisons the slot and switches deduplication off for the language, silently.
void RetiredDoesNotPoison()
{
    var root = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
    var media = Path.Combine(root, "media");
    Directory.CreateDirectory(media);

    var body = File.ReadAllText(Srt(baseline));
    var video = Path.Combine(media, "Movie (2001).mkv");
    File.WriteAllText(video, "not really a video");

    var first = Path.Combine(media, "Movie (2001).eng.srt");
    var second = Path.Combine(media, "Movie (2001).eng.0.srt");
    File.WriteAllText(first, body);
    File.WriteAllText(second, body);

    var itemId = Guid.NewGuid();
    var store = new FakeStore();
    var targets = new List<SubtitleTarget> { Target(itemId, video, first), Target(itemId, video, second) };

    foreach (var target in targets)
    {
        store.Upsert(new SyncRecord
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            ItemName = "Movie",
            TargetKey = target.Key,
            OutputPath = target.SubtitlePath,
            Status = SyncStatus.Synced,
            Provenance = SubtitleProvenance.Retimed
        });
    }

    // An embedded track in the same slot, extracted on an earlier run and removed as a duplicate.
    // Its stream is still in the video, so discovery offers it again with no file behind it.
    var embedded = new SubtitleTarget
    {
        ItemId = itemId,
        ItemName = "Movie",
        VideoPath = video,
        Origin = SubtitleOrigin.Embedded,
        StreamIndex = 3,
        Language = "eng",
        Key = SubtitleTarget.EmbeddedKey(3, "subrip")
    };

    store.Upsert(new SyncRecord
    {
        Id = Guid.NewGuid(),
        ItemId = itemId,
        ItemName = "Movie",
        TargetKey = embedded.Key,
        OutputPath = Path.Combine(media, "Movie (2001).eng.autosubsync.srt"),
        Status = SyncStatus.Synced,
        Provenance = SubtitleProvenance.Created,
        Retired = true
    });

    targets.Add(embedded);

    var paths = new PluginPaths(new StubPaths(root), NullLogger<PluginPaths>.Instance);
    var deduplicator = new SubtitleDeduplicator(
        store,
        new BackupVault(paths, NullLogger<BackupVault>.Instance),
        NullLogger<SubtitleDeduplicator>.Instance);

    var config = new PluginConfiguration { DeduplicateSubtitles = true, DryRunMode = false };
    var report = deduplicator.ProcessItem(itemId, targets, config);

    var ok = report.Removed == 1;
    if (!ok)
    {
        failures++;
    }

    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "{0}  removed {1}   a retired row does not switch the slot off",
        ok ? "ok  " : "FAIL",
        report.Removed));
}

static SubtitleTarget Target(Guid itemId, string video, string subtitle) => new()
{
    ItemId = itemId,
    ItemName = "Movie",
    VideoPath = video,
    Origin = SubtitleOrigin.External,
    SubtitlePath = subtitle,
    Language = "eng",
    Key = SubtitleTarget.ExternalKey(video, subtitle)
};

string[] Cues(int count, Lcg rng)
    => Enumerable.Range(0, count).Select(_ => Sentence(rng)).ToArray();

string Sentence(Lcg rng)
{
    var length = 6 + rng.Next(7);
    return string.Join(' ', Enumerable.Range(0, length).Select(_ => vocabulary[rng.Next(vocabulary.Length)]));
}

string[] Reworded(string[] cues, int everyNth)
{
    var rng = new Lcg(9377);
    return cues.Select((c, i) => i % everyNth == 0 ? Sentence(rng) : c).ToArray();
}

static string Reflow(string cue)
{
    var words = cue.Split(' ');
    return string.Join(' ', words.Take(words.Length / 2)) + "\n" + string.Join(' ', words.Skip(words.Length / 2));
}

static string[] Resplit(string[] cues)
{
    var split = new List<string>();

    foreach (var cue in cues)
    {
        var words = cue.Split(' ');
        split.Add(string.Join(' ', words.Take(words.Length / 2)));
        split.Add(string.Join(' ', words.Skip(words.Length / 2)));
    }

    return split.ToArray();
}

// Character confusions a real OCR pass makes, applied to a share of the characters.
static string[] Ocr(string[] cues, double rate)
{
    var rng = new Lcg(2027);
    var confusions = new Dictionary<char, string>
    {
        ['l'] = "I", ['i'] = "l", ['o'] = "0", ['s'] = "5",
        ['e'] = "c", ['c'] = "e", ['n'] = "h", ['m'] = "rn", ['a'] = "o"
    };

    return cues.Select(cue =>
    {
        var builder = new StringBuilder(cue.Length);

        foreach (var c in cue)
        {
            if (rng.NextDouble() < rate && confusions.TryGetValue(c, out var swap))
            {
                builder.Append(swap);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }).ToArray();
}

string Srt(string[] cues, int shiftSeconds = 0)
{
    var builder = new StringBuilder();

    for (var i = 0; i < cues.Length; i++)
    {
        var start = TimeSpan.FromSeconds((i * 3) + shiftSeconds);
        builder.Append(i + 1).Append('\n');
        builder.Append(Stamp(start)).Append(" --> ").Append(Stamp(start + TimeSpan.FromSeconds(2))).Append('\n');
        builder.Append(cues[i]).Append("\n\n");
    }

    return Save(".srt", builder.ToString());
}

string Ass(string[] cues, string styleName, string styleBody)
{
    var builder = new StringBuilder();
    builder.Append("[Script Info]\nScriptType: v4.00+\n\n");
    builder.Append("[V4+ Styles]\n");
    builder.Append("Format: Name,Fontname,Fontsize,PrimaryColour\n");
    builder.Append("Style: ").Append(styleName).Append(',').Append(styleBody).Append('\n');
    builder.Append("\n[Events]\n");
    builder.Append("Format: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text\n");

    for (var i = 0; i < cues.Length; i++)
    {
        var start = TimeSpan.FromSeconds(i * 3);
        builder.Append("Dialogue: 0,").Append(Stamp(start, ass: true)).Append(',')
            .Append(Stamp(start + TimeSpan.FromSeconds(2), ass: true)).Append(',')
            .Append(styleName).Append(",,0,0,0,,")
            .Append(cues[i].Replace("\n", "\\N", StringComparison.Ordinal)).Append('\n');
    }

    return Save(".ass", builder.ToString());
}

string Save(string extension, string body)
{
    var path = Path.Combine(sandbox, Guid.NewGuid().ToString("N") + extension);
    File.WriteAllText(path, body);
    return path;
}

static string Stamp(TimeSpan value, bool ass = false)
    => ass
        ? value.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture)
        : value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

internal sealed class FakeStore : ISyncStore
{
    private readonly List<SyncRecord> _records = [];

    public List<SyncRecord> GetAll() => _records.ToList();

    public SyncRecord? GetById(Guid recordId) => _records.FirstOrDefault(r => r.Id == recordId);

    public List<SyncRecord> GetByItemId(Guid itemId) => _records.Where(r => r.ItemId == itemId).ToList();

    public SyncRecord? GetByTargetKey(Guid itemId, string targetKey)
        => _records.FirstOrDefault(r => r.ItemId == itemId && r.TargetKey == targetKey);

    public List<SyncRecord> GetByStatus(SyncStatus status) => _records.Where(r => r.Status == status).ToList();

    public void Upsert(SyncRecord record)
    {
        _records.RemoveAll(r => r.ItemId == record.ItemId && r.TargetKey == record.TargetKey);
        _records.Add(record);
    }

    public void UpsertMany(IEnumerable<SyncRecord> records)
    {
        foreach (var record in records)
        {
            Upsert(record);
        }
    }

    public void Remove(Guid recordId) => _records.RemoveAll(r => r.Id == recordId);

    public void RemoveMany(IEnumerable<Guid> recordIds)
    {
        foreach (var id in recordIds)
        {
            Remove(id);
        }
    }

    public int ReopenFailed() => 0;

    public int Clear()
    {
        var count = _records.Count;
        _records.Clear();
        return count;
    }

    public void Flush()
    {
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

// A fixed generator, so a fixture scores the same on every machine and every runtime.
internal sealed class Lcg
{
    private uint _state;

    public Lcg(uint seed) => _state = seed;

    public int Next(int bound) => (int)(Step() % (uint)bound);

    public double NextDouble() => Step() / 16777216.0;

    private uint Step()
    {
        _state = (_state * 1664525u) + 1013904223u;
        return _state >> 8;
    }
}
