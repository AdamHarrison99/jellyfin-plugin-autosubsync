// IDEA-VAD, measured. Scores one subtitle against one video twice over the SAME window plan: once
// on the shipping silencedetect onsets, once on onsets from a real VAD. Everything downstream of
// the onset supply is the shipping SyncVerifier, linked rather than copied, so a verdict change
// here is a verdict change in the plugin.
//
//   dotnet run --project agentic/tools/vadcheck -- --video <p> --subtitle <p> [--shift ms]
//              [--detector webrtc|silero] [--python <p>] [--model <onnx>] [--json <out>]
//              [--gap ms] [--min-speech ms] [--aggressiveness 0-3] [--windows N] [--skip-silence]
//
// --windows overrides the planned count so the "would a denser onset supply survive shorter
// windows?" question can be asked directly; N5 is the standing warning that it might not.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;

var argv = args;

string? Value(string name)
{
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
}

List<string> All(string name)
{
    var found = new List<string>();
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i] == name)
        {
            found.Add(argv[i + 1]);
        }
    }

    return found;
}

var videos = All("--video");
var subtitles = All("--subtitle");

if (videos.Count == 0 || subtitles.Count == 0)
{
    Console.Error.WriteLine("--video and --subtitle are required");
    return 2;
}

var here = AppContext.BaseDirectory;
var ffmpeg = Value("--ffmpeg")
    ?? Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "ffmpeg", "ffmpeg.exe"));
var script = Value("--script")
    ?? Path.GetFullPath(Path.Combine(here, "..", "..", "..", "vad-onsets.py"));
var python = Value("--python") ?? "python";
var detectors = All("--detector");
if (detectors.Count == 0)
{
    detectors.Add("webrtc");
}
var model = Value("--model");
var gap = int.Parse(Value("--gap") ?? "250", CultureInfo.InvariantCulture);
var minSpeech = int.Parse(Value("--min-speech") ?? "100", CultureInfo.InvariantCulture);
var aggressiveness = int.Parse(Value("--aggressiveness") ?? "3", CultureInfo.InvariantCulture);
var threshold = double.Parse(Value("--threshold") ?? "0.5", CultureInfo.InvariantCulture);
var forcedWindows = Value("--windows") is { } w ? int.Parse(w, CultureInfo.InvariantCulture) : (int?)null;
var windowSeconds = int.Parse(Value("--window-seconds") ?? "90", CultureInfo.InvariantCulture);
var raiseSeconds = int.Parse(Value("--raise-seconds") ?? "90", CultureInfo.InvariantCulture);
var jsonOut = Value("--json");
var cacheDir = Value("--cache")
    ?? Path.Combine(Path.GetTempPath(), "vadcheck-flags");
var shifts = All("--shift").Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
shifts.Insert(0, 0);

var rows = new List<Dictionary<string, object?>>();

Console.WriteLine(
    $"{"title",-46} {"applied",8} {"source",-8} {"verdict",-13} {"shift",8} {"drift",8} "
    + $"{"peak",6} {"hits/floor",12} {"onsets",7} {"win",4}");

foreach (var video in videos)
{
    foreach (var original in subtitles)
    {
        foreach (var by in shifts)
        {
            var subtitle = by == 0 ? original : Shifted(original, by);
            var starts = SyncVerifier.Starts(subtitle);

            if (starts is null)
            {
                Console.WriteLine($"{Short(original),-46} {by,8} {"—",-8} too few cues");
                continue;
            }

            var plan = Plan(starts, forcedWindows, windowSeconds, raiseSeconds);

            if (!argv.Contains("--skip-silence"))
            {
                var silence = SilenceOnsets(ffmpeg, video, plan, cacheDir);
                Report(original, video, by, "silence", plan, silence, starts, null);
            }

            var vad = VadOnsets(python, script, ffmpeg, video, plan, detectors, model,
                gap, minSpeech, aggressiveness, threshold, cacheDir);

            foreach (var name in detectors)
            {
                if (vad.TryGetValue(name, out var read))
                {
                    Report(original, video, by, name, plan, read.Onsets, starts, read.SpeechShare);
                }
            }
        }
    }
}

if (jsonOut is not null)
{
    File.WriteAllText(jsonOut, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
}

return 0;

void Report(
    string subtitle,
    string video,
    int by,
    string source,
    List<SyncVerifier.Window> plan,
    List<long> onsets,
    List<long> starts,
    double? speechShare)
{
    var sample = new AudioSample(onsets, plan, plan.Count);
    var result = onsets.Count == 0
        ? new VerificationResult(SyncVerdict.Inconclusive, null, null, plan.Count, 0)
        : SyncVerifier.Score(sample, starts);

    Console.WriteLine(
        $"{Short(subtitle),-46} {by,8} {source,-8} {result.Verdict,-13} "
        + $"{result.BestShiftMs?.ToString(CultureInfo.InvariantCulture) ?? "—",8} "
        + $"{result.DriftMs?.ToString(CultureInfo.InvariantCulture) ?? "—",8} "
        + $"{result.Strength,6:F2} {$"{result.Hits}/{result.Floor}",12} {result.Onsets,7} {plan.Count,4}"
        + (speechShare is { } share ? $"   speech {share,5:P0}" : string.Empty));

    var halves = Halves(plan, onsets, starts);

    rows.Add(new Dictionary<string, object?>
    {
        ["relaxedShiftMs"] = RelaxedPeak(onsets, starts),
        ["earlyShiftMs"] = halves.Early.Shift,
        ["earlyHits"] = halves.Early.Hits,
        ["earlyFloor"] = halves.Early.Floor,
        ["earlyStrength"] = halves.Early.Strength,
        ["lateShiftMs"] = halves.Late.Shift,
        ["lateHits"] = halves.Late.Hits,
        ["lateFloor"] = halves.Late.Floor,
        ["lateStrength"] = halves.Late.Strength,
        ["video"] = video,
        ["subtitle"] = subtitle,
        ["appliedShiftMs"] = by,
        ["source"] = source,
        ["verdict"] = result.Verdict.ToString(),
        ["bestShiftMs"] = result.BestShiftMs,
        ["driftMs"] = result.DriftMs,
        ["strength"] = result.Strength,
        ["hits"] = result.Hits,
        ["floor"] = result.Floor,
        ["onsets"] = result.Onsets,
        ["windows"] = plan.Count,
        ["cues"] = starts.Count,
        ["speechShare"] = speechShare
    });
}

static string Short(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    return name.Length <= 45 ? name : name[..45];
}

// The shipping plan, or the same span replanned at a different window length. --window-seconds
// reproduces what PlanWindows would return if WindowSeconds held that value, incl. the conditional
// raise to DriftWindows. --raise-seconds lowers ONLY the raise threshold, leaving the length cap at
// WindowSeconds → a short title gains six windows of span/18 without taking audio from any title
// that already reaches six. Those are different changes and they must be measured apart.
static List<SyncVerifier.Window> Plan(
    List<long> starts, int? forced, int windowSeconds, int raiseSeconds)
{
    const int MinimumWindows = 4;
    const int MaximumWindows = 16;
    const int MinutesPerWindow = 6;
    const int DriftWindows = 6;
    const long WholeTrackSpanMs = 600_000;

    var first = starts[0];
    var span = starts[^1] - starts[0];

    if (forced is null && windowSeconds == 90 && raiseSeconds == 90)
    {
        return SyncVerifier.PlanWindows(first, span);
    }

    if (forced is null && span <= WholeTrackSpanMs)
    {
        return SyncVerifier.PlanWindows(first, span);
    }

    int count;
    if (forced is { } given)
    {
        count = given;
    }
    else
    {
        count = (int)Math.Clamp(span / (MinutesPerWindow * 60_000), MinimumWindows, MaximumWindows);
        if (count < DriftWindows && span / (DriftWindows * 3) >= raiseSeconds * 1000L)
        {
            count = DriftWindows;
        }
    }

    var length = Math.Min(windowSeconds * 1000L, span / (count * 3));
    var stride = (span - length) / Math.Max(1, count - 1);
    var windows = new List<SyncVerifier.Window>(count);

    for (var i = 0; i < count; i++)
    {
        windows.Add(new SyncVerifier.Window(first + (i * stride), length));
    }

    return windows;
}

// The shipping onset reader, re-expressed here only because SyncVerifier keeps it private. The
// filter graph and the threshold are copied verbatim from OnsetsAsync.
static List<long> SilenceOnsets(
    string ffmpeg,
    string video,
    List<SyncVerifier.Window> plan,
    string cacheDir)
{
    var onsets = new List<long>();
    Directory.CreateDirectory(cacheDir);

    foreach (var window in plan)
    {
        // ! Cached like the VAD flags. Without this the same window is decoded once per injected
        //   shift, and the share is the whole cost of a sweep.
        var stamp = $"{video}|silence|{window.StartMs}|{window.LengthMs}";
        var name = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(stamp)));
        var cached = Path.Combine(cacheDir, name + ".silence.json");

        if (File.Exists(cached))
        {
            onsets.AddRange(JsonSerializer.Deserialize<List<long>>(File.ReadAllText(cached))!);
            continue;
        }

        var found = new List<long>();

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "-nostdin", "-loglevel", "info",
            "-ss", Seconds(window.StartMs), "-t", Seconds(window.LengthMs),
            "-i", video, "-map", "0:a:0", "-vn", "-sn", "-ar", "16000",
            "-af", "aformat=channel_layouts=mono,silencedetect=noise=-30dB:d=0.35",
            "-f", "null", "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var run = Process.Start(startInfo)!;
        var text = run.StandardError.ReadToEnd();
        run.WaitForExit();

        foreach (Match match in Regex.Matches(text, @"silence_end:\s*(?<t>[\d.]+)"))
        {
            if (double.TryParse(match.Groups["t"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds))
            {
                found.Add(window.StartMs + (long)Math.Round(seconds * 1000));
            }
        }

        File.WriteAllText(cached, JsonSerializer.Serialize(found));
        onsets.AddRange(found);
    }

    return onsets;

    static string Seconds(long ms) => (ms / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
}

static Dictionary<string, (List<long> Onsets, double? SpeechShare)> VadOnsets(
    string python,
    string script,
    string ffmpeg,
    string video,
    List<SyncVerifier.Window> plan,
    List<string> detectors,
    string? model,
    int gap,
    int minSpeech,
    int aggressiveness,
    double threshold,
    string cacheDir)
{
    var request = new Dictionary<string, object?>
    {
        ["cacheDir"] = cacheDir,
        ["ffmpeg"] = ffmpeg,
        ["video"] = video,
        ["detectors"] = detectors,
        ["gapMs"] = gap,
        ["minSpeechMs"] = minSpeech,
        ["aggressiveness"] = aggressiveness,
        ["threshold"] = threshold,
        ["windows"] = plan.Select(w => new Dictionary<string, long>
        {
            ["startMs"] = w.StartMs,
            ["lengthMs"] = w.LengthMs
        }).ToList()
    };

    if (model is not null)
    {
        request["model"] = model;
    }

    var startInfo = new ProcessStartInfo(python)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8
    };

    startInfo.ArgumentList.Add(script);

    using var run = Process.Start(startInfo)!;
    run.StandardInput.Write(JsonSerializer.Serialize(request));
    run.StandardInput.Close();

    var stdout = run.StandardOutput.ReadToEnd();
    var stderr = run.StandardError.ReadToEnd();
    run.WaitForExit();

    var read = new Dictionary<string, (List<long>, double?)>(StringComparer.Ordinal);

    if (run.ExitCode != 0 || stdout.Length == 0)
    {
        Console.Error.WriteLine($"  vad failed ({run.ExitCode}): {Tail(stderr)}");
        return read;
    }

    using var document = JsonDocument.Parse(stdout);

    foreach (var entry in document.RootElement.GetProperty("byDetector").EnumerateObject())
    {
        var onsets = entry.Value.GetProperty("onsets").EnumerateArray().Select(e => e.GetInt64()).ToList();
        var frames = entry.Value.GetProperty("frames").GetInt32();
        var speech = entry.Value.GetProperty("speechFrames").GetInt32();
        read[entry.Name] = (onsets, frames == 0 ? null : speech / (double)frames);
    }

    return read;

    static string Tail(string text)
        => text.Length <= 400 ? text.Replace('\n', ' ') : text[^400..].Replace('\n', ' ');
}

// The same cues displaced by a known amount, written where nothing indexes them.
static string Shifted(string path, int byMs)
{
    var written = Path.Combine(Path.GetTempPath(), $"vadshift{byMs}-{Path.GetFileName(path)}");
    var pattern = new Regex(@"(\d{2}):(\d{2}):(\d{2}),(\d{3})");

    var text = pattern.Replace(File.ReadAllText(path), match =>
    {
        var at = (((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60)
            + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) * 60
            + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000
            + int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) + byMs;
        at = Math.Max(0, at);
        return $"{at / 3600000:00}:{at / 60000 % 60:00}:{at / 1000 % 60:00},{at % 1000:000}";
    });

    File.WriteAllText(written, text);
    return written;
}

// Where a source thinks the alignment is when it is ¬required to be conclusive. Runs the shipping
// `BestFit` w/ the two bars it exposes as parameters wound down: reachable 0 drops the hit floor to
// MinimumHits, rivalRatio 0 drops the rival test. PeakRatio still applies ∵ it is a private const →
// a peak is still ¬returned for a flat sweep, which is the honest limit of this measurement.
//
// ! This is a MEASUREMENT of corroboration, ¬a proposed gate. A relaxed peak is weaker evidence
//   than a verdict and must never be read as one on its own.
static int? RelaxedPeak(List<long> onsets, List<long> starts)
    => SyncVerifier.BestFit(onsets, starts, 0, 0.0).Shift;

// Why a drift verdict was or was not reached. `Drift` is private and returns only its answer, so
// the two half-fits are re-run here through the shipping `BestFit` at the shipping half bar.
// ! Only the `reachable` count is re-expressed; the fit and every gate inside it are the real ones.
static (ShiftFit Early, ShiftFit Late) Halves(
    List<SyncVerifier.Window> plan,
    List<long> onsets,
    List<long> starts)
{
    const double HalfRivalRatio = 1.1;
    const int SweepMs = 4_000;

    var half = plan.Count / 2;
    if (half < 2)
    {
        return (default, default);
    }

    var early = plan.Take(half).ToList();
    var late = plan.Skip(plan.Count - half).ToList();

    return (Fit(early), Fit(late));

    ShiftFit Fit(List<SyncVerifier.Window> windows)
    {
        var within = onsets
            .Where(o => windows.Any(w => o >= w.StartMs && o <= w.StartMs + w.LengthMs))
            .ToList();

        var reachable = starts.Count(start => windows
            .Any(w => start >= w.StartMs - SweepMs && start <= w.StartMs + w.LengthMs + SweepMs));

        return reachable == 0
            ? new ShiftFit(null, 0, 0, 0, within.Count)
            : SyncVerifier.BestFit(within, starts, reachable, HalfRivalRatio);
    }
}
