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
//   - Decide consulting the engine score with confirmation on -> the governing rule is gone
//   - Decide falling through Inconclusive to Accept        -> unverified subtitles get written
//   - AcquiredOutputSurvives always true                   -> a deleted download is never replaced
//   - AcquiredOutputSurvives dropped from IsStillCurrent   -> the same file is bought every night
//   - BudgetSpent counting this run instead of the ledger  -> a set-aside row buys a fresh budget
//   - BudgetSpent dropping its SetAside test               -> a kept download is never re-checked

using System.Globalization;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;

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

        return SyncOrchestrator.IsExhausted(record, target, Config(), DateTime.UtcNow)
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

            if (!SyncOrchestrator.IsExhausted(record, target, config, DateTime.UtcNow))
            {
                reopened.Add(stored);
            }
        }

        return reopened.Count == 0 ? null : $"reopened on {string.Join(", ", reopened)}";
    });

    Decisions();
    AcquireGates();

    Check("a refusal the widened bound now accepts is retried", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = SyncVerifier.TypicalLeadMs;

        return SyncOrchestrator.IsExhausted(record, target, Config(), DateTime.UtcNow)
            ? "a refusal the check would now accept stayed parked"
            : null;
    });
}


// The verdict-to-decision gates, shared by the sync path and the acquire path.
void Decisions()
{
    Console.WriteLine();
    Console.WriteLine("What do the gates decide about an engine result?");

    Check("an aligned result with no rescale is accepted", () =>
        Decide(Aligned(), Moved(400)) is { Kind: SyncDecisionKind.Accept } d && d.Accepted
            ? null
            : "the result was not accepted");

    Check("a misaligned result is refused and carries the signed miss", () =>
    {
        var d = Decide(new VerificationResult(SyncVerdict.Misaligned, -900, null, 12, 2.1), Moved(400));

        if (d.Accepted) { return "a misaligned result was accepted"; }
        if (d.Kind != SyncDecisionKind.Misaligned) { return $"wrong branch: {d.Kind}"; }
        if (d.RejectedOffsetMs != -900) { return $"the miss lost its sign: {d.RejectedOffsetMs}"; }

        return d.Message!.Contains("out of alignment", StringComparison.Ordinal)
            ? null
            : $"wrong message: {d.Message}";
    });

    // ! Signed. A magnitude cannot be judged against a bound centred on the authored lead.
    Check("a drifting misaligned result reports the drift, not the shift", () =>
    {
        var drift = SyncVerifier.DriftWithinMs + 200;
        var d = Decide(new VerificationResult(SyncVerdict.Misaligned, -50, -drift, 12, 2.1), Moved(400));

        if (d.RejectedOffsetMs != -drift) { return $"reported {d.RejectedOffsetMs}, wanted {-drift}"; }

        return d.Message!.Contains("drifting across the runtime", StringComparison.Ordinal)
            ? null
            : $"wrong message: {d.Message}";
    });

    Check("a rescale the check never measured is refused", () =>
    {
        var d = Decide(Unmeasured(), new OffsetChange(400, SyncVerifier.DriftWithinMs + 300, 1.04));

        if (d.Accepted) { return "an unmeasured rescale was accepted"; }

        return d.Kind == SyncDecisionKind.UnmeasuredStretch ? null : $"wrong branch: {d.Kind}";
    });

    // ! Two reasons, not one. The panel groups by message text.
    Check("a title too short to measure drift gets its own reason", () =>
    {
        var stretch = new OffsetChange(400, SyncVerifier.DriftWithinMs + 300, 1.04);
        var shortTitle = Decide(
            new VerificationResult(SyncVerdict.Aligned, 170, null, SyncVerifier.DriftWindows - 1, 2.1),
            stretch);
        var flatTitle = Decide(Unmeasured(), stretch);

        if (!shortTitle.Message!.Contains("too short", StringComparison.Ordinal))
        {
            return $"the short title lost its reason: {shortTitle.Message}";
        }

        return flatTitle.Message!.Contains("too short", StringComparison.Ordinal)
            ? "a measurable title claimed it was too short"
            : null;
    });

    Check("a flat coarse reading releases a rescale the drift test never judged", () =>
    {
        var released = new VerificationResult(
            SyncVerdict.Aligned, 170, null, SyncVerifier.DriftWindows - 2, 2.1, CoarseDriftMs: 100);
        var d = Decide(released, new OffsetChange(400, SyncVerifier.DriftWithinMs + 300, 1.04));

        if (!d.Accepted) { return $"the release was refused: {d.Message}"; }

        return d.Kind == SyncDecisionKind.AcceptStretchReleased ? null : $"wrong branch: {d.Kind}";
    });

    // ! The governing rule. With confirmation on, nothing unverified is ever written.
    Check("an inconclusive verdict is refused outright while confirmation is required", () =>
    {
        var consulted = false;
        var d = SyncDecisionMaker.Decide(
            Inconclusive(),
            Moved(400),
            () => { consulted = true; return 99; },
            scoreWanted: false,
            Config());

        if (d.Accepted) { return "an unconfirmed result was accepted"; }
        if (consulted) { return "the engine score was consulted with confirmation on"; }
        if (d.Kind != SyncDecisionKind.NoVerdict) { return $"wrong branch: {d.Kind}"; }

        return d.Message == SyncOutcome.NoVerdictRefusal ? null : $"wrong message: {d.Message}";
    });

    Check("an inconclusive verdict falls to the engine score once confirmation is off", () =>
    {
        var d = Decide(
            Inconclusive(), Moved(400), confirm: false, score: SyncDecisionMaker.MinimumEngineScore);

        return d.Accepted ? null : $"a scored result was refused: {d.Message}";
    });

    Check("an engine score under the floor is refused", () =>
    {
        var d = Decide(
            Inconclusive(), Moved(400), confirm: false, score: SyncDecisionMaker.MinimumEngineScore - 0.1);

        if (d.Accepted) { return "a result under the score floor was accepted"; }

        return d.Kind == SyncDecisionKind.LowEngineScore ? null : $"wrong branch: {d.Kind}";
    });

    // ! The setting the maintainer specified: off, a download nothing refused is kept.
    Check("a download abstention falls to the engine score once the download setting is off", () =>
    {
        var d = Decide(
            Inconclusive(),
            Moved(400),
            score: SyncDecisionMaker.MinimumEngineScore,
            require: false);

        return d.Accepted ? null : $"a scored download was refused: {d.Message}";
    });

    // ! "Not outright rejected" is every gate, not just the verdict. The floor still applies.
    Check("the score floor still refuses a download the setting let through", () =>
    {
        var d = Decide(
            Inconclusive(),
            Moved(400),
            score: SyncDecisionMaker.MinimumEngineScore - 0.1,
            require: false);

        if (d.Accepted) { return "a download under the score floor was accepted"; }

        return d.Kind == SyncDecisionKind.LowEngineScore ? null : $"wrong branch: {d.Kind}";
    });

    // ! One caller only. An unset override must leave the sync path reading the sync setting.
    Check("the sync path is untouched by the download override", () =>
    {
        var d = SyncDecisionMaker.Decide(
            Inconclusive(),
            Moved(400),
            () => 99,
            scoreWanted: false,
            Config(confirm: true, conclusiveDownloads: false));

        if (d.Accepted) { return "the sync path read the download setting"; }

        return d.Kind == SyncDecisionKind.NoVerdict ? null : $"wrong branch: {d.Kind}";
    });

    // ! The description promises no effect while the sync gate is off, in both directions.
    Check("the download setting does nothing while audio confirmation is off", () =>
    {
        foreach (var conclusive in new[] { true, false })
        {
            if (SyncOrchestrator.DownloadNeedsConfirmation(Config(confirm: false, conclusive)))
            {
                return $"it required confirmation with the download setting {conclusive}";
            }
        }

        if (!SyncOrchestrator.DownloadNeedsConfirmation(Config()))
        {
            return "both settings on did not require confirmation";
        }

        return SyncOrchestrator.DownloadNeedsConfirmation(Config(true, conclusiveDownloads: false))
            ? "the download setting off still required confirmation"
            : null;
    });

    Check("an unscored inconclusive result is refused", () =>
    {
        var d = Decide(Inconclusive(), Moved(400), confirm: false, score: null);

        if (d.Accepted) { return "a result the engine never scored was accepted"; }

        return d.Kind == SyncDecisionKind.NoEngineScore ? null : $"wrong branch: {d.Kind}";
    });

    // ! Ahead of the score gates. An unconfirmed move this large is refused whatever it scored.
    Check("an unconfirmed move past the ceiling is refused before the score is read", () =>
    {
        var consulted = false;
        var d = SyncDecisionMaker.Decide(
            Inconclusive(),
            Moved(SyncDecisionMaker.MaximumUnverifiedShiftMs + 1),
            () => { consulted = true; return 99; },
            scoreWanted: false,
            Config(confirm: false));

        if (d.Accepted) { return "an unconfirmed move past the ceiling was accepted"; }
        if (consulted) { return "the engine score was read before the ceiling refused it"; }

        return d.Kind == SyncDecisionKind.UnverifiedShift ? null : $"wrong branch: {d.Kind}";
    });

    // ! It parses the produced file. An accepted sync must not pay for it unasked.
    Check("the engine score is left unread on an aligned result nothing asked about", () =>
    {
        var consulted = false;
        SyncDecisionMaker.Decide(
            Aligned(),
            Moved(400),
            () => { consulted = true; return 99; },
            scoreWanted: false,
            Config());

        return consulted ? "the produced file was parsed for nothing" : null;
    });

    Check("a caller that wants the score for its log gets it back", () =>
    {
        var d = SyncDecisionMaker.Decide(Aligned(), Moved(400), () => 61.5, scoreWanted: true, Config());

        return d.EngineScore == 61.5 ? null : $"the score did not come back: {d.EngineScore}";
    });
}

static VerificationResult Aligned()
    => new(SyncVerdict.Aligned, SyncVerifier.TypicalLeadMs, 0, 12, 2.1);

// Aligned, on a title whose drift the check never measured.
static VerificationResult Unmeasured()
    => new(SyncVerdict.Aligned, SyncVerifier.TypicalLeadMs, null, 12, 2.1);

static VerificationResult Inconclusive()
    => new(SyncVerdict.Inconclusive, null, null, 12, 1.1);

static OffsetChange Moved(long constantMs) => new(constantMs, 0, 1.0);

static SyncDecision Decide(
    VerificationResult verdict,
    OffsetChange change,
    bool confirm = true,
    double? score = null,
    bool? require = null)
    => SyncDecisionMaker.Decide(
        verdict,
        change,
        () => score,
        scoreWanted: false,
        Config(confirm),
        require);

// The acquire target has no source file, so the video hash is the whole fingerprint.
void AcquireGates()
{
    Console.WriteLine();
    Console.WriteLine("What reopens a downloaded subtitle?");

    Check("a placed download is left alone while its file is there", () =>
    {
        var (record, target) = Acquired();

        return SyncOrchestrator.IsStillCurrent(record, target, null, Config())
            ? null
            : "a download still on disk was offered again";
    });

    // ! The re-acquire loop. Nothing else stands between a deleted sidecar and a second purchase
    //   on every scan until the library catches up.
    Check("a download the user deleted is bought again", () =>
    {
        var (record, target) = Acquired();
        File.Delete(record.OutputPath!);

        return SyncOrchestrator.IsStillCurrent(record, target, null, Config())
            ? "a download that is gone was treated as current"
            : null;
    });

    Check("a replaced video reopens the download", () =>
    {
        var (record, target) = Acquired();
        File.WriteAllBytes(target.VideoPath, Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray());

        return SyncOrchestrator.IsStillCurrent(record, target, null, Config())
            ? "a download for another video was treated as current"
            : null;
    });

    // ! Without this an exhausted row re-searches every provider every night.
    Check("an exhausted acquire row is parked", () =>
    {
        var (record, target) = Acquired();
        record.Status = SyncStatus.Failed;
        record.OutputPath = null;
        record.UpdatedUtc = DateTime.UtcNow;
        record.Message = "Failed: every subtitle offered for this language was refused.";

        return SyncOrchestrator.IsExhausted(record, target, Config(), DateTime.UtcNow)
            ? null
            : "an exhausted row would re-search on the next scan";
    });

    // ! The retry setting is the second release, and it reaches a download alone.
    Check("an exhausted acquire row past the retry window searches again", () =>
    {
        var config = Config();
        var (record, target) = Acquired();
        record.Status = SyncStatus.Failed;
        record.OutputPath = null;
        record.UpdatedUtc = DateTime.UtcNow.AddDays(-config.RetryDownloadsAfterDays - 1);

        return SyncOrchestrator.IsExhausted(record, target, config, DateTime.UtcNow)
            ? "a row past its retry window stayed parked"
            : null;
    });

    Check("a sync failure is never retried on the clock", () =>
    {
        var (record, target) = Placed(SubtitleProvenance.Retimed, backup: true);
        record.Status = SyncStatus.Failed;
        record.RejectedOffsetMs = 4_000;
        record.UpdatedUtc = DateTime.UtcNow.AddDays(-400);

        return SyncOrchestrator.IsExhausted(record, target, Config(), DateTime.UtcNow)
            ? null
            : "an old sync failure was retried on the same bytes";
    });

    Check("a refusal the widened bound now accepts reopens the search", () =>
    {
        var (record, target) = Acquired();
        record.Status = SyncStatus.Failed;
        record.OutputPath = null;
        record.UpdatedUtc = DateTime.UtcNow;
        record.RejectedOffsetMs = SyncVerifier.TypicalLeadMs;

        return SyncOrchestrator.IsExhausted(record, target, Config(), DateTime.UtcNow)
            ? "a refusal the check would now accept stayed parked"
            : null;
    });

    Console.WriteLine();
    Console.WriteLine("What stops a set-aside row buying a fresh budget?");

    // ! A set-aside row is never exhausted, so nothing else bounds what it spends over time.
    Check("a set-aside row that spent the limit is parked", () =>
        SyncOrchestrator.BudgetSpent(SetAside(3), Config(), DateTime.UtcNow)
            ? null
            : "a spent budget would buy three more on the next scan");

    Check("a set-aside row under the limit still searches", () =>
        SyncOrchestrator.BudgetSpent(SetAside(2), Config(), DateTime.UtcNow)
            ? "a row with budget left was parked"
            : null);

    Check("raising the limit releases a parked row", () =>
        SyncOrchestrator.BudgetSpent(
            SetAside(3),
            new PluginConfiguration { MaxDownloadsPerItem = 6 },
            DateTime.UtcNow)
            ? "raising the limit left the row parked"
            : null);

    Check("an unlimited budget never parks a row", () =>
        SyncOrchestrator.BudgetSpent(
            SetAside(9),
            new PluginConfiguration { MaxDownloadsPerItem = 0 },
            DateTime.UtcNow)
            ? "an unlimited budget was treated as spent"
            : null);

    // ! A suppressed track is set aside too, and it has never bought anything.
    Check("a suppression is not a spent budget", () =>
        SyncOrchestrator.BudgetSpent(SetAside(0), Config(), DateTime.UtcNow)
            ? "a track a setting declined to process was parked as if it had downloaded"
            : null);

    // ! The allowance is counted over the retry window, so a lapsed one opens a fresh window.
    //   The acquirer's own ledger check is what stops it buying the same files again.
    Check("an allowance older than the retry window is spent again", () =>
    {
        var config = Config();
        var stale = SetAside(3, DateTime.UtcNow.AddDays(-config.RetryDownloadsAfterDays - 1));

        return SyncOrchestrator.BudgetSpent(stale, config, DateTime.UtcNow)
            ? "a lapsed allowance stayed parked for ever"
            : null;
    });

    // ! Zero is no wait at all, at both gates. A budget counted over an empty window is empty.
    Check("no cooldown means the allowance never parks a row", () =>
    {
        var config = Config();
        config.RetryDownloadsAfterDays = 0;

        return SyncOrchestrator.BudgetSpent(SetAside(9), config, DateTime.UtcNow)
            ? "a row was parked under a zero cooldown"
            : null;
    });

    Check("a kept download is not parked by its own ledger", () =>
    {
        var (record, _) = Acquired();
        record.AcquireAttempts.Add(new AcquireAttempt
        {
            AttemptedUtc = DateTime.UtcNow,
            Outcome = AcquireAttemptOutcome.Kept
        });

        return SyncOrchestrator.BudgetSpent(
            record,
            new PluginConfiguration { MaxDownloadsPerItem = 1 },
            DateTime.UtcNow)
            ? "a synced row was parked and would never be checked again"
            : null;
    });

    Console.WriteLine();
    Console.WriteLine("When is a language asked about again?");

    // ! The whole point of the stamp. A scan on its own must not spend the API call twice.
    Check("a language answered a moment ago is not searched again", () =>
    {
        var config = new PluginConfiguration();
        var record = Searched(config, DateTime.UtcNow.AddDays(-1));

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? null
            : "the row would be searched again on the next scan";
    });

    Check("the cooldown lapses", () =>
    {
        var config = new PluginConfiguration();
        var record = Searched(config, DateTime.UtcNow - config.RetryDownloadsAfter().Add(TimeSpan.FromHours(1)));

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "a lapsed row stayed parked"
            : null;
    });

    // ! Zero is the setting's own off switch, and it must reach this gate as well as the budget.
    Check("no cooldown searches every scan", () =>
    {
        var config = new PluginConfiguration { RetryDownloadsAfterDays = 0 };
        var record = Searched(config, DateTime.UtcNow);

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "a row was parked under a zero cooldown"
            : null;
    });

    // ! A clock that moved backwards must not park a row until it catches up.
    Check("a stamp in the future is read as lapsed", () =>
    {
        var config = new PluginConfiguration();
        var record = Searched(config, DateTime.UtcNow.AddDays(30));

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "a row stamped in the future was parked"
            : null;
    });

    // ! A wall leaves the time stamped and the search stamp null. Reading the time alone parks
    //   a language no provider ever answered for.
    Check("a row closed without an answer is never parked", () =>
    {
        var config = new PluginConfiguration();
        var record = Searched(config, DateTime.UtcNow);
        record.SearchStamp = null;

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "a walled row was parked as if the providers had answered"
            : null;
    });

    // ! The retry gate must read the acquire loop's own stamp. UpdatedUtc is rewritten by any
    //   later write to the row, which would postpone the retry with nothing to show for it.
    Check("a later write to the row does not postpone the retry", () =>
    {
        var config = Config();
        var (record, target) = Acquired();
        record.Status = SyncStatus.Failed;
        record.OutputPath = null;
        record.SearchedUtc = DateTime.UtcNow.AddDays(-config.RetryDownloadsAfterDays - 1);
        record.UpdatedUtc = DateTime.UtcNow;

        return SyncOrchestrator.RetryDue(record, target, config, DateTime.UtcNow)
            ? null
            : "a row past its window was held by an unrelated write";
    });

    // ! The answer was about a different question. Parking on it hides the new one.
    Check("changing what the search asks for releases the row at once", () =>
    {
        var config = new PluginConfiguration();
        var record = Searched(config, DateTime.UtcNow);

        config.AcquireHearingImpaired = !config.AcquireHearingImpaired;

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "the row stayed parked under settings it was never searched under"
            : null;
    });

    Check("a row that never recorded an answer is never parked", () =>
    {
        var config = new PluginConfiguration();
        var record = new SyncRecord { Status = SyncStatus.SetAside };

        return SyncOrchestrator.SearchedRecently(record, config, DateTime.UtcNow)
            ? "a row with no recorded search was parked"
            : null;
    });

    Console.WriteLine();
    Console.WriteLine("Where does an acquire outcome land on the panel?");

    // ! The set-aside family is on no card. Failing them fills the panel with one fact.
    Check("nothing bought is a skip, never a failure", () =>
    {
        foreach (var result in new[]
                 {
                     AcquireResult.NothingOffered,
                     AcquireResult.HearingImpairedOnly,
                     AcquireResult.AllFiltered,
                     AcquireResult.ProvidersRetired,
                     AcquireResult.CapReached
                 })
        {
            if (SyncOrchestrator.StageFor(result) != StageOutcome.Skipped)
            {
                return $"{result} was not a skip";
            }
        }

        return null;
    });

    // ! Only a list the check saw and refused. A spent allowance stopped the item short of it.
    Check("a download bought and refused is a failure", () =>
    {
        if (SyncOrchestrator.StageFor(AcquireResult.Exhausted) != StageOutcome.Failed)
        {
            return "a refused list was not a failure";
        }

        return SyncOrchestrator.StageFor(AcquireResult.Kept) == StageOutcome.Succeeded
            ? null
            : "a kept download was not a success";
    });
}

// A row the providers answered in full, set aside at the given moment.
static SyncRecord Searched(PluginConfiguration config, DateTime when) => new()
{
    Status = SyncStatus.SetAside,
    SearchedUtc = when,
    SearchStamp = config.SearchStamp()
};

// A row the acquire path set aside, carrying the downloads it already paid for.
static SyncRecord SetAside(int paid, DateTime? attemptedUtc = null)
{
    var record = new SyncRecord { Status = SyncStatus.SetAside };

    for (var i = 0; i < paid; i++)
    {
        record.AcquireAttempts.Add(new AcquireAttempt
        {
            SubtitleId = i.ToString(CultureInfo.InvariantCulture),
            AttemptedUtc = attemptedUtc ?? DateTime.UtcNow,
            Outcome = AcquireAttemptOutcome.HearingImpaired
        });
    }

    return record;
}

// A downloaded subtitle this plugin placed, with no source file behind it.
(SyncRecord Record, SubtitleTarget Target) Acquired()
{
    var id = Guid.NewGuid().ToString("N");
    var video = Path.Combine(root, id + ".mkv");
    var output = Path.Combine(root, id + ".eng.autosubsync.srt");

    File.WriteAllBytes(video, Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray());
    File.WriteAllText(output, "1\r\n00:00:01,000 --> 00:00:03,000\r\nthe downloaded file\r\n");

    var target = new SubtitleTarget
    {
        ItemId = Guid.NewGuid(),
        ItemName = "Test Item",
        VideoPath = video,
        Origin = SubtitleOrigin.Acquired,
        Language = "eng",
        Key = SubtitleTarget.AcquireKey("eng")
    };

    var record = new SyncRecord
    {
        Id = Guid.NewGuid(),
        ItemId = target.ItemId,
        ItemName = target.ItemName,
        TargetKey = target.Key,
        Origin = SubtitleOrigin.Acquired,
        Status = SyncStatus.Synced,
        Provenance = SubtitleProvenance.Created,
        OutputPath = output,
        SettingsStamp = Config().OutcomeStamp(),
        VideoPartialHash = FileFingerprint.TryComputePartial(video)
    };

    return (record, target);
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

static PluginConfiguration Config(bool confirm = true, bool conclusiveDownloads = true)
    => new()
    {
        DryRunMode = false,
        RequireAudioConfirmation = confirm,
        RequireConclusiveDownloads = conclusiveDownloads
    };

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
