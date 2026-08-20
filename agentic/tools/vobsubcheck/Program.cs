using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

// Exercises the real VobSubIndex.cs, linked by the csproj.
//
// The case that produced it: "Gravity 2013 1080p BluRay multi-subs" carries 24 language streams in
// one .idx, seconv OCRs all 21,123 images of them, and the 20-minute budget kills it every time.
// Splitting the index to one stream takes English to 1,003 images. See agentic/AUDIT.md, Z4.
//
//   dotnet run --project agentic/tools/vobsubcheck
//   dotnet run --project agentic/tools/vobsubcheck -- <path to a real .idx>

var failures = 0;

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

// A miniature of the real layout: header, then one block per language, comments interleaved.
static string Fixture(params (string Lang, int Index, int Images)[] tracks)
{
    var text = new System.Text.StringBuilder()
        .AppendLine("# VobSub index file, v7")
        .AppendLine("size: 720x480")
        .AppendLine("langidx: 0")
        .AppendLine();

    foreach (var (lang, index, images) in tracks)
    {
        text.AppendLine("# " + lang.ToUpperInvariant());
        text.AppendLine($"id: {lang}, index: {index}");
        text.AppendLine("# alt: something");

        for (var i = 0; i < images; i++)
        {
            text.AppendLine($"timestamp: 00:00:{i:D2}:000, filepos: 000000{i:D3}");
        }

        text.AppendLine();
    }

    return text.ToString();
}

static string? Stage(VobSubStaging staging, string subPath, int streamIndex)
    => staging.StageAsync(subPath, streamIndex, CancellationToken.None).GetAwaiter().GetResult();

static string Write(string content)
{
    var path = Path.Combine(Path.GetTempPath(), "vobsubcheck-" + Guid.NewGuid().ToString("N") + ".idx");
    File.WriteAllText(path, content);
    return path;
}

Console.WriteLine();

var multi = Write(Fixture(("en", 0, 5), ("zh", 1, 3), ("ru", 19, 4)));
var single = Write(Fixture(("en", 0, 6)));
var headerOnly = Write("# VobSub index file, v7\nsize: 720x480\nlangidx: 0\n");

try
{
    Check("every declared stream is reported, with its own index", () =>
    {
        var tracks = VobSubIndex.Read(multi);
        return tracks.Count == 3
               && tracks[0] == new VobSubTrack(0, "en", 5)
               && tracks[1] == new VobSubTrack(1, "zh", 3)
               && tracks[2] == new VobSubTrack(19, "ru", 4)
            ? null
            : "read " + string.Join(", ", tracks);
    });

    // A one-stream index is the common case and must keep flowing through untouched.
    Check("a single-stream index reports one stream", () =>
    {
        var tracks = VobSubIndex.Read(single);
        return tracks.Count == 1 && tracks[0].Images == 6 ? null : "read " + string.Join(", ", tracks);
    });

    Check("an index declaring no stream reports none", () =>
        VobSubIndex.Read(headerOnly).Count == 0 ? null : "reported a stream");

    Check("a missing file reports none rather than throwing", () =>
        VobSubIndex.Read(Path.Combine(Path.GetTempPath(), "no-such-file.idx")).Count == 0
            ? null : "reported a stream");

    // ! The split must carry the header. Without it the converter has no palette or resolution
    //   and reads nothing at all.
    Check("a split index keeps the header and exactly one stream", () =>
    {
        var output = Write(string.Empty);
        if (!VobSubIndex.TryWriteSingle(multi, 1, output))
        {
            return "TryWriteSingle refused";
        }

        var text = File.ReadAllText(output);
        var tracks = VobSubIndex.Read(output);

        return text.Contains("size: 720x480", StringComparison.Ordinal)
               && tracks.Count == 1
               && tracks[0] == new VobSubTrack(1, "zh", 3)
            ? null
            : "wrote " + string.Join(", ", tracks) + (text.Contains("size:", StringComparison.Ordinal) ? string.Empty : ", header lost");
    });

    // The mid-list case, measured against the real file before this was written.
    Check("a stream that is neither first nor last splits out whole", () =>
    {
        var output = Write(string.Empty);
        VobSubIndex.TryWriteSingle(multi, 19, output);
        var tracks = VobSubIndex.Read(output);
        return tracks.Count == 1 && tracks[0] == new VobSubTrack(19, "ru", 4)
            ? null : "wrote " + string.Join(", ", tracks);
    });

    Check("splitting a stream the index does not declare refuses", () =>
        VobSubIndex.TryWriteSingle(multi, 7, Write(string.Empty)) ? "reported success" : null);

    Check("the paired index is found from the .sub beside it", () =>
        VobSubIndex.IndexFor(Path.Combine("d", "movie.sub")).EndsWith("movie.idx", StringComparison.Ordinal)
            ? null : "resolved " + VobSubIndex.IndexFor("movie.sub"));

    // Staging: the converter opens the payload by name beside the index, so the pairing is the
    // whole point. A split index with no payload next to it reads nothing.
    var media = Path.Combine(Path.GetTempPath(), "vobsubcheck-media-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(media);
    var sourceSub = Path.Combine(media, "film.sub");
    File.WriteAllBytes(sourceSub, new byte[4096]);
    File.WriteAllText(Path.Combine(media, "film.idx"), Fixture(("en", 0, 5), ("zh", 1, 3), ("ru", 19, 4)));

    var scratch = Path.Combine(Path.GetTempPath(), "vobsubcheck-scratch-" + Guid.NewGuid().ToString("N"));
    var staging = new VobSubStaging(scratch, NullLogger<VobSubStaging>.Instance);

    Check("staging yields a split index with the payload beside it", () =>
    {
        var staged = Stage(staging,sourceSub, 1);

        if (staged is null)
        {
            return "staging refused";
        }

        var paired = Path.ChangeExtension(staged, ".sub");
        var tracks = VobSubIndex.Read(staged);

        return File.Exists(paired) && tracks.Count == 1 && tracks[0].Index == 1
            ? null
            : $"index {staged}, payload beside it {File.Exists(paired)}, read {string.Join(", ", tracks)}";
    });

    // ! Two streams of one file stage concurrently as separate queue items. Each needs its own
    //   index, and both must resolve a payload.
    Check("two streams of one file stage side by side", () =>
    {
        var first = Stage(staging,sourceSub, 0);
        var second = Stage(staging,sourceSub, 19);

        if (first is null || second is null)
        {
            return "one of them refused";
        }

        return first != second
               && File.Exists(Path.ChangeExtension(first, ".sub"))
               && File.Exists(Path.ChangeExtension(second, ".sub"))
               && VobSubIndex.Read(first)[0].Index == 0
               && VobSubIndex.Read(second)[0].Index == 19
            ? null
            : "the two stagings collided";
    });

    Check("staging a stream the index does not declare refuses", () =>
        Stage(staging, sourceSub, 7) is null ? null : "returned a path");

    Check("staging a file with no payload refuses", () =>
        Stage(staging, Path.Combine(media, "absent.sub"), 0) is null ? null : "returned a path");

    // The payload is copied once and shared, which is what keeps a 24-stream film affordable.
    Check("the payload is staged once for the whole file", () =>
    {
        var copies = Directory.EnumerateFiles(scratch, "source.sub", SearchOption.AllDirectories).Count();
        return copies == 1 ? null : $"found {copies} payload copies, wanted 1";
    });

    // ! A copy here is the 2.9 GB case the shared payload exists to avoid, and it is silent —
    //   the conversion works either way. Reading the payload's bytes through the pair is the tell.
    Check("each stream's payload is a link, not a second copy", () =>
    {
        var staged = Stage(staging, sourceSub, 0);

        if (staged is null)
        {
            return "staging refused";
        }

        var payload = Path.Combine(Path.GetDirectoryName(staged)!, "source.sub");

        using (var handle = new FileStream(payload, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            handle.WriteByte(0x5A);
        }

        return File.ReadAllBytes(Path.ChangeExtension(staged, ".sub"))[0] == 0x5A
            ? null
            : "the pair is a second copy of the payload";
    });

    // ! Staging must keep the sweep off a folder it is about to hand to the converter.
    Check("staging refreshes the folder against the sweep", () =>
    {
        var folder = Path.GetDirectoryName(Stage(staging, sourceSub, 0))!;
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-3));
        Stage(staging, sourceSub, 0);

        return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < TimeSpan.FromMinutes(1)
            ? null
            : "the folder kept its stale timestamp";
    });

    Check("a fresh staging survives the sweep", () =>
    {
        staging.Sweep();
        return Stage(staging, sourceSub, 0) is not null ? null : "swept a live staging";
    });

    // ! An index past the line cap must refuse. A truncated block still reads as a valid
    //   subtitle, so a silent cut ships a track missing its tail.
    Check("an index past the line cap refuses rather than truncating", () =>
    {
        var huge = new System.Text.StringBuilder("id: en, index: 0\n");

        for (var i = 0; i < 500_001; i++)
        {
            huge.Append("timestamp: 00:00:00:000, filepos: 000000000\n");
        }

        var path = Write(huge.ToString());
        return VobSubIndex.Read(path).Count == 0 && !VobSubIndex.TryWriteSingle(path, 0, Write(string.Empty))
            ? null
            : "reported a stream from a truncated read";
    });

    // ! The defect this exists for: every stream of one index shares the payload, so a fingerprint
    //   taken over the payload alone made them identical and one refusal was adopted by all.
    Check("two streams of one payload fingerprint differently", () =>
    {
        var first = FileFingerprint.TryComputeSource(sourceSub, 0);
        var second = FileFingerprint.TryComputeSource(sourceSub, 19);

        return first is not null && second is not null && first != second
            ? null
            : $"first {first ?? "null"}, second {second ?? "null"}";
    });

    Check("a sidecar with no declared stream keeps the whole-file hash", () =>
        FileFingerprint.TryComputeSource(sourceSub, null) == FileFingerprint.TryComputeFull(sourceSub)
            ? null : "the plain path changed shape");

    // ! A null hash must stay null. Suffixing one produces a fingerprint that matches everything.
    Check("an unreadable payload fingerprints as nothing, not as a bare suffix", () =>
        FileFingerprint.TryComputeSource(Path.Combine(media, "absent.sub"), 3) is null
            ? null : "returned a fingerprint for a file that is not there");

    Directory.Delete(media, recursive: true);
    Directory.Delete(scratch, recursive: true);

    // Stages one stream of a real file and prints the path, so the OCR step can be run against it.
    // Windows PowerShell cannot load a net9.0 assembly, so this is how a script reaches the stager.
    if (args.Length == 3 && File.Exists(args[0]) && int.TryParse(args[1], out var wanted))
    {
        var stager = new VobSubStaging(args[2], NullLogger<VobSubStaging>.Instance);
        Console.WriteLine();
        Console.WriteLine(
            stager.StageAsync(args[0], wanted, CancellationToken.None).GetAwaiter().GetResult()
            ?? "staging refused");
    }

    // A real index, when one is offered. Nothing here is fixture-shaped.
    // ! Guarded on the file existing. `dotnet run` leaks its own switches into args.
    else if (args.Length > 0 && File.Exists(args[0]))
    {
        Console.WriteLine();
        var real = VobSubIndex.Read(args[0]);
        Console.WriteLine($"  {args[0]}");
        Console.WriteLine($"  {real.Count} streams, {real.Sum(t => t.Images)} images total");

        foreach (var track in real)
        {
            Console.WriteLine($"    index {track.Index,3}  {track.Language,-4} {track.Images,6} images");
        }
    }
}
finally
{
    foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "vobsubcheck-*.idx"))
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the temp sweeper.
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "vobsubcheck: all checks passed" : $"vobsubcheck: {failures} failed");
return failures == 0 ? 0 : 1;
