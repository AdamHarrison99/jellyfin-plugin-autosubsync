// The verify step's gate methods, against records whose history is known.
//
//   dotnet run --project .\agentic\tools\orchestratorcheck
//
// SyncOrchestrator cannot be constructed in a harness: fifteen dependencies, most of them
// Jellyfin services. Its decisions are static and pure, and those are what this links.
//
// Mutations that must fail a case here:
//   - StillOurOutput dropping the fingerprint half        -> a replaced subtitle counts as ours
//   - StillOurOutput dropping the backup/Created half     -> every record counts as ours
//   - MinimumWouldNowSync reading AppliedOffsetMs again    -> a demoted row reopens for ever
//   - ToleranceWouldNowSync judging a magnitude            -> a stored refusal churns

using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;

var failures = 0;
var root = Path.Combine(Path.GetTempPath(), "orchestratorcheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    Run();
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // A leftover scratch directory is not a failure.
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "orchestratorcheck: all cases pass" : $"orchestratorcheck: {failures} failed");
return failures == 0 ? 0 : 1;

void Run()
{
    Console.WriteLine("Is this row still ours?");

    Check("a retimed row whose file is untouched is still ours", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);

        return SyncOrchestrator.StillOurOutput(record, target, target.SubtitlePath)
            ? null
            : "the row was not recognised";
    });

    Check("a created row whose source is untouched is still ours", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Created, backup: false);

        return SyncOrchestrator.StillOurOutput(record, target, target.SubtitlePath)
            ? null
            : "the row was not recognised";
    });

    // ! Retimed is 0, the enum's default. A row the plugin never placed reads as Retimed, and
    //   testing provenance alone would call every record in the store ours.
    Check("a row the plugin never placed is not ours, though its provenance reads Retimed", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: false);

        if (record.Provenance != SubtitleProvenance.Retimed) { return "the default provenance moved"; }

        return SyncOrchestrator.StillOurOutput(record, target, target.SubtitlePath)
            ? "a record with no backup and no Created provenance counted as ours"
            : null;
    });

    Check("a subtitle replaced since the sync is not ours", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        File.WriteAllText(target.SubtitlePath!, "1\r\n00:00:09,000 --> 00:00:11,000\r\nsomeone else's file\r\n");

        return SyncOrchestrator.StillOurOutput(record, target, target.SubtitlePath)
            ? "a replaced subtitle counted as ours"
            : null;
    });

    Check("a replaced video is not ours", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        File.WriteAllBytes(target.VideoPath, new byte[9000]);

        return SyncOrchestrator.StillOurOutput(record, target, target.SubtitlePath)
            ? "a replaced video counted as ours"
            : null;
    });

    Console.WriteLine();
    Console.WriteLine("What reopens a closed row");

    Check("a synced row nothing has touched is current", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);

        return SyncOrchestrator.IsStillCurrent(record, target, target.SubtitlePath, Config())
            ? null
            : "a untouched synced row was reopened";
    });

    // ! The U2 case. The already-aligned exit leaves AppliedOffsetMs holding a successful sync's
    //   offset, and reading it as a skipped movement reopened the row on every scan for ever.
    Check("a demoted row carrying a successful offset is not reopened", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Skipped;
        record.AlignedAtMs = SyncVerifier.TypicalLeadMs;
        record.AppliedOffsetMs = 400;
        record.SkippedMovementMs = null;

        return SyncOrchestrator.IsStillCurrent(record, target, target.SubtitlePath, Config())
            ? null
            : "a row skipped as already aligned reopened on its old applied offset";
    });

    Check("a row the minimum skipped stays skipped while the minimum stands", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Skipped;
        record.SkippedMovementMs = 40;

        return SyncOrchestrator.IsStillCurrent(record, target, target.SubtitlePath, Config())
            ? null
            : "a movement under the minimum reopened the row";
    });

    // The tolerance hooks are the retroactivity levers, and they judge a signed reading.
    Check("a row left alone at a shift the check now refuses is reopened", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Skipped;
        record.AlignedAtMs = 2_000;

        if (SyncVerifier.IsAligned(2_000)) { return "2000 ms is inside the aligned bound"; }

        return SyncOrchestrator.IsStillCurrent(record, target, target.SubtitlePath, Config())
            ? "a subtitle the check would now refuse was left closed"
            : null;
    });

    Check("a settings change reopens a synced row", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.SettingsStamp = "check1|live|confirmed|hi+|ocr-|SideBySide|utf8|.autosubsync";

        return SyncOrchestrator.IsStillCurrent(record, target, target.SubtitlePath, Config())
            ? "a record stamped under other settings was taken as current"
            : null;
    });

    Console.WriteLine();
    Console.WriteLine("What reopens a failed row");

    Check("a failed row nothing has changed is exhausted", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = 4_000;

        return SyncOrchestrator.IsExhausted(record, target, Config())
            ? null
            : "an unchanged failure was retried";
    });

    // ! D11: the hooks read a signed reading against a centred bound. Every refusal stores a
    //   value outside that window, so no stored refusal can reopen itself.
    Check("a refusal stored as a magnitude cannot reopen itself", () =>
    {
        var config = Config();
        var reopened = new List<int>();

        // ! -200 and -350 are the ones that matter: refused on a signed reading, inside the
        //   bound once a magnitude is taken. A hook reading |stored| reopens them for ever.
        foreach (var stored in new[] { 501, 800, 1_500, 60_001, -200, -350, -501, -800, -2_000 })
        {
            var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
            record.Status = SyncStatus.Failed;
            record.RejectedOffsetMs = stored;

            if (!SyncOrchestrator.IsExhausted(record, target, config))
            {
                reopened.Add(stored);
            }
        }

        return reopened.Count == 0 ? null : $"reopened on {string.Join(", ", reopened)}";
    });

    Check("a refusal the widened bound now accepts is retried", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = SyncVerifier.TypicalLeadMs;

        return SyncOrchestrator.IsExhausted(record, target, Config())
            ? "a refusal the check would now accept stayed parked"
            : null;
    });
}

// A record and its target, with both files on disk and fingerprints taken from them.
(SyncRecord Record, SubtitleTarget Target) Placed(SubtitleProvenance provenance, bool backup)
{
    var id = Guid.NewGuid().ToString("N");
    var video = Path.Combine(root, id + ".mkv");
    var subtitle = Path.Combine(root, id + ".srt");

    File.WriteAllBytes(video, Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray());
    File.WriteAllText(subtitle, "1\r\n00:00:01,000 --> 00:00:03,000\r\nthe plugin's own output\r\n");

    var target = new SubtitleTarget
    {
        ItemId = Guid.NewGuid(),
        ItemName = "Test Item",
        VideoPath = video,
        SubtitlePath = subtitle,
        Origin = SubtitleOrigin.External,
        Key = "sub.srt"
    };

    var record = new SyncRecord
    {
        Id = Guid.NewGuid(),
        ItemId = target.ItemId,
        ItemName = target.ItemName,
        TargetKey = target.Key,
        Status = SyncStatus.Synced,
        Provenance = provenance,
        OutputPath = subtitle,
        BackupPath = backup ? Path.Combine(root, id + ".backup.srt") : null,
        AppliedOffsetMs = 400,
        SettingsStamp = Config().OutcomeStamp(),
        VideoPartialHash = FileFingerprint.TryComputePartial(video),
        SourceSha256 = FileFingerprint.TryComputeSource(subtitle, null)
    };

    return (record, target);
}

static PluginConfiguration Config() => new() { DryRunMode = false };

void Check(string name, Func<string?> run)
{
    string? failure;

    try
    {
        failure = run();
    }
    catch (Exception ex)
    {
        failure = ex.Message;
    }

    if (failure is null)
    {
        Console.WriteLine($"  ok    {name}");
        return;
    }

    Console.WriteLine($"  FAIL  {name}: {failure}");
    failures++;
}
