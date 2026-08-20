using Jellyfin.Plugin.AutoSubSync.Subtitles;

// Exercises the real OcrReadability.cs against subtitles whose quality is known.
//
// The fixture readings were measured against real subtitles, which are not in the repo. Drop
// <name>.srt files into fixtures/ to run those cases; without them only the synthetic cases run.
// See agentic/AUDIT.md, C1.
//
//   dotnet run --project agentic/tools/ocrcheck
//   dotnet run --project agentic/tools/ocrcheck -- <path to a subtitle>

var failures = 0;
var here = AppContext.BaseDirectory;
var fixtures = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "fixtures"));

bool Present(string name) => File.Exists(Path.Combine(fixtures, name + ".srt"));

var skipped = 0;

void Skip(string name)
{
    Console.WriteLine("  skip  " + name + " (fixture absent)");
    skipped++;
}

void Check(string name, Func<string?> run)
{
    string? problem;

    try
    {
        problem = run();
    }
    catch (Exception ex)
    {
        problem = ex.GetType().Name + ": " + ex.Message;
    }

    Console.WriteLine((problem is null ? "  ok    " : "  FAIL  ") + name);

    if (problem is not null)
    {
        Console.WriteLine("          " + problem);
        failures++;
    }
}

static OcrReading Of(string path) => OcrReadability.Read(path);

static string Describe(OcrReading r)
    => $"{r.Words} words, mean {r.MeanWordLength:F2}, short {r.ShortWordShare:P1}";

// Real subtitles nobody OCR'd. None may ever be called noise.
var real = new[]
{
    "madmen-s02e06", "mpfc-s01e02", "simpsons-s01e10", "tng-s02e02", "twinpeaks-fwwm"
};

// What the converter's default produced. Both are unusable and must be refused.
var noise = new[] { "gravity-isolate-on", "sandakan-isolate-on" };

// The same two streams read with colour isolation off. Imperfect, but words.
var usable = new[] { "gravity-isolate-off", "sandakan-isolate-off" };

Console.WriteLine();

if (args.Length > 0 && File.Exists(args[0]))
{
    var one = Of(args[0]);
    Console.WriteLine($"  {args[0]}");
    Console.WriteLine($"  {Describe(one)}");
    Console.WriteLine($"  judgeable {one.IsJudgeable}, noise {one.IsNoise}");
    Console.WriteLine();
    return 0;
}

foreach (var name in real)
{
    if (!Present(name)) { Skip($"a real subtitle is not called noise: {name}"); continue; }

    Check($"a real subtitle is not called noise: {name}", () =>
    {
        var reading = Of(Path.Combine(fixtures, name + ".srt"));
        return reading is { IsJudgeable: true, IsNoise: false } ? null : Describe(reading);
    });
}

foreach (var name in noise)
{
    if (!Present(name)) { Skip($"unreadable OCR is refused: {name}"); continue; }

    Check($"unreadable OCR is refused: {name}", () =>
    {
        var reading = Of(Path.Combine(fixtures, name + ".srt"));
        return reading.IsNoise ? null : "passed the gate: " + Describe(reading);
    });
}

foreach (var name in usable)
{
    if (!Present(name)) { Skip($"usable OCR is kept: {name}"); continue; }

    Check($"usable OCR is kept: {name}", () =>
    {
        var reading = Of(Path.Combine(fixtures, name + ".srt"));
        return reading is { IsJudgeable: true, IsNoise: false } ? null : Describe(reading);
    });
}

// ! The separation the thresholds sit in. If a change narrows this, the thresholds are guesses.
if (!real.Concat(usable).Concat(noise).All(Present))
{
    Skip("the two populations stay apart");
}
else
{
    Check("the two populations stay apart", () =>
{
    var worstReal = real.Concat(usable).Min(n => Of(Path.Combine(fixtures, n + ".srt")).MeanWordLength);
    var bestNoise = noise.Max(n => Of(Path.Combine(fixtures, n + ".srt")).MeanWordLength);

    return worstReal - bestNoise > 1.0
        ? null
        : $"mean word length: worst real {worstReal:F2} against best noise {bestNoise:F2}";
});
}

// ! A CJK track carries no spaced Latin words. Refusing it for that would drop every
//   Chinese and Japanese subtitle the plugin ever reads.
Check("a CJK track is left unjudged rather than refused", () =>
{
    var lines = Enumerable.Repeat("在敵人的槍口下，我們別無選擇。", 400);
    var reading = OcrReadability.Measure(lines);
    return !reading.IsJudgeable && !reading.IsNoise ? null : Describe(reading);
});

// ! A forced track is a handful of captions. Too little text to judge is not evidence of noise.
Check("a short forced track is left unjudged", () =>
{
    var lines = new[] { "1", "00:00:01,000 --> 00:00:03,000", "They speak Klingon here.", string.Empty };
    var reading = OcrReadability.Measure(lines);
    return !reading.IsJudgeable && !reading.IsNoise ? null : Describe(reading);
});

// ! One signal is not enough. A language of short words sits near the mean-length bound on
//   its own, and refusing it there would drop a subtitle that reads perfectly well.
Check("a track failing only one signal is kept", () =>
{
    // Under the mean bound, but few of its words are short.
    var lowMean = OcrReadability.Measure(Enumerable.Repeat("the cat sat on the mat", 60));

    // Over the short-word share, but its words average long.
    var manyShort = OcrReadability.Measure(Enumerable.Repeat("of to in international extraordinary", 60));

    if (lowMean.MeanWordLength >= OcrReadability.MinimumMeanWordLength
        || lowMean.ShortWordShare > OcrReadability.MaximumShortWordShare)
    {
        return "the low-mean sample does not fail exactly one signal: " + Describe(lowMean);
    }

    if (manyShort.ShortWordShare <= OcrReadability.MaximumShortWordShare
        || manyShort.MeanWordLength < OcrReadability.MinimumMeanWordLength)
    {
        return "the many-short sample does not fail exactly one signal: " + Describe(manyShort);
    }

    return !lowMean.IsNoise && !manyShort.IsNoise
        ? null
        : $"refused on one signal: {Describe(lowMean)} / {Describe(manyShort)}";
});

// A timing line is not text and must never count toward the reading.
Check("timing lines are not counted as words", () =>
{
    var withTimings = OcrReadability.Measure(
        Enumerable.Range(0, 300).SelectMany(i => new[] { "00:00:01,000 --> 00:00:03,000", "steady readable dialogue here" }));
    var without = OcrReadability.Measure(Enumerable.Repeat("steady readable dialogue here", 300));

    return Math.Abs(withTimings.MeanWordLength - without.MeanWordLength) < 0.001
        ? null
        : $"{withTimings.MeanWordLength:F3} against {without.MeanWordLength:F3}";
});

Console.WriteLine();
Console.WriteLine("  --- readings ---");

foreach (var name in real.Concat(usable).Concat(noise).Where(Present))
{
    var reading = Of(Path.Combine(fixtures, name + ".srt"));
    Console.WriteLine($"    {name,-24} {Describe(reading)}{(reading.IsNoise ? "   REFUSED" : string.Empty)}");
}

Console.WriteLine();
var tail = skipped == 0 ? string.Empty : $", {skipped} skipped for absent fixtures";
Console.WriteLine(failures == 0 ? $"ocrcheck: all checks passed{tail}" : $"ocrcheck: {failures} failed{tail}");
return failures == 0 ? 0 : 1;
