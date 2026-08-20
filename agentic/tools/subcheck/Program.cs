using Jellyfin.Plugin.AutoSubSync.Subtitles;

// Exercises the real SubtitleContent.cs and SdhDetector.cs, linked by the csproj.
var root = Path.Combine(Path.GetTempPath(), "subcheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

var failures = 0;
var checks = 0;

try
{
    RunCueChecks();
    RunParseChecks();
    RunSdhChecks();
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // A leftover temp directory is not a test failure.
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"\nsubcheck: {failures} failure(s)");
    return 1;
}

Console.WriteLine($"subcheck: {checks} checks, all pass");
return 0;

void RunCueChecks()
{
    var cases = new (string Name, string Content, bool Expected)[]
    {
        ("srt with cues", "1\n00:00:01,000 --> 00:00:02,000\nHello.\n", true),
        ("srt empty", string.Empty, false),
        ("srt whitespace only", "\n\n   \n", false),
        ("srt numbers but no timing", "1\n2\n3\n", false),

        // ! The case a size check cannot catch.
        ("ass headers only", "[Script Info]\nTitle: x\n\n[V4+ Styles]\nFormat: Name\n\n[Events]\nFormat: Layer, Text\n", false),
        ("ass with dialogue", "[Events]\nFormat: Layer, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hi\n", true),

        ("vtt with cues", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello.\n", true),
        ("vtt header only", "WEBVTT\n\nNOTE nothing here\n", false),

        // Never block a format we do not understand. ".sub" is one of those now: MicroDVD was
        // dropped with the engine that read it, so it must be waved through, not judged empty.
        ("microdvd is now unknown", "{100}{200}Hello.\n", true),
        ("microdvd empty is now unknown", string.Empty, true),
        ("unknown extension", "anything at all\n", true)
    };

    foreach (var (name, content, expected) in cases)
    {
        var path = Write(name, ExtensionFor(name), content);
        Check($"HasCues({name})", SubtitleContent.HasCues(path), expected);
    }

    Check("HasCues(missing file)", SubtitleContent.HasCues(Path.Combine(root, "nope.srt")), true);
}

void RunParseChecks()
{
    var srt = Write("parse", ".srt", string.Join('\n',
        "1",
        "00:00:01,000 --> 00:00:02,000",
        "First line",
        "second line",
        string.Empty,
        "2",
        "00:00:03,000 --> 00:00:04,000",
        "1998",
        string.Empty,
        "3",
        "00:00:05,000 --> 00:00:06,000",
        "Last.",
        string.Empty));

    var cues = SubtitleContent.ReadCues(srt).ToList();
    Check("srt cue count", cues.Count, 3);
    Check("srt multiline cue", cues.ElementAtOrDefault(0), "First line\nsecond line");

    // ! A numeric line after the timing is dialogue, not the block index.
    Check("srt numeric dialogue kept", cues.ElementAtOrDefault(1), "1998");

    var ass = Write("parse", ".ass", string.Join('\n',
        "[Script Info]",
        "Title: something",
        string.Empty,
        "[Events]",
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
        @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\an8}Hello, there",
        @"Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Line one\NLine two",
        @"Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\fad(200,200)}"));

    var assCues = SubtitleContent.ReadCues(ass).ToList();

    // The third is override tags only, with nothing left after stripping.
    Check("ass cue count", assCues.Count, 2);
    Check("ass override stripped, commas kept", assCues.ElementAtOrDefault(0), "Hello, there");
    Check("ass line break", assCues.ElementAtOrDefault(1), "Line one\nLine two");

    var vtt = Write("parse", ".vtt", string.Join('\n',
        "WEBVTT",
        string.Empty,
        "NOTE this is a comment",
        "spanning two lines",
        string.Empty,
        "STYLE",
        "::cue { color: white }",
        string.Empty,
        "1",
        "00:00:01.000 --> 00:00:02.000 line:0",
        "Real text",
        string.Empty));

    var vttCues = SubtitleContent.ReadCues(vtt).ToList();
    Check("vtt skips note and style blocks", vttCues.Count, 1);
    Check("vtt cue text", vttCues.ElementAtOrDefault(0), "Real text");

    // MicroDVD is no longer a format the plugin reads, so a .sub file parses as nothing.
    var sub = Write("parse", ".sub", "{100}{200}One|Two\n{300}{400}Three\n");
    Check("microdvd is not parsed", SubtitleContent.ReadCues(sub).Count(), 0);
    Check("microdvd has no format key", SubtitleContent.FormatKey(sub), null);
}

void RunSdhChecks()
{
    // Every form seconv's --remove-text-for-hi actually strips, proven against the tool.
    var marked = new[]
    {
        "[door creaks]",
        "(SIGHS)\nI don't know what to do.",
        "MAN: Get out of here.",
        "- JOHN: Hello?\n- WOMAN #2: Over here.",
        ">> NARRATOR: In the beginning.",
        "<i>[explosion]</i>",
        "[MAN SPEAKING SPANISH]",

        // An OCR'd caps label, where the I came back as a lowercase l.
        "ROBlN: Joker!",
        "PENGUlN: Take it to him, man."
    };

    foreach (var cue in marked)
    {
        Check($"marked: {Show(cue)}", SdhDetector.IsMarked(cue), true);
    }

    // seconv leaves music notes alone, so they are not evidence of SDH.
    var clean = new[]
    {
        "Just ordinary dialogue here.",
        "♪ music playing ♪",
        "I said no.",
        "It was 3:30 in the morning.",
        "- Where are you going?\n- Out.",
        "Wait: I never said that.",
        "Dr. Smith will see you now.",

        // Tolerating the OCR l must not open the label rule to ordinary lowercase.
        "All lies: he never came.",

        // Arabic parenthesizes proper nouns. These are Charlie and Winston, not sound effects.
        "(تشارلي)",
        "\"والسيد والسيدة (ريتشموند) من \"سانت لويس"
    };

    foreach (var cue in clean)
    {
        Check($"clean: {Show(cue)}", SdhDetector.IsMarked(cue), false);
    }

    var sdhTrack = Enumerable.Repeat("Ordinary dialogue.", 600)
        .Concat(Enumerable.Repeat("[footsteps approaching]", 60))
        .ToList();
    Check("600 plain + 60 effects is SDH", SdhDetector.Analyze(sdhTrack).IsHearingImpaired, true);

    // The ratio a real lightly-annotated track measures at, taken from Batman: The Movie.
    var sparse = Enumerable.Repeat("Ordinary dialogue.", 874)
        .Concat(Enumerable.Repeat("[a door creaks]", 59))
        .ToList();
    Check("874 plain + 59 effects is SDH", SdhDetector.Analyze(sparse).IsHearingImpaired, true);

    // The two sides of the gap measured across 384 real tracks: the lowest genuine SDH track
    // sits at 2.33%, and the highest track that must not be stripped at 1.42%.
    var lowest = Enumerable.Repeat("Ordinary dialogue.", 1927)
        .Concat(Enumerable.Repeat("[Telephone Rings]", 46))
        .ToList();
    Check("1927 plain + 46 effects is SDH", SdhDetector.Analyze(lowest).IsHearingImpaired, true);

    var highest = Enumerable.Repeat("Ordinary dialogue.", 1600)
        .Concat(Enumerable.Repeat("[ OXYGEN LEVEL, CRITICAL ]", 23))
        .ToList();
    Check("1600 plain + 23 effects is not SDH", SdhDetector.Analyze(highest).IsHearingImpaired, false);

    // A handful of parenthetical asides across a full movie is not SDH.
    var asides = Enumerable.Repeat("Ordinary dialogue.", 700)
        .Concat(Enumerable.Repeat("He said (and I quote) it was fine.", 8))
        .ToList();
    Check("700 plain + 8 asides is not SDH", SdhDetector.Analyze(asides).IsHearingImpaired, false);

    // A short signs track with a few notes must not clear the bar on ratio alone.
    var signs = Enumerable.Repeat("Sign text.", 26)
        .Concat(Enumerable.Repeat("(TL note: this is a pun)", 4))
        .ToList();
    Check("26 signs + 4 notes is not SDH", SdhDetector.Analyze(signs).IsHearingImpaired, false);

    Check("empty track is not SDH", SdhDetector.Analyze(Array.Empty<string>()).IsHearingImpaired, false);

    var short8 = new[]
    {
        "[door creaks]", "MAN: Get out of here.", "(SIGHS)\nI don't know.",
        "- JOHN: Hello?", ">> NARRATOR: In the beginning.", "♪ music playing ♪",
        "He said (and I quote) it was fine.", "Just ordinary dialogue here."
    };
    var result = SdhDetector.Analyze(short8);
    Check("crafted file marked count", result.MarkedCueCount, 6);
    Check("crafted file is SDH", result.IsHearingImpaired, true);
}

string ExtensionFor(string name)
{
    if (name.StartsWith("ass", StringComparison.Ordinal)) { return ".ass"; }
    if (name.StartsWith("vtt", StringComparison.Ordinal)) { return ".vtt"; }
    if (name.StartsWith("microdvd", StringComparison.Ordinal)) { return ".sub"; }
    if (name.StartsWith("unknown", StringComparison.Ordinal)) { return ".xyz"; }
    return ".srt";
}

string Write(string name, string extension, string content)
{
    var path = Path.Combine(root, name.Replace(' ', '-') + "-" + checks + extension);
    File.WriteAllText(path, content);
    return path;
}

void Check<T>(string label, T actual, T expected)
{
    checks++;
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        Console.Error.WriteLine($"FAIL  {label} = {Show(actual)}, want {Show(expected)}");
        failures++;
    }
}

static string Show(object? value)
    => value is null ? "null" : value.ToString()!.Replace("\n", "\\n", StringComparison.Ordinal);
