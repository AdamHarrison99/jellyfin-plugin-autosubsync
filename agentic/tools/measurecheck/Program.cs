// Does SubtitleOffsetProbe.Measure report what the engine actually did? See P4 in AUDIT.md.
//
// Mutation: let KeyFor keep the end timestamp in the key. Every shifted case then falls back to
// the endpoints and the marker cases fail. A real defect in the first draft, caught here.
//
// Given two subtitle paths it measures those instead, for checking against a real library.

using Jellyfin.Plugin.AutoSubSync.Subtitles;

if (args.Length == 2)
{
    var measured = SubtitleOffsetProbe.Measure(args[0], args[1]);
    Console.WriteLine(
        $"constant {measured.ConstantMs?.ToString() ?? "null"}ms  "
        + $"drift {measured.DriftMs?.ToString() ?? "null"}ms  "
        + $"rate {(measured.RateRatio is { } ratio ? ratio.ToString("F5") : "null")}");
    return 0;
}

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "measurecheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);

// Enough cues, spread wide enough, for the fit to be allowed to run.
static List<(long Start, string Text)> Dialogue(int count, long firstMs, long stepMs)
{
    var cues = new List<(long, string)>();
    for (var i = 0; i < count; i++)
    {
        cues.Add((firstMs + (i * stepMs), $"Line number {i} of the conversation."));
    }

    return cues;
}

static List<(long Start, string Text)> Retime(
    List<(long Start, string Text)> cues, long shiftMs, double rate)
{
    var moved = new List<(long, string)>();
    foreach (var cue in cues)
    {
        moved.Add(((long)Math.Round(cue.Start * rate) + shiftMs, cue.Text));
    }

    return moved;
}

var files = 0;

string Srt(List<(long Start, string Text)> cues)
{
    var path = Path.Combine(sandbox, $"cues-{files++}.srt");
    var text = new System.Text.StringBuilder();
    var index = 1;

    foreach (var cue in cues)
    {
        text.Append(index++).Append('\n')
            .Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.Start + 2000)).Append('\n')
            .Append(cue.Text).Append("\n\n");
    }

    File.WriteAllText(path, text.ToString());
    return path;

    static string Stamp(long ms)
        => $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
}

// A case returns null when it passes, or what it saw when it does not.
void Check(string name, Func<string?> body)
{
    string? detail;
    try
    {
        detail = body();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL  {name}\n        threw {ex.GetType().Name}: {ex.Message}");
        failures++;
        return;
    }

    if (detail is not null)
    {
        Console.WriteLine($"  FAIL  {name}\n        {detail}");
        failures++;
    }
}

static bool Near(long? actual, long expected, long tolerance = 60)
    => actual is { } value && Math.Abs(value - expected) <= tolerance;

static string Saw(OffsetChange change)
    => $"constant {change.ConstantMs?.ToString() ?? "null"}, "
       + $"drift {change.DriftMs?.ToString() ?? "null"}, "
       + $"rate {(change.RateRatio is { } r ? r.ToString("F5") : "null")}";

// 200 cues at 3s intervals: a 10-minute span, comfortably over MinimumSpanMs.
var dialogue = Dialogue(200, 30_000, 3_000);

Check("a pure shift is measured as that shift", () =>
{
    var change = SubtitleOffsetProbe.Measure(Srt(dialogue), Srt(Retime(dialogue, 4_000, 1.0)));
    return Near(change.ConstantMs, 4_000) && change.RateRatio is { } r && Math.Abs(r - 1) < 0.001
        ? null : Saw(change);
});

Check("a PAL stretch is measured as its rate, not as a shift", () =>
{
    var after = Retime(dialogue, 0, 25.0 / 23.976);
    var change = SubtitleOffsetProbe.Measure(Srt(dialogue), Srt(after));
    // The stretch is anchored at zero, so the first cue at 30s is itself displaced.
    return change.RateRatio is { } r
           && Math.Abs(r - (25.0 / 23.976)) < 0.002
           && Near(change.ConstantMs, 1_281, 120)
        ? null : Saw(change);
});

Check("a dropped leading marker cue is not read as a shift", () =>
{
    var withMarker = new List<(long, string)> { (41, "25.000") };
    withMarker.AddRange(dialogue);

    // The engine keeps the dialogue where it was and drops the marker.
    var change = SubtitleOffsetProbe.Measure(Srt(withMarker), Srt(dialogue));
    return Near(change.ConstantMs, 0, 120) ? null : Saw(change);
});

Check("Atlantis: a marker plus a real shift reports only the real shift", () =>
{
    var withMarker = new List<(long, string)> { (41, "25.000") };
    withMarker.AddRange(dialogue);

    var change = SubtitleOffsetProbe.Measure(Srt(withMarker), Srt(Retime(dialogue, 1_500, 1.0)));

    // The old measurement reported the 30s gap to the first line of dialogue.
    return Near(change.ConstantMs, 1_500) ? null : Saw(change);
});

Check("a dropped trailing cue does not distort the rate", () =>
{
    var withTail = new List<(long, string)>(dialogue) { (dialogue[^1].Start + 600_000, "www.subs.example") };
    var change = SubtitleOffsetProbe.Measure(Srt(withTail), Srt(Retime(dialogue, 0, 1.0)));
    return change.RateRatio is { } r && Math.Abs(r - 1) < 0.01 ? null : Saw(change);
});

Check("dropped interior cues do not move the measurement", () =>
{
    var thinned = dialogue.Where((_, i) => i % 7 != 0).ToList();
    var change = SubtitleOffsetProbe.Measure(Srt(dialogue), Srt(Retime(thinned, 2_500, 1.0)));
    return Near(change.ConstantMs, 2_500) ? null : Saw(change);
});

Check("repeated text falls back rather than pairing the wrong cues", () =>
{
    var same = Enumerable.Range(0, 200).Select(i => ((long)(30_000 + (i * 3_000)), "Yeah.")).ToList();
    var change = SubtitleOffsetProbe.Measure(Srt(same), Srt(Retime(same, 5_000, 1.0)));

    // No key is unique, so this is the endpoint path; it still has to produce a number.
    return Near(change.ConstantMs, 5_000) ? null : Saw(change);
});

Check("an unmatchable pair still reports a constant, never null", () =>
{
    var other = Dialogue(200, 30_000, 3_000).Select(c => (c.Start, c.Text + " entirely different")).ToList();
    var change = SubtitleOffsetProbe.Measure(Srt(dialogue), Srt(Retime(other, 900, 1.0)));
    return change.ConstantMs is not null ? null : Saw(change);
});

Check("a short subtitle reports a shift but no rate", () =>
{
    var brief = Dialogue(20, 1_000, 500);
    var change = SubtitleOffsetProbe.Measure(Srt(brief), Srt(Retime(brief, 700, 1.0)));
    return Near(change.ConstantMs, 700) && change.RateRatio is null && change.DriftMs is null
        ? null : Saw(change);
});

Check("ASS centiseconds parse to the same answer as SRT milliseconds", () =>
{
    var cues = SubtitleOffsetProbe.TryReadCues(WriteAss(dialogue));
    return cues is { Count: 200 } && cues[0].StartMs == 30_000
        ? null : $"read {cues?.Count} cues, first at {cues?[0].StartMs}";
});

Check("a missing file measures as nothing rather than throwing", () =>
{
    var change = SubtitleOffsetProbe.Measure(
        Path.Combine(sandbox, "absent-a.srt"), Path.Combine(sandbox, "absent-b.srt"));
    return change.ConstantMs is null ? null : Saw(change);
});

string WriteAss(List<(long Start, string Text)> cues)
{
    var path = Path.Combine(sandbox, $"cues-{files++}.ass");
    var text = new System.Text.StringBuilder("[Events]\n");

    foreach (var cue in cues)
    {
        text.Append("Dialogue: 0,").Append(Stamp(cue.Start)).Append(',').Append(Stamp(cue.Start + 2000))
            .Append(",Default,,0,0,0,,").Append(cue.Text).Append('\n');
    }

    File.WriteAllText(path, text.ToString());
    return path;

    static string Stamp(long ms)
        => $"{ms / 3600000}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}";
}

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
    // The sandbox is in the temp directory; leaving it is not a failure.
}

Console.WriteLine(failures == 0 ? "measurecheck: all cases pass" : $"measurecheck: {failures} failed");
return failures == 0 ? 0 : 1;
