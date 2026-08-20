// Does the audio check plan a sane set of windows, and does it recover a shift it is shown?
//
// Mutation: let PlanWindows use the container span instead of the cue span. The last window then
// lands in the end credits, where there is no dialogue, and the fit loses a quarter of its hits.
//
// Mutation: drop the PeakRatio test in BestShift. The noise case then reports a confident shift
// off a flat sweep, which is how a correct subtitle would get refused.
//
// Mutation: gate the coarse drift on windows read rather than windows planned. A six-window plan
// with only four windows read then fits three windows a side over sparse onsets and carries it.
//
// Mutation: let the coarse drift reach the drift-Misaligned branch. Simpsons S01E10 reads -3125 at
// four windows and would be called misaligned, and would also lose the voice-detection second pass.
//
// Given media it runs the real check instead:
//
//   dotnet run --project agentic/tools/verifycheck -- --video <path> --subtitle <path> [...]

using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Services;
using Microsoft.Extensions.Logging.Abstractions;

if (Array.IndexOf(args, "--plan") >= 0)
{
    return PlanOnly(args);
}

if (Array.IndexOf(args, "--video") >= 0)
{
    return await RealMedia(args).ConfigureAwait(false);
}

var failures = 0;

void Check(string name, Func<string?> body)
{
    string? failure;
    try
    {
        failure = body();
    }
    catch (Exception ex)
    {
        failure = ex.Message;
    }

    Console.WriteLine(failure is null ? $"  ok    {name}" : $"  FAIL  {name}: {failure}");
    if (failure is not null)
    {
        failures++;
    }
}

static long Minutes(double count) => (long)(count * 60_000);

// Speech onsets a correct subtitle would sit on, with the odd unspoken noise between them.
static List<long> OnsetsFor(List<long> starts, long shiftMs, int strayEvery = 0)
{
    var onsets = new List<long>();
    var index = 0;

    foreach (var start in starts)
    {
        onsets.Add(start + shiftMs + ((index % 3) - 1) * 20);

        if (strayEvery > 0 && index % strayEvery == 0)
        {
            onsets.Add(start + shiftMs + 7_337);
        }

        index++;
    }

    return onsets;
}

// Uneven gaps, as dialogue has. Evenly spaced cues alias against evenly spaced onsets.
static List<long> Cues(int count, long firstMs, long stepMs)
{
    var starts = new List<long>(count);
    var at = firstMs;
    var jitter = new Random(4711);

    for (var i = 0; i < count; i++)
    {
        starts.Add(at);
        at += stepMs + jitter.NextInt64(-stepMs / 2, stepMs * 2);
    }

    return starts;
}

Console.WriteLine("Window planning");

Check("a short film is read in one pass rather than seeked around", () =>
{
    var windows = SyncVerifier.PlanWindows(Minutes(0.5), Minutes(8));
    return windows is { Count: 1 } && windows[0].StartMs == 0
        ? null : $"planned {windows.Count} windows starting at {windows[0].StartMs}";
});

Check("that one pass reaches past the last cue", () =>
{
    var windows = SyncVerifier.PlanWindows(Minutes(0.5), Minutes(8));
    return windows[0].LengthMs >= Minutes(8.5)
        ? null : $"covers only {windows[0].LengthMs}ms";
});

Check("a feature is sampled, not read whole", () =>
{
    var windows = SyncVerifier.PlanWindows(Minutes(2), Minutes(100));
    var sampled = windows.Sum(w => w.LengthMs);
    return windows.Count is >= 4 and <= 16 && sampled <= Minutes(25)
        ? null : $"{windows.Count} windows totalling {sampled}ms";
});

Check("windows never overlap", () =>
{
    var windows = SyncVerifier.PlanWindows(Minutes(2), Minutes(100));
    for (var i = 1; i < windows.Count; i++)
    {
        var previousEnd = windows[i - 1].StartMs + windows[i - 1].LengthMs;
        if (windows[i].StartMs < previousEnd)
        {
            return $"window {i} starts at {windows[i].StartMs}, inside the one ending at {previousEnd}";
        }
    }

    return null;
});

Check("the first window starts at the first cue, not at the container", () =>
{
    var windows = SyncVerifier.PlanWindows(Minutes(4), Minutes(100));
    return windows[0].StartMs == Minutes(4) ? null : $"started at {windows[0].StartMs}";
});

Check("the last window ends inside the dialogue", () =>
{
    var first = Minutes(2);
    var span = Minutes(100);
    var windows = SyncVerifier.PlanWindows(first, span);
    var end = windows[^1].StartMs + windows[^1].LengthMs;
    return end <= first + span ? null : $"ran {end - (first + span)}ms past the last cue";
});

Check("a very long recording stays capped at sixteen windows", () =>
{
    var windows = SyncVerifier.PlanWindows(0, Minutes(600));
    return windows.Count == 16 ? null : $"planned {windows.Count}";
});

Check("a span just over the whole-track limit still gets four windows", () =>
{
    var windows = SyncVerifier.PlanWindows(0, Minutes(11));
    return windows.Count == 4 ? null : $"planned {windows.Count}";
});

// A half-hour episode is the case drift used to go unmeasured on.
Check("a span that can afford six full windows gets six, so drift is measurable", () =>
{
    var windows = SyncVerifier.PlanWindows(0, Minutes(30));
    return windows.Count == 6 && windows[0].LengthMs == 90_000
        ? null
        : $"planned {windows.Count} of {windows[0].LengthMs}ms";
});

// ! The raise is only ever free. Buying windows with window length is what broke the check.
Check("a span too short for six full windows keeps four long ones", () =>
{
    var windows = SyncVerifier.PlanWindows(0, Minutes(23));
    return windows.Count == 4 && windows[0].LengthMs == 90_000
        ? null
        : $"planned {windows.Count} of {windows[0].LengthMs}ms";
});

Console.WriteLine("Shift fitting");

Check("a subtitle already on the speech reports no movement", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, 0), starts, starts.Count);
    return shift is { } found && Math.Abs(found) <= 50 ? null : $"reported {shift?.ToString() ?? "null"}";
});

Check("a subtitle running early reports how far it must move later", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, 1_490), starts, starts.Count);
    return shift is { } found && Math.Abs(found - 1_490) <= 50
        ? null : $"reported {shift?.ToString() ?? "null"}, wanted 1490";
});

Check("a subtitle running late reports a negative movement", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, -800), starts, starts.Count);
    return shift is { } found && Math.Abs(found + 800) <= 50
        ? null : $"reported {shift?.ToString() ?? "null"}, wanted -800";
});

Check("non-speech noise between the lines does not move the answer", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, 600, strayEvery: 2), starts, starts.Count);
    return shift is { } found && Math.Abs(found - 600) <= 50
        ? null : $"reported {shift?.ToString() ?? "null"}, wanted 600";
});

Check("onsets unrelated to the cues report nothing rather than a guess", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var random = new Random(20260815);
    var onsets = new List<long>();
    for (var i = 0; i < 400; i++)
    {
        onsets.Add(60_000 + random.NextInt64(0, 480_000));
    }

    var shift = SyncVerifier.BestShift(onsets, starts, starts.Count);
    return shift is null ? null : $"reported {shift}ms off noise";
});

Check("a handful of onsets is not enough to refuse a subtitle", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, 1_500).Take(10).ToList(), starts, starts.Count);
    return shift is null ? null : $"reported {shift}ms off ten onsets";
});

// A laugh bed leaves only the lines that open after a real pause. The floor used to be a share
// of the cue count, which those titles cannot reach however well they are aligned.
Check("few onsets still measure when they all agree", () =>
{
    var starts = Cues(500, 60_000, 4_000);
    var sparse = starts.Where((_, i) => i % 6 == 0).ToList();
    var shift = SyncVerifier.BestShift(OnsetsFor(sparse, 600), starts, starts.Count);
    return shift is { } found && Math.Abs(found - 600) <= 50
        ? null : $"reported {shift?.ToString() ?? "null"}, wanted 600";
});

// ! The other half of that change. Scarce onsets must not become a cheap way past the gates.
Check("few onsets agreeing with nothing are still refused", () =>
{
    var starts = Cues(500, 60_000, 4_000);
    var random = new Random(20260817);
    var onsets = new List<long>();
    for (var i = 0; i < 60; i++)
    {
        onsets.Add(60_000 + random.NextInt64(0, 2_000_000));
    }

    var shift = SyncVerifier.BestShift(onsets, starts, starts.Count);
    return shift is null ? null : $"reported {shift}ms off sixty unrelated onsets";
});

Check("a shift past the sweep is not reported as a small one", () =>
{
    var starts = Cues(120, 60_000, 4_000);
    var shift = SyncVerifier.BestShift(OnsetsFor(starts, 30_000), starts, starts.Count);
    return shift is null || Math.Abs(shift.Value) >= 3_500
        ? null : $"reported {shift}ms for a 30s displacement";
});

Console.WriteLine("Scoring");

// One sample of a two-hour film, planned the way the verifier plans it.
static AudioSample SampleFor(List<long> starts, long shiftMs, double rate = 1.0)
{
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);
    var onsets = new List<long>();

    foreach (var start in starts)
    {
        var at = (long)(start * rate) + shiftMs;
        if (plan.Any(w => at >= w.StartMs && at <= w.StartMs + w.LengthMs))
        {
            onsets.Add(at);
        }
    }

    return new AudioSample(onsets, plan, plan.Count);
}

Check("a subtitle on the speech is scored aligned", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 200), starts);
    return result.Verdict == SyncVerdict.Aligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a subtitle a second off the speech is refused", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 1_000), starts);
    return result.Verdict == SyncVerdict.Misaligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

// The bound is centred on the authored lead, so it is not symmetric about zero.
Check("the aligned bound sits around the authored lead, not around zero", () =>
{
    var wrong = new List<string>();
    var lead = SyncVerifier.TypicalLeadMs;
    var bound = SyncVerifier.AlignedWithinMs;

    if (!SyncVerifier.IsAligned(lead)) { wrong.Add("the lead itself"); }
    if (!SyncVerifier.IsAligned(lead + bound)) { wrong.Add($"{lead + bound}"); }
    if (SyncVerifier.IsAligned(lead + bound + 1)) { wrong.Add($"{lead + bound + 1}"); }
    if (!SyncVerifier.IsAligned(lead - bound)) { wrong.Add($"{lead - bound}"); }
    if (SyncVerifier.IsAligned(lead - bound - 1)) { wrong.Add($"{lead - bound - 1}"); }

    return wrong.Count == 0 ? null : $"judged wrongly: {string.Join(", ", wrong)}";
});

Check("a subtitle 450 ms behind the speech is refused", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 450), starts);
    return result.Verdict == SyncVerdict.Misaligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a subtitle 350 ms behind the speech is still within the lead's spread", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 350), starts);
    return result.Verdict == SyncVerdict.Aligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a subtitle 100 ms ahead of the speech is refused", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, -100), starts);
    return result.Verdict == SyncVerdict.Misaligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a rate error is refused even though it starts on the speech", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 0, 1.0004), starts);
    return result.Verdict == SyncVerdict.Misaligned && result.DriftMs is not null
        ? null : $"scored {result.Verdict} at {result.BestShiftMs} drifting {result.DriftMs}";
});

// Drift is late minus early, so the authored lead is in both halves and cancels.
Check("a drift inside the raw bound is not refused, though it exceeds the centred one", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var result = SyncVerifier.Score(SampleFor(starts, 0, 1.00005), starts);

    if (result.DriftMs is not { } drift)
    {
        return "measured no drift";
    }

    var between = Math.Abs(drift) > SyncVerifier.AlignedWithinMs
        && Math.Abs(drift) <= SyncVerifier.DriftWithinMs;

    return between && result.Verdict != SyncVerdict.Misaligned
        ? null
        : $"drifting {drift}, scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a flat sweep is not refused off two halves that each guessed", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var random = new Random(20260816);
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);
    var onsets = new List<long>();

    foreach (var window in plan)
    {
        for (var i = 0; i < 200; i++)
        {
            onsets.Add(window.StartMs + random.NextInt64(0, window.LengthMs));
        }
    }

    var result = SyncVerifier.Score(new AudioSample(onsets, plan, plan.Count), starts);
    return result.Verdict != SyncVerdict.Misaligned
        ? null : $"refused off noise at {result.BestShiftMs} drifting {result.DriftMs}";
});

Check("too few cues to say anything is inconclusive, not misaligned", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var sample = SampleFor(starts, 1_000);
    var result = SyncVerifier.Score(sample, starts.Take(10).ToList());
    return result.Verdict == SyncVerdict.Inconclusive ? null : $"scored {result.Verdict}";
});

Console.WriteLine();
Console.WriteLine("The coarse drift");

// The same jittered cue pattern over a chosen span, so the window plan is the one under test.
static List<long> CuesOver(long spanMs, int count)
{
    var raw = Cues(count, 60_000, 5_000);
    var span = raw[^1] - raw[0];

    return raw.Select(s => 60_000 + ((s - raw[0]) * spanMs / span)).ToList();
}

// Onsets sitting on the cues of one half of a four-window plan, at a shift of that half's own.
static AudioSample Halved(List<long> starts, long earlyShiftMs, long? lateShiftMs, int lateEvery)
{
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);
    var half = plan.Count / 2;
    var onsets = new List<long>();

    for (var i = 0; i < starts.Count; i++)
    {
        var early = starts[i] + earlyShiftMs;
        if (plan.Take(half).Any(w => early >= w.StartMs && early <= w.StartMs + w.LengthMs))
        {
            onsets.Add(early);
        }

        if (lateShiftMs is not { } by || i % lateEvery != 0)
        {
            continue;
        }

        var late = starts[i] + by;
        if (plan.Skip(half).Any(w => late >= w.StartMs && late <= w.StartMs + w.LengthMs))
        {
            onsets.Add(late);
        }
    }

    onsets.Sort();
    return new AudioSample(onsets, plan, plan.Count);
}

Check("a four-window title the check calls aligned is released on a flat coarse drift", () =>
{
    var starts = CuesOver(Minutes(16), 400);
    var sample = SampleFor(starts, 200);
    var result = SyncVerifier.Score(sample, starts);

    if (sample.Plan.Count != 4) { return $"planned {sample.Plan.Count} windows, not four"; }
    if (result.CoarseDriftMs is not { } coarse) { return $"measured no coarse drift, scored {result.Verdict}"; }

    return result.Verdict == SyncVerdict.Aligned
        && Math.Abs(coarse) <= SyncVerifier.CoarseDriftWithinMs
        && SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"scored {result.Verdict} at {result.BestShiftMs}, coarse {coarse}";
});

Check("a four-window title carrying a real rate error is not released", () =>
{
    var starts = CuesOver(Minutes(16), 400);
    var sample = SampleFor(starts, 0, 1.00125);
    var result = SyncVerifier.Score(sample, starts);

    if (result.CoarseDriftMs is not { } coarse) { return $"measured no coarse drift, scored {result.Verdict}"; }

    return Math.Abs(coarse) > SyncVerifier.CoarseDriftWithinMs
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {coarse}, scored {result.Verdict} at {result.BestShiftMs}";
});

// 600 ms end to end reads as roughly 400 over the two-thirds baseline: inside 500, outside 300.
Check("a rate error the drift bound would have admitted is not released", () =>
{
    var starts = CuesOver(Minutes(16), 400);
    var sample = SampleFor(starts, 0, 1.000625);
    var result = SyncVerifier.Score(sample, starts);

    if (result.CoarseDriftMs is not { } coarse) { return $"measured no coarse drift, scored {result.Verdict}"; }

    return Math.Abs(coarse) > SyncVerifier.CoarseDriftWithinMs
        && Math.Abs(coarse) <= SyncVerifier.DriftWithinMs
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {coarse}, scored {result.Verdict} at {result.BestShiftMs}";
});

// No measurement, no release: the case 15 of 40 correct titles landed on when this was measured.
Check("a four-window title whose late half supplied nothing is refused, not released", () =>
{
    var starts = CuesOver(Minutes(16), 400);
    var result = SyncVerifier.Score(Halved(starts, 200, null, 1), starts);

    return result.CoarseDriftMs is null && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, scored {result.Verdict} at {result.BestShiftMs}";
});

// ! The coarse reading may never produce a verdict. This is the Simpsons S01E10 case.
Check("a large coarse drift leaves an aligned title aligned and releases nothing", () =>
{
    var starts = CuesOver(Minutes(16), 400);
    var result = SyncVerifier.Score(Halved(starts, 200, 2_000, 3), starts);

    if (result.CoarseDriftMs is not { } coarse) { return $"measured no coarse drift, scored {result.Verdict}"; }

    return Math.Abs(coarse) > SyncVerifier.CoarseDriftWithinMs
        && result.Verdict == SyncVerdict.Aligned
        && result.DriftMs is null
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {coarse}, scored {result.Verdict} at {result.BestShiftMs} drifting {result.DriftMs}";
});

Check("a six-window title carries no coarse drift and is judged on the drift test", () =>
{
    var starts = CuesOver(Minutes(40), 900);
    var sample = SampleFor(starts, 200);
    var result = SyncVerifier.Score(sample, starts);

    if (sample.Plan.Count < SyncVerifier.DriftWindows) { return $"planned {sample.Plan.Count} windows"; }

    return result.CoarseDriftMs is null
        && result.DriftMs is not null
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, drift {result.DriftMs}, scored {result.Verdict}";
});

// Windows planned, not windows read. Gated on the latter this would fit three a side on sparse onsets.
Check("a six-window plan with four windows read carries no coarse drift", () =>
{
    var starts = CuesOver(Minutes(40), 900);
    var sample = SampleFor(starts, 200);
    var partial = new AudioSample(sample.Onsets, sample.Plan, SyncVerifier.DriftWindows - 2);
    var result = SyncVerifier.Score(partial, starts);

    return result.CoarseDriftMs is null
        && result.DriftMs is null
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, drift {result.DriftMs}";
});

Check("a title short enough for one whole-track window measures no coarse drift", () =>
{
    var starts = CuesOver(Minutes(8), 200);
    var sample = SampleFor(starts, 200);
    var result = SyncVerifier.Score(sample, starts);

    if (sample.Plan.Count != 1) { return $"planned {sample.Plan.Count} windows, not one"; }

    return result.CoarseDriftMs is null && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, scored {result.Verdict}";
});

// Drift is late minus early, so the authored lead is in both halves and cancels out of the bound.
Check("the coarse bound is raw, and nothing but an aligned verdict releases", () =>
{
    var wrong = new List<string>();
    var bound = SyncVerifier.CoarseDriftWithinMs;

    static VerificationResult Reading(SyncVerdict verdict, int? coarse)
        => new(verdict, 200, null, 4, 1.5, 30, 12, 90, coarse);

    if (!SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Aligned, bound))) { wrong.Add($"{bound}"); }
    if (!SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Aligned, -bound))) { wrong.Add($"{-bound}"); }
    if (SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Aligned, bound + 1))) { wrong.Add($"{bound + 1}"); }
    if (SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Aligned, SyncVerifier.TypicalLeadMs + bound)))
    {
        wrong.Add("the lead plus the bound");
    }

    if (SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Inconclusive, 0))) { wrong.Add("an inconclusive verdict"); }
    if (SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Misaligned, 0))) { wrong.Add("a misaligned verdict"); }
    if (SyncVerifier.ReleasedByCoarseDrift(Reading(SyncVerdict.Aligned, null))) { wrong.Add("no coarse reading at all"); }

    return wrong.Count == 0 ? null : $"released wrongly: {string.Join(", ", wrong)}";
});

// A window plan's worth of onsets that agree with nothing: what an unmeasurable title looks like.
static AudioSample Noise(List<SyncVerifier.Window> plan)
{
    var random = new Random(20260818);
    var onsets = new List<long>();

    foreach (var window in plan)
    {
        for (var i = 0; i < 200; i++)
        {
            onsets.Add(window.StartMs + random.NextInt64(0, window.LengthMs));
        }
    }

    return new AudioSample(onsets, plan, plan.Count);
}

// Only what the windows actually reach, as a real read would supply.
static List<long> Within(List<long> onsets, List<SyncVerifier.Window> plan)
    => onsets.Where(o => plan.Any(w => o >= w.StartMs && o <= w.StartMs + w.LengthMs)).ToList();

Console.WriteLine();
Console.WriteLine("Voice-detection fallback");

// Onsets the detector would supply, and a record of whether it was asked at all.
var detector = new RecordingDetector();
var verifier = new SyncVerifier(null!, NullLogger<SyncVerifier>.Instance, detector);

// ! The whole safety argument rests on this: silence saying Misaligned ends it. A disjunction
//   here is what would let the weaker of two readings write a subtitle.
Check("a refused subtitle is never handed to the detector", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    detector.Reset(OnsetsFor(starts, 0));

    var result = verifier
        .ScoreAsync("video.mkv", SampleFor(starts, 1_000), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (detector.Calls != 0)
    {
        return "the detector was consulted on a Misaligned verdict";
    }

    return result.Verdict == SyncVerdict.Misaligned ? null : $"scored {result.Verdict}";
});

Check("an aligned subtitle is never handed to the detector", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    detector.Reset(OnsetsFor(starts, 3_000));

    var result = verifier
        .ScoreAsync("video.mkv", SampleFor(starts, 200), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (detector.Calls != 0)
    {
        return "the detector was consulted on an Aligned verdict";
    }

    return result.Verdict == SyncVerdict.Aligned ? null : $"scored {result.Verdict}";
});

Check("a verdict the silence could not reach is settled by the detector", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);

    // Onsets the detector reads cleanly, over windows the silence read as noise.
    detector.Reset(Within(OnsetsFor(starts, 0), plan));

    var result = verifier
        .ScoreAsync("video.mkv", Noise(plan), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (detector.Calls != 1)
    {
        return $"the detector was consulted {detector.Calls} times";
    }

    return result.Verdict == SyncVerdict.Aligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a detector that reaches no verdict either leaves the refusal standing", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);

    detector.Reset(Noise(plan).Onsets.ToList());

    var result = verifier
        .ScoreAsync("video.mkv", Noise(plan), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    return result.Verdict == SyncVerdict.Inconclusive ? null : $"scored {result.Verdict}";
});

Check("a detector that answers nothing at all leaves the refusal standing", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);

    detector.Reset(null);

    var result = verifier
        .ScoreAsync("video.mkv", Noise(plan), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    return result.Verdict == SyncVerdict.Inconclusive ? null : $"scored {result.Verdict}";
});

// ! A subtitle the detector reads as misaligned is refused, ¬left inconclusive. The fallback is a
//   check, not a permission-granter.
Check("the detector can refuse as well as accept", () =>
{
    var starts = Cues(1400, 60_000, 5_000);
    var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);

    detector.Reset(Within(OnsetsFor(starts, 1_500), plan));

    var result = verifier
        .ScoreAsync("video.mkv", Noise(plan), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    return result.Verdict == SyncVerdict.Misaligned ? null : $"scored {result.Verdict} at {result.BestShiftMs}";
});

Console.WriteLine();
Console.WriteLine("The detector as a coarse reading");

// A four-window title the whole-track fit places but the half fits cannot: the late half holds
// nothing, so the release condition has nothing to read.
static List<long> CoarseBlind() => CuesOver(Minutes(16), 400);

Check("an aligned title the coarse fit could not read is handed to the detector", () =>
{
    var starts = CoarseBlind();
    var sample = Halved(starts, 200, null, 1);
    detector.Reset(OnsetsFor(starts, 200));

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (detector.Calls != 1) { return $"the detector was consulted {detector.Calls} times"; }
    if (result.CoarseDriftMs is not { } coarse) { return "the detector's reading was not taken"; }

    return result.Verdict == SyncVerdict.Aligned && SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {coarse}, scored {result.Verdict} at {result.BestShiftMs}";
});

// ! The one that matters: a short plan is the shape the coarse pass consults the detector on, and
//   a refused title must not become one of them.
Check("a refused four-window title is never handed to the detector", () =>
{
    var starts = CoarseBlind();
    var sample = Halved(starts, 1_000, null, 1);
    detector.Reset(OnsetsFor(starts, 200));

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (sample.Plan.Count != 4) { return $"planned {sample.Plan.Count} windows, not four"; }

    return detector.Calls == 0 && result.Verdict == SyncVerdict.Misaligned
        ? null
        : $"the detector was consulted {detector.Calls} times, scored {result.Verdict}";
});

// ! The safety argument from the other side: the detector may supply a reading the silence
//   lacked, never a verdict the silence already reached.
Check("a detector that reads the title as misaligned leaves the aligned verdict standing", () =>
{
    var starts = CoarseBlind();
    var sample = Halved(starts, 200, null, 1);
    detector.Reset(OnsetsFor(starts, 2_500));

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    return result.Verdict == SyncVerdict.Aligned
        && result.CoarseDriftMs is null
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, scored {result.Verdict} at {result.BestShiftMs}";
});

Check("a detector that measures no coarse drift either leaves the refusal standing", () =>
{
    var starts = CoarseBlind();
    var sample = Halved(starts, 200, null, 1);
    detector.Reset(Halved(starts, 200, null, 1).Onsets.ToList());

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    return result.CoarseDriftMs is null && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, scored {result.Verdict}";
});

// The bound is the bound, whichever detector supplied the number.
Check("a detector coarse reading past the bound does not release", () =>
{
    var starts = CoarseBlind();
    var sample = Halved(starts, 200, null, 1);
    detector.Reset(Halved(starts, 200, 2_000, 3).Onsets.ToList());

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (result.CoarseDriftMs is not { } coarse) { return "the detector's reading was not taken"; }

    return Math.Abs(coarse) > SyncVerifier.CoarseDriftWithinMs
        && !SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {coarse}, scored {result.Verdict}";
});

Check("an aligned title that already read its coarse drift is never handed to the detector", () =>
{
    var starts = CoarseBlind();
    detector.Reset(OnsetsFor(starts, 200));

    var result = verifier
        .ScoreAsync("video.mkv", SampleFor(starts, 200), starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (detector.Calls != 0) { return "the detector was consulted on a reading already taken"; }

    return result.CoarseDriftMs is not null && SyncVerifier.ReleasedByCoarseDrift(result)
        ? null
        : $"coarse {result.CoarseDriftMs}, scored {result.Verdict}";
});

// ! A six-window plan is judged by the drift test and carries no coarse reading by design. Asking
//   the detector for one is a second decode for a value nothing reads.
Check("a six-window aligned title is never handed to the detector", () =>
{
    var starts = CuesOver(Minutes(40), 900);
    var sample = SampleFor(starts, 200);
    detector.Reset(OnsetsFor(starts, 200));

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (sample.Plan.Count < SyncVerifier.DriftWindows) { return $"planned {sample.Plan.Count} windows"; }

    return detector.Calls == 0 && result.Verdict == SyncVerdict.Aligned
        ? null
        : $"the detector was consulted {detector.Calls} times, scored {result.Verdict}";
});

Check("a whole-track aligned title is never handed to the detector", () =>
{
    var starts = CuesOver(Minutes(8), 200);
    var sample = SampleFor(starts, 200);
    detector.Reset(OnsetsFor(starts, 200));

    var result = verifier
        .ScoreAsync("video.mkv", sample, starts, CancellationToken.None)
        .GetAwaiter().GetResult();

    if (sample.Plan.Count / 2 >= 2) { return $"planned {sample.Plan.Count} windows, not a whole track"; }

    return detector.Calls == 0 && result.Verdict == SyncVerdict.Aligned
        ? null
        : $"the detector was consulted {detector.Calls} times, scored {result.Verdict}";
});
Console.WriteLine();
Console.WriteLine("The payload seam");

// ! The payload dispatches on the first argument. A global option in front of it hands the whole
//   call to the upstream parser, which has never heard of this subcommand.
Check("every planned window is named on the command line, subcommand first", () =>
{
    var windows = new List<VadWindow> { new(0, 90_000), new(540_000, 90_000) };
    var invocation = AssyArgumentBuilder.BuildVad("assy-cli", "ffmpeg", "movie.mkv", windows);
    var args = string.Join(' ', invocation.Arguments);

    if (invocation.Arguments[0] != "vad" || invocation.Arguments[1] != "movie.mkv")
    {
        return $"built '{args}'";
    }

    foreach (var wanted in new[] { "--ffmpeg ffmpeg", "--window 0:90000", "--window 540000:90000", "--json" })
    {
        if (!args.Contains(wanted, StringComparison.Ordinal))
        {
            return $"'{wanted}' is missing from '{args}'";
        }
    }

    return null;
});

Check("the detector's answer is read back off standard output", () =>
{
    var parsed = AssyVadOnsets.Parse(
        """{"ok": true, "onsets": [100, 2500, 9000], "windowsRead": 4, "windowsPlanned": 4}""");

    return parsed is { Onsets.Count: 3, Windows: 4 } && parsed.Value.Onsets[2] == 9000
        ? null
        : $"read {parsed?.Onsets.Count.ToString() ?? "nothing"}";
});

// ! Every one of these is a title the check would otherwise decide on a stream it never got.
Check("an unusable answer is read as no answer", () =>
{
    var rejected = new[]
    {
        """{"ok": false, "onsets": [100, 200], "windowsRead": 4}""",
        """{"ok": true, "onsets": [], "windowsRead": 4}""",
        """{"ok": true, "onsets": [100], "windowsRead": 0}""",
        """{"ok": true}""",
        "not json at all",
        "",
    };

    foreach (var answer in rejected)
    {
        if (AssyVadOnsets.Parse(answer) is not null)
        {
            return $"accepted '{answer}'";
        }
    }

    return null;
});

Console.WriteLine(failures == 0 ? "verifycheck: all cases pass" : $"verifycheck: {failures} failed");
return failures == 0 ? 0 : 1;

// Window planning alone, no audio decoded. Sweeps a library cheaply for the titles a planning
// change would move, so the expensive runs go only where something actually differs.
static int PlanOnly(string[] argv)
{
    var subtitles = argv
        .Select((value, index) => (value, index))
        .Where(a => a.index > 0 && argv[a.index - 1] == "--subtitle")
        .Select(a => a.value);

    foreach (var subtitle in subtitles)
    {
        var starts = SyncVerifier.Starts(subtitle);

        if (starts is null || starts.Count < 2)
        {
            Console.WriteLine($"{"unreadable",-48}{Path.GetFileName(subtitle)}");
            continue;
        }

        var span = starts[^1] - starts[0];
        var windows = SyncVerifier.PlanWindows(starts[0], span);
        var drift = windows.Count >= SyncVerifier.DriftWindows ? "measurable" : "unmeasured";

        Console.WriteLine(
            $"span {span / 60000.0,5:F1} min  {windows.Count,2} win x {windows[0].LengthMs / 1000.0,5:F1}s  "
            + $"drift {drift,-11}{Path.GetFileName(subtitle)}");
    }

    return 0;
}

// The shipping check, over the vendored ffmpeg, against files whose state is already known.
static async Task<int> RealMedia(string[] argv)
{
    var ffmpeg = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ffmpeg", "ffmpeg.exe");

    // --vad <assy-cli> runs the shipping fallback against the real payload, which is the only way
    // to see on real media what the second pass does with a title the first cannot measure.
    var payload = All(argv, "--vad").FirstOrDefault();
    var detector = payload is null
        ? null
        : new PayloadDetector(Path.GetFullPath(payload), Path.GetFullPath(ffmpeg));

    var verifier = new SyncVerifier(null!, NullLogger<SyncVerifier>.Instance, detector);

    // A known displacement applied to a real subtitle, so the answer is checkable.
    var shifts = All(argv, "--shift").Select(int.Parse).ToList();
    shifts.Insert(0, 0);

    foreach (var video in All(argv, "--video"))
    {
        foreach (var original in All(argv, "--subtitle"))
        {
            foreach (var by in shifts)
            {
                var subtitle = by == 0 ? original : Shifted(original, by);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = await verifier
                    .VerifyAsync(Path.GetFullPath(ffmpeg), video, subtitle, CancellationToken.None)
                    .ConfigureAwait(false);
                clock.Stop();

                Console.WriteLine(
                    $"{result.Verdict,-13} {result.BestShiftMs?.ToString() ?? "—",8}ms  "
                    + $"drift {result.DriftMs?.ToString() ?? "—",6}ms  "
                    + $"peak {result.Strength,5:F2}x  "
                    + $"{result.Hits,4} hits / {result.Floor,4} floor  {result.Onsets,5} onsets  "
                    + $"{result.Windows,2} windows  {clock.ElapsedMilliseconds,6}ms  "
                    + $"{(by == 0 ? "as shipped" : $"moved {by}ms"),-16} {Path.GetFileName(original)}");
            }
        }
    }

    if (Array.IndexOf(argv, "--correlate") >= 0)
    {
        foreach (var video in All(argv, "--video"))
        {
            foreach (var subtitle in All(argv, "--subtitle"))
            {
                foreach (var by in shifts)
                {
                    Correlate(Path.GetFullPath(ffmpeg), video, by == 0 ? subtitle : Shifted(subtitle, by), by);
                }
            }
        }
    }

    if (Array.IndexOf(argv, "--flux") >= 0)
    {
        foreach (var video in All(argv, "--video"))
        {
            foreach (var subtitle in All(argv, "--subtitle"))
            {
                foreach (var by in shifts)
                {
                    Flux(Path.GetFullPath(ffmpeg), video, by == 0 ? subtitle : Shifted(subtitle, by), by);
                }
            }
        }
    }

    if (Array.IndexOf(argv, "--profile") >= 0)
    {
        foreach (var video in All(argv, "--video"))
        {
            foreach (var subtitle in All(argv, "--subtitle"))
            {
                Profile(Path.GetFullPath(ffmpeg), video, subtitle);
            }
        }
    }

    return 0;

    // The same cues, displaced by a known amount, written where nothing indexes them.
    static string Shifted(string path, int byMs)
    {
        var written = Path.Combine(Path.GetTempPath(), $"shift{byMs}-{Path.GetFileName(path)}");
        var pattern = new System.Text.RegularExpressions.Regex(@"(\d{2}):(\d{2}):(\d{2}),(\d{3})");

        var text = pattern.Replace(File.ReadAllText(path), match =>
        {
            var at = (((int.Parse(match.Groups[1].Value) * 60) + int.Parse(match.Groups[2].Value)) * 60
                + int.Parse(match.Groups[3].Value)) * 1000 + int.Parse(match.Groups[4].Value) + byMs;
            at = Math.Max(0, at);
            return $"{at / 3600000:00}:{at / 60000 % 60:00}:{at / 1000 % 60:00},{at % 1000:000}";
        });

        File.WriteAllText(written, text);
        return written;
    }

    // What the fit actually sees: how many cues land on a speech onset at each shift.
    static void Profile(string ffmpeg, string video, string subtitle)
    {
        var starts = Jellyfin.Plugin.AutoSubSync.Subtitles.SubtitleOffsetProbe.TryReadCues(subtitle)
            ?.Select(c => c.StartMs).OrderBy(ms => ms).ToList() ?? [];

        if (starts.Count == 0)
        {
            Console.WriteLine($"  {Path.GetFileName(subtitle)}: no cues");
            return;
        }

        var windows = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);
        var onsets = new List<long>();
        var inWindow = 0;

        foreach (var window in windows)
        {
            var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                Arguments = $"-nostdin -hide_banner -ss {window.StartMs / 1000.0:F3} -t "
                    + $"{window.LengthMs / 1000.0:F3} -i \"{video}\" -map 0:a:0 -vn -sn -ar 16000 "
                    + $"-af aformat=channel_layouts=mono,silencedetect=noise=-30dB:d={Environment.GetEnvironmentVariable("VC_D") ?? "0.35"} -f null -",
                RedirectStandardError = true,
                UseShellExecute = false
            })!;

            var text = run.StandardError.ReadToEnd();
            run.WaitForExit();

            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text, @"silence_end:\s*([\d.]+)"))
            {
                onsets.Add(window.StartMs + (long)Math.Round(double.Parse(m.Groups[1].Value) * 1000));
            }

            inWindow += starts.Count(s => s >= window.StartMs && s <= window.StartMs + window.LengthMs);
        }

        // Only cues that open a line after a pause. Mid-conversation cues have no onset to hit.
        var gap = int.Parse(Environment.GetEnvironmentVariable("VC_GAP") ?? "0");
        var opening = starts.Where((v, i) => i == 0 || v - starts[i - 1] >= gap).ToList();

        Console.WriteLine(
            $"  {Path.GetFileName(subtitle)}: {starts.Count} cues ({opening.Count} opening), "
            + $"{inWindow} inside the windows, {onsets.Count} onsets over {windows.Count} windows");

        starts = opening;
        inWindow = starts.Count(s2 => windows.Any(w => s2 >= w.StartMs && s2 <= w.StartMs + w.LengthMs));
        var buckets = onsets.Select(o => o / 50).ToHashSet();

        Strength(buckets, starts, inWindow, windows.Count);

        for (var shift = -3_000; shift <= 3_000; shift += 250)
        {
            var hits = starts.Count(s => Enumerable
                .Range(-5, 11)
                .Any(k => buckets.Contains((s + shift + (k * 50)) / 50)));
            Console.WriteLine($"    {shift,6}ms  {hits,5}  {new string('#', hits * 60 / Math.Max(1, inWindow))}");
        }
    }

    // The whole speech envelope against the whole subtitle envelope, rather than their edges.
    static void Correlate(string ffmpeg, string video, string subtitle, int by)
    {
        const int Step = 50;
        const int Sweep = 4_000;

        var cues = ReadCues(subtitle);
        if (cues.Count == 0)
        {
            Console.WriteLine($"  {Path.GetFileName(subtitle)}: no cues");
            return;
        }

        var windows = SyncVerifier.PlanWindows(cues[0].Start, cues[^1].Start - cues[0].Start);
        var sweep = new int[(Sweep * 2 / Step) + 1];
        var speechTotal = 0;

        // ! Raw overlap carries a baseline the size of the speech itself, which buries the peak.
        //   These accumulate a correlation coefficient instead: excess over chance, normalized.
        var samples = 0L;
        var speechSum = 0L;
        var cueSum = new long[sweep.Length];
        var product = new long[sweep.Length];

        // ! The bar is set against the title's own level. One window is not a stable estimate of
        //   that — a quiet scene and a loud one differ by 20dB.
        var db = windows.Average(w => MeanVolume(ffmpeg, video, w))
            + double.Parse(Environment.GetEnvironmentVariable("VC_OVER") ?? "12");

        foreach (var window in windows)
        {
            var length = (int)(window.LengthMs / Step);
            var speech = new bool[length];
            Array.Fill(speech, true);

            foreach (var (from, to) in Silences(ffmpeg, video, window, db))
            {
                for (var i = Math.Max(0, (int)(from / Step)); i < Math.Min(length, (int)(to / Step)); i++)
                {
                    speech[i] = false;
                }
            }

            speechTotal += speech.Count(on => on);
            samples += length;
            speechSum += speech.Count(on => on);

            for (var s = 0; s < sweep.Length; s++)
            {
                var shift = -Sweep + (s * Step);
                var hits = 0;
                var covered = 0;

                foreach (var cue in cues)
                {
                    var from = cue.Start + shift - window.StartMs;
                    var to = cue.End + shift - window.StartMs;
                    if (to < 0 || from > window.LengthMs)
                    {
                        continue;
                    }

                    for (var i = Math.Max(0, (int)(from / Step)); i < Math.Min(length, (int)(to / Step)); i++)
                    {
                        covered++;
                        if (speech[i])
                        {
                            hits++;
                        }
                    }
                }

                sweep[s] += hits;
                cueSum[s] += covered;
                product[s] += hits;
            }
        }

        double R(int s)
        {
            double n = samples;
            double sx = speechSum;
            double sy = cueSum[s];
            var numerator = (n * product[s]) - (sx * sy);
            var denominator = Math.Sqrt(((n * sx) - (sx * sx)) * ((n * sy) - (sy * sy)));
            return denominator > 0 ? numerator / denominator : 0;
        }

        var correlation = Enumerable.Range(0, sweep.Length).Select(R).ToArray();
        var rPeak = correlation.Max();
        var rAt = -Sweep + (Array.IndexOf(correlation, rPeak) * Step);
        var rRival = correlation
            .Where((_, s) => Math.Abs(-Sweep + (s * Step) - rAt) > 1_000)
            .DefaultIfEmpty(0)
            .Max();

        var peak = sweep.Max();
        var at = -Sweep + (Array.IndexOf(sweep, peak) * Step);
        var mean = sweep.Average();
        var rival = sweep
            .Where((_, s) => Math.Abs(-Sweep + (s * Step) - at) > 1_000)
            .DefaultIfEmpty(0)
            .Max();

        var deviation = Math.Sqrt(sweep.Average(v => (v - mean) * (v - mean)));
        var z = deviation > 0 ? (peak - mean) / deviation : 0;
        var rivalZ = deviation > 0 ? (rival - mean) / deviation : 0;

        Console.WriteLine(
            $"  correlate {at,6}ms  z {z,5:F2}  rival z {rivalZ,5:F2}  /rival {peak / (double)Math.Max(1, rival),5:F2}  "
            + $"{windows.Count,2} windows  {db,6:F1}dB  speech {speechTotal * Step / 1000,4}s  "
            + $"{(by == 0 ? "as shipped" : $"moved {by}ms"),-16} {Path.GetFileName(subtitle)}");

        Console.WriteLine(
            $"    normalized {rAt,6}ms  r {rPeak,5:F3}  rival r {rRival,5:F3}  "
            + $"margin {rPeak - rRival,5:F3}  /rival {rPeak / Math.Max(0.001, rRival),5:F2}");
    }

    // ! Onsets read as energy transients rather than as silence boundaries. A mix with no gaps
    //   between the lines still steps up in level when a voice starts over it.
    static void Flux(string ffmpeg, string video, string subtitle, int by)
    {
        var starts = SyncVerifier.Starts(subtitle);
        if (starts is null)
        {
            Console.WriteLine($"  flux: too few cues in {Path.GetFileName(subtitle)}");
            return;
        }

        var plan = SyncVerifier.PlanWindows(starts[0], starts[^1] - starts[0]);
        var onsets = new List<long>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var window in plan)
        {
            onsets.AddRange(FluxOnsets(ffmpeg, video, window));
        }

        clock.Stop();
        var result = SyncVerifier.Score(new AudioSample(onsets, plan, plan.Count), starts);

        Console.WriteLine(
            $"  flux {result.Verdict,-13} {result.BestShiftMs?.ToString() ?? "—",8}ms  "
            + $"drift {result.DriftMs?.ToString() ?? "—",6}ms  peak {result.Strength,5:F2}x  "
            + $"{plan.Count,2} windows  {onsets.Count,5} onsets  {clock.ElapsedMilliseconds,6}ms  "
            + $"{(by == 0 ? "as shipped" : $"moved {by}ms"),-16} {Path.GetFileName(subtitle)}");
    }

    static List<long> FluxOnsets(string ffmpeg, string video, SyncVerifier.Window window)
    {
        var rise = double.Parse(Environment.GetEnvironmentVariable("VC_RISE") ?? "6");
        var look = long.Parse(Environment.GetEnvironmentVariable("VC_LOOK") ?? "100");
        var gap = long.Parse(Environment.GetEnvironmentVariable("VC_GAP") ?? "250");
        var band = (Environment.GetEnvironmentVariable("VC_BAND") ?? "1") == "1";

        var levels = Levels(ffmpeg, video, window, band);
        var onsets = new List<long>();

        if (Environment.GetEnvironmentVariable("VC_DEBUG") == "1")
        {
            var span0 = levels.Count > 1 ? levels[^1].At - levels[0].At : 0;
            Console.WriteLine(
                $"    debug window {window.StartMs}: {levels.Count} levels over {span0}ms"
                + (levels.Count > 0
                    ? $", dB {levels.Min(l => l.Db):F1}..{levels.Max(l => l.Db):F1}"
                    : string.Empty));
        }

        if (levels.Count < 8)
        {
            return onsets;
        }

        // Frames arrive at the decoder's own rate, so the lookback is counted in frames.
        var span = Math.Max(1, levels.Count - 1);
        var interval = Math.Max(1, (levels[^1].At - levels[0].At) / span);
        var back = (int)Math.Max(1, look / interval);

        var climb = new double[levels.Count];
        for (var i = back; i < levels.Count; i++)
        {
            climb[i] = levels[i].Db - levels[i - back].Db;
        }

        long? last = null;
        for (var i = back; i < levels.Count; i++)
        {
            if (climb[i] < rise)
            {
                continue;
            }

            // Only the crest of a rise, so one line start is not read as a dozen.
            var crest = true;
            for (var j = Math.Max(0, i - back); j < Math.Min(climb.Length, i + back); j++)
            {
                if (climb[j] > climb[i])
                {
                    crest = false;
                    break;
                }
            }

            if (!crest || (last is { } previous && levels[i].At - previous < gap))
            {
                continue;
            }

            last = levels[i].At;
            onsets.Add(window.StartMs + levels[i].At);
        }

        return onsets;
    }

    static List<(long At, double Db)> Levels(
        string ffmpeg,
        string video,
        SyncVerifier.Window window,
        bool band)
    {
        var filters = "aformat=channel_layouts=mono,"
            + (band ? "highpass=f=200,lowpass=f=3400," : string.Empty)
            + "astats=metadata=1:reset=1,"
            + "ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-";

        var startInfo = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", (window.StartMs / 1000.0).ToString("F3"),
            "-t", (window.LengthMs / 1000.0).ToString("F3"),
            "-i", video, "-map", "0:a:0", "-vn", "-sn", "-ar", "16000",

            // ! The metadata print owns stdout, so the muxer needs a sink of its own. Windows
            //   only, like the rest of the media harnesses.
            "-af", filters, "-f", "null", "NUL"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var run = System.Diagnostics.Process.Start(startInfo)!;
        var discard = run.StandardError.ReadToEndAsync();
        var text = run.StandardOutput.ReadToEnd();
        run.WaitForExit();
        discard.Wait();

        var levels = new List<(long, double)>();
        long at = -1;

        foreach (var line in text.Split('\n'))
        {
            var time = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:(-?[\d.]+)");
            if (time.Success)
            {
                at = (long)Math.Round(double.Parse(time.Groups[1].Value) * 1000);
                continue;
            }

            var level = System.Text.RegularExpressions.Regex.Match(line, @"RMS_level=(-?[\d.]+|-?inf)");
            if (level.Success && at >= 0)
            {
                var text2 = level.Groups[1].Value;

                // Digital silence prints as -inf and would swamp every difference around it.
                var db = text2.EndsWith("inf", StringComparison.Ordinal) ? -90 : double.Parse(text2);
                levels.Add((at, Math.Max(-90, db)));
                at = -1;
            }
        }

        return levels;
    }

    static double MeanVolume(string ffmpeg, string video, SyncVerifier.Window window)
    {
        var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            Arguments = $"-nostdin -hide_banner -ss {window.StartMs / 1000.0:F3} -t "
                + $"{window.LengthMs / 1000.0:F3} -i \"{video}\" -map 0:a:0 -vn -sn -ar 16000 "
                + "-af aformat=channel_layouts=mono,volumedetect -f null -",
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        var text = run.StandardError.ReadToEnd();
        run.WaitForExit();

        var match = System.Text.RegularExpressions.Regex.Match(text, @"mean_volume:\s*(-?[\d.]+) dB");
        return match.Success ? double.Parse(match.Groups[1].Value) : -42;
    }

    static List<(long From, long To)> Silences(
        string ffmpeg,
        string video,
        SyncVerifier.Window window,
        double db)
    {
        var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            Arguments = $"-nostdin -hide_banner -ss {window.StartMs / 1000.0:F3} -t "
                + $"{window.LengthMs / 1000.0:F3} -i \"{video}\" -map 0:a:0 -vn -sn -ar 16000 "
                + "-af aformat=channel_layouts=mono,silencedetect=noise="
                + db.ToString("F1") + "dB:d="
                + (Environment.GetEnvironmentVariable("VC_D") ?? "0.2") + " -f null -",
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        var text = run.StandardError.ReadToEnd();
        run.WaitForExit();

        var spans = new List<(long, long)>();
        long? open = 0;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"silence_(?<edge>start|end):\s*(?<t>-?[\d.]+)"))
        {
            var at = (long)Math.Round(double.Parse(m.Groups["t"].Value) * 1000);

            if (m.Groups["edge"].Value == "start")
            {
                open = at;
            }
            else if (open is { } from)
            {
                spans.Add((from, at));
                open = null;
            }
        }

        if (open is { } last)
        {
            spans.Add((last, window.LengthMs));
        }

        return spans;
    }

    static List<(long Start, long End)> ReadCues(string path)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");

        return pattern.Matches(File.ReadAllText(path))
            .Select(m =>
            {
                long At(int g) => ((((long.Parse(m.Groups[g].Value) * 60)
                    + long.Parse(m.Groups[g + 1].Value)) * 60) + long.Parse(m.Groups[g + 2].Value)) * 1000
                    + long.Parse(m.Groups[g + 3].Value);

                return (At(1), At(5));
            })
            .OrderBy(c => c.Item1)
            .ToList();
    }

    // How much the winning shift stands out, over the sweep the shipping fit actually walks.
    static void Strength(HashSet<long> buckets, List<long> starts, int reachable, int windows)
    {
        var hits = new List<(int Shift, int Hits)>();
        for (var shift = -4_000; shift <= 4_000; shift += 25)
        {
            hits.Add((shift, starts.Count(s => Enumerable
                .Range(-5, 11)
                .Any(k => buckets.Contains((s + shift + (k * 50)) / 50)))));
        }

        var peak = hits.MaxBy(h => h.Hits);
        var mean = hits.Average(h => h.Hits);
        var median = hits.Select(h => h.Hits).Order().ElementAt(hits.Count / 2);

        // The best answer the sweep offers that is not the winner. Noise has many near-equals.
        var rival = hits.Where(h => Math.Abs(h.Shift - peak.Shift) > 1_000).Max(h => h.Hits);

        Console.WriteLine(
            $"    peak {peak.Hits} at {peak.Shift}ms  mean {mean:F1}  median {median}  rival {rival}  "
            + $"| /mean {peak.Hits / mean:F2}  /median {peak.Hits / (double)Math.Max(1, median):F2}  "
            + $"/rival {peak.Hits / (double)Math.Max(1, rival):F2}  "
            + $"share {peak.Hits / (double)Math.Max(1, reachable):P0}  {windows} windows");
    }

    static List<string> All(string[] argv, string name)
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
}

// Stands in for the payload's voice detector: answers with what it was handed, and counts being
// asked at all, which is what the short-circuit cases test.
internal sealed class RecordingDetector : ISpeechOnsetSource
{
    private List<long>? _onsets;

    public int Calls { get; private set; }

    public void Reset(List<long>? onsets)
    {
        _onsets = onsets;
        Calls = 0;
    }

    public Task<SpeechOnsets?> ReadAsync(
        string videoPath,
        IReadOnlyList<SyncVerifier.Window> windows,
        CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult<SpeechOnsets?>(
            _onsets is null ? null : new SpeechOnsets(_onsets, windows.Count));
    }
}

// The shipping fallback wired to the real payload: shipping argv in, shipping parser out. Only
// the spawn is the harness's own, and AssyCliRunner is what does that in the plugin.
internal sealed class PayloadDetector : ISpeechOnsetSource
{
    private readonly string _exe;
    private readonly string _ffmpeg;

    public PayloadDetector(string exe, string ffmpeg)
    {
        _exe = exe;
        _ffmpeg = ffmpeg;
    }

    public Task<SpeechOnsets?> ReadAsync(
        string videoPath,
        IReadOnlyList<SyncVerifier.Window> windows,
        CancellationToken cancellationToken)
    {
        var planned = windows.Select(w => new VadWindow(w.StartMs, w.LengthMs)).ToList();
        var invocation = AssyArgumentBuilder.BuildVad(_exe, _ffmpeg, videoPath, planned);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"  vad exited {process.ExitCode}: {stderr.Trim()}");
            return Task.FromResult<SpeechOnsets?>(null);
        }

        var parsed = AssyVadOnsets.Parse(stdout);
        Console.WriteLine(
            parsed is { } speech
                ? $"  vad: {speech.Onsets.Count} onsets over {speech.Windows} windows"
                : "  vad: no usable answer");

        return Task.FromResult(parsed);
    }
}
