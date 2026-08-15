using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

public enum SyncVerdict
{
    Aligned,
    Misaligned,
    Inconclusive
}

public readonly record struct VerificationResult(
    SyncVerdict Verdict,
    int? BestShiftMs,
    int? DriftMs,
    int Windows,
    double Strength);

// The speech onsets read out of one video, and the windows they were read from.
public sealed record AudioSample(
    IReadOnlyList<long> Onsets,
    IReadOnlyList<SyncVerifier.Window> Plan,
    int Windows);

// Scores a finished subtitle against the video's own audio, independently of the sync engine.
public partial class SyncVerifier
{
    private const int MinimumWindows = 4;
    private const int MaximumWindows = 16;
    private const int WindowSeconds = 90;
    private const int MinutesPerWindow = 6;

    // Under this the whole track is cheaper than seeking around it.
    private const long WholeTrackSpanMs = 10 * 60 * 1000;

    private const int StderrKeepChars = 512 * 1024;

    // How far off the speech a subtitle may sit and still count as correct. Subtitles are shown
    // slightly ahead of the line, so the honest baseline is a small positive lead, not zero.
    public const int AlignedWithinMs = 500;

    private const int SweepMs = 4_000;
    private const int StepMs = 25;
    private const int ToleranceMs = 250;
    private const int Bucket = 50;

    // Below these the estimate says more about the sample than about the subtitle.
    private const int MinimumCues = 40;
    private const int MinimumHits = 12;
    private const double MinimumHitShare = 0.25;
    private const double PeakRatio = 1.4;

    // ! Noise offers many near-equal answers, so beating the mean is not enough. The winner has
    //   to beat the best shift that is nowhere near it.
    private const double RivalRatio = 1.25;
    private const int RivalGapMs = 1_000;

    // ! Half a stretched subtitle is smeared across its own half of the error, so the same bar
    //   would refuse every rate error there is.
    private const double HalfRivalRatio = 1.1;

    // Two windows a side is not enough to call a rate error.
    private const int DriftWindows = 6;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SyncVerifier> _logger;

    public SyncVerifier(IMediaEncoder mediaEncoder, ILogger<SyncVerifier> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public Task<VerificationResult> VerifyAsync(
        string videoPath,
        string subtitlePath,
        CancellationToken cancellationToken)
        => VerifyAsync(_mediaEncoder.EncoderPath, videoPath, subtitlePath, cancellationToken);

    internal async Task<VerificationResult> VerifyAsync(
        string ffmpegPath,
        string videoPath,
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        if (Starts(subtitlePath) is not { } starts)
        {
            return Nothing(0);
        }

        var sample = await SampleAsync(ffmpegPath, videoPath, starts, cancellationToken)
            .ConfigureAwait(false);

        return sample is null ? Nothing(0) : Score(sample, starts);
    }

    // The cue start times, in order. Null when there are too few to say anything.
    public static List<long>? Starts(string subtitlePath)
        => SubtitleOffsetProbe.TryReadCues(subtitlePath) is { Count: >= MinimumCues } cues
            ? cues.Select(c => c.StartMs).OrderBy(ms => ms).ToList()
            : null;

    public Task<AudioSample?> SampleAsync(
        string videoPath,
        List<long> starts,
        CancellationToken cancellationToken)
        => SampleAsync(_mediaEncoder.EncoderPath, videoPath, starts, cancellationToken);

    // ! One read of the audio per target. Both checks score against this same sample.
    internal async Task<AudioSample?> SampleAsync(
        string ffmpegPath,
        string videoPath,
        List<long> starts,
        CancellationToken cancellationToken)
    {
        var windows = PlanWindows(starts[0], starts[^1] - starts[0]);
        var onsets = new List<long>();
        var used = 0;

        foreach (var window in windows)
        {
            var found = await OnsetsAsync(ffmpegPath, videoPath, window, cancellationToken)
                .ConfigureAwait(false);

            if (found is null)
            {
                continue;
            }

            used++;
            onsets.AddRange(found);
        }

        return used < Math.Min(MinimumWindows, windows.Count) || onsets.Count == 0
            ? null
            : new AudioSample(onsets, windows, used);
    }

    // How far the cues sit from the speech, and whether that answer holds across the film.
    public static VerificationResult Score(AudioSample sample, List<long> starts)
    {
        if (starts.Count < MinimumCues)
        {
            return Nothing(sample.Windows);
        }

        var whole = Fit(sample.Onsets, sample.Plan, starts, RivalRatio, out var strength);

        // ! Halved, each side is a shorter and weaker sample, so it is only asked at all once
        //   there are windows to spare. Two a side is noise arguing with noise.
        var halves = 0d;
        var drift = sample.Windows >= DriftWindows ? Drift(sample, starts, out halves) : null;

        // ! A rate error can beat the sweep outright: no one shift fits the film, while each
        //   half of it still fits its own. That disagreement is the answer.
        if (drift is { } spread && Math.Abs(spread) > AlignedWithinMs)
        {
            return new VerificationResult(SyncVerdict.Misaligned, whole, drift, sample.Windows, halves);
        }

        if (whole is not { } best)
        {
            return Nothing(sample.Windows);
        }

        return new VerificationResult(
            Math.Abs(best) > AlignedWithinMs ? SyncVerdict.Misaligned : SyncVerdict.Aligned,
            best,
            drift,
            sample.Windows,
            strength);
    }

    // ! A rate error hides from a single shift: right at the start, minutes out at the end.
    private static int? Drift(AudioSample sample, List<long> starts, out double strength)
    {
        strength = 0;

        var half = sample.Plan.Count / 2;
        if (half < 2)
        {
            return null;
        }

        var early = sample.Plan.Take(half).ToList();
        var late = sample.Plan.Skip(sample.Plan.Count - half).ToList();

        var first = Fit(Within(sample.Onsets, early), early, starts, HalfRivalRatio, out var opening);
        var second = Fit(Within(sample.Onsets, late), late, starts, HalfRivalRatio, out var closing);

        // The weaker half is what the answer rests on.
        strength = Math.Min(opening, closing);

        return first is { } a && second is { } b ? b - a : null;
    }

    private static List<long> Within(IReadOnlyList<long> onsets, List<Window> windows)
        => onsets
            .Where(o => windows.Any(w => o >= w.StartMs && o <= w.StartMs + w.LengthMs))
            .ToList();

    private static int? Fit(
        IReadOnlyList<long> onsets,
        IReadOnlyList<Window> windows,
        List<long> starts,
        double rivalRatio,
        out double strength)
    {
        var reachable = starts.Count(start => windows
            .Any(w => start >= w.StartMs - SweepMs && start <= w.StartMs + w.LengthMs + SweepMs));

        if (reachable == 0)
        {
            strength = 0;
            return null;
        }

        return BestShift(onsets, starts, reachable, rivalRatio, out strength);
    }

    private static VerificationResult Nothing(int windows)
        => new(SyncVerdict.Inconclusive, null, null, windows, 0);

    public readonly record struct Window(long StartMs, long LengthMs);

    // Spread across the cues, never the container, so titles and end credits stay out of it.
    internal static List<Window> PlanWindows(long firstCueMs, long spanMs)
    {
        if (spanMs <= WholeTrackSpanMs)
        {
            return [new Window(0, firstCueMs + spanMs + (30 * 1000))];
        }

        var count = (int)Math.Clamp(spanMs / (MinutesPerWindow * 60_000), MinimumWindows, MaximumWindows);
        var length = Math.Min(WindowSeconds * 1000L, spanMs / (count * 3));
        var stride = (spanMs - length) / Math.Max(1, count - 1);

        var windows = new List<Window>(count);
        for (var i = 0; i < count; i++)
        {
            windows.Add(new Window(firstCueMs + (i * stride), length));
        }

        return windows;
    }

    // One window of audio, decoded no further than it has to be.
    private static ProcessStartInfo Reader(string ffmpegPath, string videoPath, Window window)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("info");

        // ! Ahead of -i. Behind it, ffmpeg decodes everything up to the window.
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(Seconds(window.StartMs));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(Seconds(window.LengthMs));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);

        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");

        return startInfo;
    }

    // ! Audio only, mono, 16 kHz. Decoding the video to read its audio costs minutes on HEVC.
    private async Task<List<long>?> OnsetsAsync(
        string ffmpegPath,
        string videoPath,
        Window window,
        CancellationToken cancellationToken)
    {
        var startInfo = Reader(ffmpegPath, videoPath, window);
        startInfo.ArgumentList.Add("-af");

        // ! The downmix belongs inside the graph. As an output option it lands after
        //   silencedetect, which then reads 5.1 and calls a channel bed silence.
        startInfo.ArgumentList.Add("aformat=channel_layouts=mono,silencedetect=noise=-30dB:d=0.35");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        // ! Keeps the whole window's silence lines. The error-report tail cuts them.
        var outcome = await FfmpegProcess
            .RunAsync(startInfo, _logger, cancellationToken, StderrKeepChars)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return null;
        }

        var onsets = new List<long>();
        foreach (Match match in SilenceEndRegex().Matches(outcome.StandardError))
        {
            if (double.TryParse(
                    match.Groups["t"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds))
            {
                onsets.Add(window.StartMs + (long)Math.Round(seconds * 1000));
            }
        }

        return onsets;
    }

    // The shift that puts the most cues on a speech onset. Null when no shift stands out.
    internal static int? BestShift(IReadOnlyList<long> onsets, List<long> starts, int reachable)
        => BestShift(onsets, starts, reachable, RivalRatio, out _);

    internal static int? BestShift(
        IReadOnlyList<long> onsets,
        List<long> starts,
        int reachable,
        double rivalRatio,
        out double strength)
    {
        var buckets = new HashSet<long>();
        foreach (var onset in onsets)
        {
            buckets.Add(onset / Bucket);
        }

        // ! Every shift inside the match tolerance scores the same. Keep them all and take the
        //   middle; the first of them is the edge of that plateau, a quarter second out.
        var best = new List<int>();
        var bestHits = -1;
        long total = 0;
        var samples = 0;
        var sweep = new List<(int Shift, int Hits)>();

        for (var shift = -SweepMs; shift <= SweepMs; shift += StepMs)
        {
            var hits = Hits(buckets, starts, shift);
            total += hits;
            samples++;
            sweep.Add((shift, hits));

            if (hits > bestHits)
            {
                bestHits = hits;
                best.Clear();
            }

            if (hits == bestHits)
            {
                best.Add(shift);
            }
        }

        var answer = best[best.Count / 2];

        // ! The best answer that is nowhere near this one. On noise the two are level.
        var rival = sweep
            .Where(s => Math.Abs(s.Shift - answer) > RivalGapMs)
            .Select(s => s.Hits)
            .DefaultIfEmpty(0)
            .Max();

        strength = bestHits / (double)Math.Max(1, rival);

        // ! Against the cues the windows can reach. A film's other nine tenths were never sampled
        //   and cannot hit at any shift.
        var floor = Math.Max(MinimumHits, (int)(reachable * MinimumHitShare));
        var mean = (double)total / samples;

        if (bestHits < floor || bestHits < mean * PeakRatio || strength < rivalRatio)
        {
            return null;
        }

        return answer;
    }

    private static int Hits(HashSet<long> buckets, List<long> starts, int shift)
    {
        var hits = 0;

        foreach (var start in starts)
        {
            var moved = start + shift;

            for (var offset = -ToleranceMs; offset <= ToleranceMs; offset += Bucket)
            {
                if (buckets.Contains((moved + offset) / Bucket))
                {
                    hits++;
                    break;
                }
            }
        }

        return hits;
    }

    private static string Seconds(long milliseconds)
        => (milliseconds / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_end:\s*(?<t>[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEndRegex();
}
