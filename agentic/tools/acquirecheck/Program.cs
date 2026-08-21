// What does the acquire path buy, what does it refuse, and what does it never pay for?
//
// A wrong provider name disables the whole feature silently, and no other harness would notice.
// Nothing here touches a network or spends an allowance: the provider is a stub.
//
// Mutation: make DownloadProviders.Matches a substring test -> the impostor case passes.
// Mutation: drop "Open Subtitles" to "OpenSubtitles"  -> a real server reports no downloader.
// Mutation: count a filtered candidate against the cap -> bad metadata bounds the wrong thing.
// Mutation: stop the loop on Inconclusive   -> the shared gates never judge the download at all.
// Mutation: break on any wall, not the provider's -> one spent account ends the aggregator's list.
// Mutation: drop the id cross-check      -> a label walls a source that never answered.
// Mutation: drop the host shape from a token -> a plain provider invents one and is under-retired.
// Mutation: drop the walled outcome      -> a spent allowance reads as unusable subtitles.
// Mutation: always use the refused wording -> the inconclusive card empties under the default.
// Mutation: compare the gap test on the slot key -> forced and SDH tracks stop filling a language.
// Mutation: drop the SearchFailures message  -> a provider that threw reports an empty answer.
// Mutation: walk InnerException alone        -> a wall inside an aggregate is asked again per item.
// Mutation: stop setting IsHearingImpaired   -> an accepted SDH download is named plain dialogue.
// Mutation: return CapReached on an all-SDH budget -> a skip is reported as an audio failure.
// Mutation: drop the SDH-Removed guard   -> the one cleaned file on the list is the one refused.
// Mutation: match HI case-insensitively  -> an episode titled "Hi Mom" is read as a marker.

using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;

const string PlainCues =
    "1\n00:00:01,000 --> 00:00:03,000\nGood evening.\n\n"
    + "2\n00:00:04,000 --> 00:00:06,000\nIt is good to see you.\n\n"
    + "3\n00:00:07,000 --> 00:00:09,000\nShall we begin?\n";

const string SdhCues =
    "1\n00:00:01,000 --> 00:00:03,000\n[door creaks]\n\n"
    + "2\n00:00:04,000 --> 00:00:06,000\n(SIGHS)\n\n"
    + "3\n00:00:07,000 --> 00:00:09,000\nMAN: Good evening.\n\n"
    + "4\n00:00:10,000 --> 00:00:12,000\n[footsteps approaching]\n\n"
    + "5\n00:00:13,000 --> 00:00:15,000\n[music swells]\n\n"
    + "6\n00:00:16,000 --> 00:00:18,000\nShall we begin?\n";

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "acquirecheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);

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

Whitelist();
GapTest();
Filters();
Budget();
FallThrough();
Retirement();
Ledger();
Naming();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "acquirecheck: all cases pass" : $"acquirecheck: {failures} failed");

try
{
    Directory.Delete(sandbox, true);
}
catch (IOException)
{
}

return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- the whitelist

// ! Every string quoted from that plugin's own source. A wrong one disables the feature silently.
void Whitelist()
{
    Console.WriteLine("Which provider names count as downloaders?");

    Check("the three shipped names match verbatim", () =>
    {
        foreach (var name in new[] { "Open Subtitles", "Addic7ed/Gestdown Subtitles", "subbuzz" })
        {
            if (!DownloadProviders.IsKnownDownloader(name, []))
            {
                return $"{name} was not recognised";
            }
        }

        return null;
    });

    // ! The official plugin reports the name with a space. The spaceless form matches nothing.
    Check("the spaceless OpenSubtitles spelling matches nothing", () =>
        DownloadProviders.IsKnownDownloader("OpenSubtitles", [])
            ? "a name no provider reports was accepted"
            : null);

    Check("a doubled space does not match", () =>
        DownloadProviders.IsKnownDownloader("open  subtitles", [])
            ? "a name no provider reports was accepted"
            : null);

    Check("case and surrounding space are ignored", () =>
        DownloadProviders.IsKnownDownloader("  OPEN SUBTITLES  ", [])
            ? null
            : "the trimmed, case-folded form was refused");

    // ! subbuzz reports its internal sources on results, never as a provider name.
    Check("a subbuzz result source is not a provider", () =>
        DownloadProviders.IsKnownDownloader("[subbuzz] <b>Addic7ed.com</b>", [])
            ? "an internal source name was taken for a provider"
            : null);

    // ! The case this rule exists for. A substring test lets exactly this through.
    Check("a plugin whose name contains a whitelisted one is not a downloader", () =>
        DownloadProviders.IsKnownDownloader("Local Subs (OpenSubtitles naming)", [])
            ? "an impostor name passed the gate"
            : null);

    Check("the box adds a name the shipped list has never heard of", () =>
        DownloadProviders.IsKnownDownloader("Whisper Subs", ["Whisper Subs"])
            ? null
            : "an admin-named downloader was refused");

    // ! It adds, it never removes. Disabling belongs in the library settings.
    Check("the box cannot remove a shipped name", () =>
        DownloadProviders.IsKnownDownloader("subbuzz", ["Whisper Subs"])
            ? null
            : "a shipped downloader was lost");

    Check("a name matching nothing installed is reported back", () =>
    {
        var unresolved = DownloadProviders.Unresolved(
            [" Whisper Subs ", "subbuzz"],
            ["subbuzz", "Open Subtitles"]);

        if (unresolved.Count != 1)
        {
            return $"reported {unresolved.Count} unresolved names";
        }

        return unresolved[0] == "Whisper Subs" ? null : $"reported {unresolved[0]}";
    });

    Console.WriteLine();
    Console.WriteLine("Which order are providers asked in?");

    // The box is a priority chain as well as a whitelist.
    Check("a name in the box is asked before the admin order", () =>
    {
        var order = DownloadProviders.Order(
            ["Open Subtitles", "subbuzz"],
            [],
            ["Open Subtitles", "subbuzz"],
            ["subbuzz"]);

        return string.Join(",", order) == "subbuzz,Open Subtitles"
            ? null
            : $"asked in the order {string.Join(",", order)}";
    });

    Check("the box orders among itself", () =>
    {
        var order = DownloadProviders.Order(
            ["Open Subtitles", "subbuzz", "Addic7ed/Gestdown Subtitles"],
            [],
            [],
            ["subbuzz", "Open Subtitles"]);

        return string.Join(",", order).StartsWith("subbuzz,Open Subtitles", StringComparison.Ordinal)
            ? null
            : $"asked in the order {string.Join(",", order)}";
    });

    // ! A disabled fetcher is not a provider. The box cannot bring one back.
    Check("the box cannot re-enable a disabled fetcher", () =>
    {
        var order = DownloadProviders.Order(
            ["Open Subtitles", "subbuzz"],
            ["subbuzz"],
            [],
            ["subbuzz"]);

        return string.Join(",", order) == "Open Subtitles"
            ? null
            : $"asked in the order {string.Join(",", order)}";
    });

    Check("a provider the admin never ordered sorts last", () =>
    {
        var order = DownloadProviders.Order(
            ["subbuzz", "Open Subtitles"],
            [],
            ["Open Subtitles"],
            []);

        return string.Join(",", order) == "Open Subtitles,subbuzz"
            ? null
            : $"asked in the order {string.Join(",", order)}";
    });
}

// ---------------------------------------------------------------- the gap test

// ! On LanguageKey alone. The slot key is the wrong instrument and passes the embedded case.
void GapTest()
{
    Console.WriteLine();
    Console.WriteLine("Which languages does an item have nothing in?");

    Check("a language with no track at all is a gap", () =>
        Gaps(Wants("eng"), External("spa")) is ["eng"] ? null : "the gap was not offered");

    Check("a plain external track fills its language", () =>
        Gaps(Wants("eng"), External("eng")) is [] ? null : "a filled language was called a gap");

    // ! A slot is a language. Nothing here splits it by forced or hearing-impaired.
    Check("a forced track fills its language", () =>
        Gaps(Wants("eng"), Forced("eng")) is [] ? null : "a forced track left the language open");

    Check("a hearing-impaired track fills its language", () =>
        Gaps(Wants("eng"), Sdh("eng")) is [] ? null : "an SDH track left the language open");

    // ! The plugin's own output is in that stream list. This is what stops a second purchase.
    Check("a subtitle this plugin wrote fills its language", () =>
        Gaps(Wants("eng"), Ours("eng")) is [] ? null : "the plugin would buy its own file again");

    // OCR is the tool for a bitmap track, not a download.
    Check("an image track fills its language", () =>
        Gaps(Wants("eng"), Image("eng")) is [] ? null : "an image track left the language open");

    Check("an embedded track fills its language by default", () =>
        Gaps(Wants("eng"), Embedded("eng")) is []
            ? null
            : "an embedded track left the language open");

    Check("the opt-out turns an embedded track back into a gap", () =>
    {
        var config = Wants("eng");
        config.AcquireWhenEmbeddedExists = true;

        return Gaps(config, Embedded("eng")) is ["eng"] ? null : "the opt-out changed nothing";
    });

    // ! The opt-out is about embedded tracks alone.
    Check("the opt-out leaves an external track filling its language", () =>
    {
        var config = Wants("eng");
        config.AcquireWhenEmbeddedExists = true;

        return Gaps(config, External("eng")) is [] ? null : "a sidecar stopped filling its language";
    });

    Check("region qualifiers and two-letter forms are one language", () =>
        Gaps(Wants("eng"), External("en-US")) is [] ? null : "en-US did not fill eng");

    Check("the bibliographic form is the same language", () =>
        Gaps(Wants("deu"), External("ger")) is [] ? null : "ger did not fill deu");

    // ! Normalize does not de-duplicate this list, so one language could claim two budgets.
    Check("eng and en are one gap, not two", () =>
        Gaps(Wants("eng", "en"), External("spa")) is ["eng"]
            ? null
            : "one language claimed two targets");

    Check("the order the languages were typed is the order they are tried", () =>
        Gaps(Wants("spa", "eng"), External("fra")) is ["spa", "eng"]
            ? null
            : "the listed order was not preserved");

    // ! Empty means all, and all is not a thing anyone can download.
    Check("an empty language list leaves the feature inert", () =>
        Gaps(Wants(), External("spa")) is [] ? null : "an empty box asked for a download");

    Check("the master toggle off leaves the feature inert", () =>
    {
        var config = Wants("eng");
        config.AcquireMissingSubtitles = false;

        return Gaps(config, External("spa")) is [] ? null : "the toggle did not disable it";
    });

    // ! A track nobody labelled could be any language, so nothing can be proved missing.
    Check("an unlabelled track stops the item being called a gap", () =>
        SubtitleDiscoveryService.Gaps([Stream(null, external: true)], Wants("eng")) is null
            ? null
            : "an unreadable label was treated as proof of absence");

    Check("an unlabelled embedded track is ignored under the opt-out", () =>
    {
        var config = Wants("eng");
        config.AcquireWhenEmbeddedExists = true;

        return Gaps(config, Stream(null, external: false)) is ["eng"]
            ? null
            : "an embedded track blocked the opt-out";
    });
}

// ---------------------------------------------------------------- pre-fetch filters

void Filters()
{
    Console.WriteLine();
    Console.WriteLine("Which candidates are dropped before a download is spent?");

    Check("a forced candidate is dropped and costs nothing", () =>
        Spent(Offer("a", forced: true)) == 0 ? null : "a forced candidate was bought");

    Check("a machine translation is dropped and costs nothing", () =>
        Spent(Offer("a", machine: true)) == 0 ? null : "a machine translation was bought");

    Check("an AI translation is dropped and costs nothing", () =>
        Spent(Offer("a", ai: true)) == 0 ? null : "an AI translation was bought");

    // ! Null is the provider not saying, never no.
    Check("an unstated translation flag is not a refusal", () =>
        Spent(Offer("a")) == 1 ? null : "a candidate nobody flagged was dropped");

    Check("a format the engine cannot read is dropped and costs nothing", () =>
        Spent(Offer("a", format: "sub")) == 0 ? null : "an unreadable format was bought");

    Check("every readable format is offered", () =>
        Spent(Offer("a", format: "ass")) == 1 ? null : "a readable format was dropped");

    Console.WriteLine();
    Console.WriteLine("What happens to hearing-impaired candidates?");

    // ! Free, so it must never consume the per-item budget.
    Check("an advertised SDH candidate is dropped without a download", () =>
        Spent(Offer("a", sdh: true)) == 0 ? null : "an advertised SDH candidate was bought");

    // ! The shapes a real provider offered, synthetic. The flag was false on all of them.
    Check("a name that says SDH is dropped without a download", () =>
    {
        foreach (var name in new[]
                 {
                     "Show (2001) - S01E02 - Episode.eng.SDH.srt",
                     "English SDH Movie.2001.2160p.WEB-DL.en-us.srt",
                     "Movie.2001.WEB-DL.H.264-GROUP.en[sdh].srt",
                     "Show S01E12 Episode.DVD.HI.pcc.en.GROUP",
                     "Show.S01E12.Episode.DVDRip-GROUP.eng-HI"
                 })
        {
            if (Spent(Offer("a", name: name)) != 0)
            {
                return $"bought {name}";
            }
        }

        return null;
    });

    // ! The token can mean the annotations are gone. That names the best file on the list.
    Check("a name saying the tags were removed is still bought", () =>
        Spent(Offer("a", name: "English (SDH Removed) Movie.2001.2160p.WEB-DL.en-us.srt")) == 1
            ? null
            : "a cleaned subtitle was refused");

    // ! Addic7ed puts the episode title in Name, so a lower-case Hi is a word, not a marker.
    Check("an ordinary name carrying hi is still bought", () =>
    {
        foreach (var name in new[]
                 {
                     "[720p.WEB-DL] Show | Hi There | Completed",
                     "Machine.2001.1080p.BluRay.eng.srt",
                     "Highway.2001.1080p.BluRay.eng.srt",
                     "Show.S01.E01.WEBRip.Service.en-us.srt"
                 })
        {
            if (Spent(Offer("a", name: name)) != 1)
            {
                return $"refused {name}";
            }
        }

        return null;
    });

    Check("turning SDH on lets a name that says SDH through", () =>
    {
        var run = Run(
            WithSdh(),
            [Offer("a", name: "Show (2001) - S01E02 - Episode.eng.SDH.srt")],
            _ => CandidateVerdict.Kept);

        return run.Outcome.Result == AcquireResult.Kept ? null : $"ended {run.Outcome.Result}";
    });

    // ! The download has already been made, so it counts.
    Check("a candidate the detector finds SDH is discarded and does count", () =>
    {
        var run = Run(Config(), [Offer("a")], _ => CandidateVerdict.Kept, sdhBytes: true);

        if (run.Outcome.Fetches != 1)
        {
            return $"charged {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.Result == AcquireResult.HearingImpairedOnly
            ? null
            : $"ended {run.Outcome.Result}";
    });

    Check("an item offered nothing but SDH is set aside, not failed", () =>
    {
        var run = Run(Config(), [Offer("a", sdh: true), Offer("b", sdh: true)], _ => CandidateVerdict.Kept);

        if (run.Outcome.Fetches != 0)
        {
            return $"charged {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.Result == AcquireResult.HearingImpairedOnly
            ? null
            : $"ended {run.Outcome.Result}";
    });

    Check("turning SDH on lets the same candidate through", () =>
    {
        var run = Run(WithSdh(), [Offer("a", sdh: true)], _ => CandidateVerdict.Kept);
        return run.Outcome.Result == AcquireResult.Kept ? null : $"ended {run.Outcome.Result}";
    });

    // ! A budget spent on files the judge never saw. Failing it blames the audio check.
    Check("a budget spent entirely on SDH is set aside, not failed", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 2;

        var run = Run(config, [Offer("a"), Offer("b"), Offer("c")], _ => CandidateVerdict.Kept, sdhBytes: true);

        if (run.Outcome.Fetches != 2)
        {
            return $"charged {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.Result == AcquireResult.HearingImpairedOnly
            ? null
            : $"ended {run.Outcome.Result}";
    });

    Check("a budget with a real refusal in it still fails", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 2;

        var offers = new[] { Offer("a", sdh: true), Offer("b"), Offer("c"), Offer("d") };
        var run = Run(config, offers, _ => CandidateVerdict.Misaligned);

        return run.Outcome.Result == AcquireResult.CapReached ? null : $"ended {run.Outcome.Result}";
    });

    // ! Turning it on inserts candidates and re-orders nothing.
    Check("turning SDH on inserts candidates and reorders none", () =>
    {
        var offers = new[] { Offer("a", sdh: true), Offer("b"), Offer("c", sdh: true), Offer("d") };

        var off = Order(Config(), offers);
        var on = Order(WithSdh(), offers);

        if (string.Join(",", off) != "b,d")
        {
            return $"with SDH off the order was {string.Join(",", off)}";
        }

        return string.Join(",", on) == "a,b,c,d"
            ? null
            : $"with SDH on the order was {string.Join(",", on)}";
    });

    Console.WriteLine();
    Console.WriteLine("How are the candidates ranked?");

    // ! One promotion on top of the provider order, which was made with better information.
    Check("a hash match is promoted to the front", () =>
    {
        var order = Order(Config(), [Offer("a"), Offer("b"), Offer("c", hash: true)]);

        return string.Join(",", order) == "c,a,b" ? null : $"ranked {string.Join(",", order)}";
    });

    Check("everything else keeps the order the provider returned", () =>
    {
        var order = Order(Config(), [Offer("a"), Offer("b"), Offer("c")]);

        return string.Join(",", order) == "a,b,c" ? null : $"ranked {string.Join(",", order)}";
    });

    Check("two hash matches keep their own relative order", () =>
    {
        var order = Order(Config(), [Offer("a"), Offer("b", hash: true), Offer("c", hash: true)]);

        return string.Join(",", order) == "b,c,a" ? null : $"ranked {string.Join(",", order)}";
    });
}

// ---------------------------------------------------------------- the budget

void Budget()
{
    Console.WriteLine();
    Console.WriteLine("What bounds one item?");

    Check("the cap stops the loop with candidates still on the list", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 2;

        var run = Run(config, [Offer("a"), Offer("b"), Offer("c")], _ => CandidateVerdict.Misaligned);

        if (run.Outcome.Fetches != 2)
        {
            return $"made {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.Result == AcquireResult.CapReached ? null : $"ended {run.Outcome.Result}";
    });

    // ! A cap reached is not exhaustion. Only one of the two is fixed by changing a setting.
    Check("a list that ran out is exhausted, not capped", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 5;

        var run = Run(config, [Offer("a"), Offer("b")], _ => CandidateVerdict.Misaligned);
        return run.Outcome.Result == AcquireResult.Exhausted ? null : $"ended {run.Outcome.Result}";
    });

    // ! The budget spans providers. Moving on does not reset it.
    Check("the budget spans every provider", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 2;

        var run = Run(
            config,
            [Offer("a"), Offer("b")],
            _ => CandidateVerdict.Misaligned,
            providers: ["Open Subtitles", "subbuzz", "Addic7ed/Gestdown Subtitles"]);

        return run.Outcome.Fetches == 2 ? null : $"made {run.Outcome.Fetches} downloads";
    });

    // ! Zero is unlimited, never disabled. The master toggle is what disables the feature.
    Check("zero is unlimited and runs past the default of three", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 0;

        var offers = Enumerable.Range(0, 7).Select(i => Offer("id" + i)).ToArray();
        var run = Run(config, offers, _ => CandidateVerdict.Misaligned);

        return run.Outcome.Fetches == 7 ? null : $"made {run.Outcome.Fetches} downloads";
    });

    Check("a filtered candidate never consumes the budget", () =>
    {
        var config = Config();
        config.MaxDownloadsPerItem = 1;

        var run = Run(
            config,
            [Offer("a", forced: true), Offer("b", sdh: true), Offer("c")],
            _ => CandidateVerdict.Kept);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        return run.Outcome.Fetches == 1 ? null : $"made {run.Outcome.Fetches} downloads";
    });
}

// ---------------------------------------------------------------- fall-through

void FallThrough()
{
    Console.WriteLine();
    Console.WriteLine("When does the loop move to the next provider?");

    Check("a refused list falls through and the next provider wins", () =>
    {
        var source = new StubSource(["Open Subtitles", "subbuzz"]);
        source.Results["Open Subtitles"] = [Offer("a1"), Offer("a2")];
        source.Results["subbuzz"] = [Offer("b1")];

        var run = RunWith(
            Config(),
            source,
            id => id.StartsWith('a') ? CandidateVerdict.Misaligned : CandidateVerdict.Kept);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        if (run.Outcome.Fetches != 3)
        {
            return $"made {run.Outcome.Fetches} downloads";
        }

        return source.Searched.Count == 2 ? null : $"searched {source.Searched.Count} providers";
    });

    // ! The setting belongs to the shared gates. A second copy of it here would stop the item
    //   ahead of them, which is the whole of what the loop must not do.
    Check("the loop reads no setting of its own about an abstention", () =>
    {
        foreach (var on in new[] { true, false })
        {
            var source = new StubSource(["Open Subtitles", "subbuzz"]);
            source.Results["Open Subtitles"] = [Offer("a1"), Offer("a2")];
            source.Results["subbuzz"] = [Offer("b1")];

            var run = RunWith(Conclusive(on), source, _ => CandidateVerdict.Inconclusive);

            if (run.Outcome.Result != AcquireResult.Exhausted)
            {
                return $"ended {run.Outcome.Result} with the setting {on}";
            }

            if (run.Outcome.Fetches != 3)
            {
                return $"made {run.Outcome.Fetches} downloads with the setting {on}";
            }

            if (source.Searched.Count != 2)
            {
                return $"searched {source.Searched.Count} providers with the setting {on}";
            }
        }

        return null;
    });

    // ! The default. It buys the list out rather than trusting one non-answer about the title.
    Check("an abstention buys the next candidate where the setting is on", () =>
    {
        var source = new StubSource(["Open Subtitles", "subbuzz"]);
        source.Results["Open Subtitles"] = [Offer("a1"), Offer("a2")];
        source.Results["subbuzz"] = [Offer("b1")];

        var run = RunWith(Conclusive(true), source, _ => CandidateVerdict.Inconclusive);

        if (run.Outcome.Result != AcquireResult.Exhausted)
        {
            return $"ended {run.Outcome.Result}";
        }

        if (run.Outcome.Fetches != 3)
        {
            return $"made {run.Outcome.Fetches} downloads";
        }

        return source.Searched.Count == 2 ? null : $"searched {source.Searched.Count} providers";
    });

    // ! The verdict the check reached, not what the setting did with it.
    Check("the ledger still records an abstention as inconclusive", () =>
    {
        var run = Run(Conclusive(true), [Offer("a")], _ => CandidateVerdict.Inconclusive);
        var ledger = run.Record.AcquireAttempts;

        if (ledger.Count != 1)
        {
            return $"ledgered {ledger.Count} attempts";
        }

        return ledger[0].Outcome == AcquireAttemptOutcome.Inconclusive
            ? null
            : $"ledgered it as {ledger[0].Outcome}";
    });

    // ! The budget still bounds it. Patience must not become an unbounded spend.
    Check("the per-item limit still stops a patient item", () =>
    {
        var config = Conclusive(true);
        config.MaxDownloadsPerItem = 2;

        var run = Run(
            config,
            [Offer("a"), Offer("b"), Offer("c")],
            _ => CandidateVerdict.Inconclusive);

        return run.Outcome.Fetches == 2 ? null : $"made {run.Outcome.Fetches} downloads";
    });

    // ! The default path, so this is the wording the card is mostly made of.
    Check("a list bought out on abstentions is named as inconclusive", () =>
    {
        var run = Run(Conclusive(true), [Offer("a"), Offer("b")], _ => CandidateVerdict.Inconclusive);

        if (run.Outcome.Message != SyncOutcome.NoVerdictExhausted)
        {
            return $"said \"{run.Outcome.Message}\"";
        }

        return SyncOutcome.IsInconclusiveRefusal(Panel(run.Outcome))
            ? null
            : "the panel reads it as an ordinary refusal";
    });

    // ! One measured refusal is a refusal. Only a list of pure non-answers is the other card.
    Check("one misalignment among abstentions is an ordinary refusal", () =>
    {
        var run = Run(
            Conclusive(true),
            [Offer("a"), Offer("b")],
            id => id == "b" ? CandidateVerdict.Misaligned : CandidateVerdict.Inconclusive);

        if (run.Outcome.Message == SyncOutcome.NoVerdictExhausted)
        {
            return "named as inconclusive";
        }

        return SyncOutcome.IsInconclusiveRefusal(Panel(run.Outcome))
            ? "the panel reads it as inconclusive"
            : null;
    });

    Check("nothing offered anywhere is set aside, not failed", () =>
    {
        var run = Run(Config(), [], _ => CandidateVerdict.Kept);

        if (run.Outcome.Fetches != 0)
        {
            return $"made {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.Result == AcquireResult.NothingOffered ? null : $"ended {run.Outcome.Result}";
    });

    Check("everything filtered out is set aside with its own reason", () =>
    {
        var run = Run(Config(), [Offer("a", forced: true)], _ => CandidateVerdict.Kept);

        return run.Outcome.Result == AcquireResult.AllFiltered ? null : $"ended {run.Outcome.Result}";
    });

    // ! A search failure is this provider offering nothing, never an item failure.
    Check("a provider that cannot be searched is skipped, not failed", () =>
    {
        var source = new StubSource(["Open Subtitles", "subbuzz"]);
        source.SearchThrows["Open Subtitles"] = new IOException("the search timed out");
        source.Results["subbuzz"] = [Offer("b1")];

        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept);
        return run.Outcome.Result == AcquireResult.Kept ? null : $"ended {run.Outcome.Result}";
    });

    // ! The panel may never lie. A provider that threw established nothing about what it has.
    Check("a search that threw is not reported as an empty answer", () =>
    {
        var source = new StubSource(["Open Subtitles"]);
        source.SearchThrows["Open Subtitles"] = new IOException("the search timed out");

        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept);

        if (run.Outcome.Result != AcquireResult.NothingOffered)
        {
            return $"ended {run.Outcome.Result}";
        }

        return run.Outcome.Message?.Contains("could be searched", StringComparison.Ordinal) == true
            ? null
            : $"said: {run.Outcome.Message}";
    });

    Check("a provider that answered nothing still says so", () =>
    {
        var run = Run(Config(), [], _ => CandidateVerdict.Kept);

        return run.Outcome.Message?.Contains("no subtitle was offered", StringComparison.Ordinal) == true
            ? null
            : $"said: {run.Outcome.Message}";
    });
}

// ---------------------------------------------------------------- what the file is called

void Naming()
{
    Console.WriteLine();
    Console.WriteLine("Does a kept download carry the right name?");

    // ! The placer builds the sidecar name off the target. Nothing else marks a download SDH.
    Check("an accepted hearing-impaired download is named as one", () =>
    {
        var target = new SubtitleTarget();
        var run = Run(WithSdh(), [Offer("a", sdh: true)], _ => CandidateVerdict.Kept, target: target);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        return target.IsHearingImpaired ? null : "an SDH download would be named as plain dialogue";
    });

    Check("a plain download is not named hearing-impaired", () =>
    {
        var target = new SubtitleTarget();
        var run = Run(Config(), [Offer("a")], _ => CandidateVerdict.Kept, target: target);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        return target.IsHearingImpaired ? "a plain download gained an sdh token" : null;
    });
}

// ---------------------------------------------------------------- quota and auth

void Retirement()
{
    Console.WriteLine();
    Console.WriteLine("What happens when a provider stops answering?");

    // ! An AggregateException holds several inner exceptions and InnerException reads the first.
    Check("a wall wrapped in an aggregate is still a wall", () =>
    {
        var buried = new AggregateException(
            new IOException("a socket gave up"),
            new RateLimitExceededException());

        if (ProviderRetirement.RetirementReason(buried) is null)
        {
            return "a spent allowance behind an aggregate would be asked again for every item";
        }

        var nested = new InvalidOperationException("the provider failed", buried);
        return ProviderRetirement.RetirementReason(nested) is not null
            ? null
            : "a wall two levels down was missed";
    });

    Check("an ordinary failure is not a wall", () =>
        ProviderRetirement.RetirementReason(new AggregateException(new IOException("timeout"))) is null
            ? null
            : "a timeout retired the provider for the whole sweep");

    // ! Stop that provider, not the sweep. A spent allowance says nothing about the others.
    Check("a spent allowance retires one provider and the next still answers", () =>
    {
        var source = new StubSource(["Open Subtitles", "subbuzz"]);
        source.Results["Open Subtitles"] = [Offer("a1")];
        source.Results["subbuzz"] = [Offer("b1")];
        source.FetchThrows["a1"] = new RateLimitExceededException();

        var retirement = new ProviderRetirement();
        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        // ! A retired provider was charged nothing, since no download was made.
        if (run.Outcome.Fetches != 1)
        {
            return $"charged {run.Outcome.Fetches} downloads";
        }

        return retirement.ReasonFor("Open Subtitles") is not null
            ? null
            : "the spent provider was left live";
    });

    Check("refused credentials retire that provider too", () =>
    {
        var source = new StubSource(["Open Subtitles"]);
        source.Results["Open Subtitles"] = [Offer("a1")];
        source.FetchThrows["a1"] = new AuthenticationFailedException();

        var retirement = new ProviderRetirement();
        RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        return retirement.ReasonFor("Open Subtitles") is not null
            ? null
            : "a provider that refused the credentials was left live";
    });

    Check("an ordinary fetch failure does not retire the provider", () =>
    {
        var source = new StubSource(["Open Subtitles"]);
        source.Results["Open Subtitles"] = [Offer("a1"), Offer("a2")];
        source.FetchThrows["a1"] = new IOException("the connection dropped");

        var retirement = new ProviderRetirement();
        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        if (retirement.ReasonFor("Open Subtitles") is not null)
        {
            return "a transient failure retired the provider";
        }

        return run.Outcome.Result == AcquireResult.Kept ? null : $"ended {run.Outcome.Result}";
    });

    // ! An item with nowhere left to ask must not be failed; the panel would fill with one fact.
    Check("an item with every provider retired is set aside", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.Retire("Open Subtitles", "has spent its download allowance");

        var source = new StubSource(["Open Subtitles"]);
        source.Results["Open Subtitles"] = [Offer("a1")];

        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        if (run.Outcome.Result != AcquireResult.ProvidersRetired)
        {
            return $"ended {run.Outcome.Result}";
        }

        return source.Searched.Count == 0 ? null : "a retired provider was searched anyway";
    });

    Check("the next sweep asks a retired provider again", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.Retire("Open Subtitles", "has spent its download allowance");
        retirement.Reset();

        return retirement.ReasonFor("Open Subtitles") is null
            ? null
            : "a retirement outlived its sweep";
    });

    Console.WriteLine();
    Console.WriteLine("What happens when one source inside an aggregator stops answering?");

    // ! The case this exists for. One exhausted account inside subbuzz must not take the rest of
    //   subbuzz with it, and until this was built it did.
    Check("a wall on one source leaves its siblings answering", () =>
    {
        var source = new StubSource(["subbuzz"]);
        source.Results["subbuzz"] = [Aggregated("opensubtitles.com", "1"), Aggregated("subsource.net", "1")];
        source.FetchThrows[Aggregated("opensubtitles.com", "1").Id] = new RateLimitExceededException();

        var retirement = new ProviderRetirement();
        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        if (retirement.ReasonFor("subbuzz") is not null)
        {
            return "one source took the whole provider with it";
        }

        return retirement.ReasonFor("subbuzz", "opensubtitles.com") is not null
            ? null
            : "the walled source was left live";
    });

    // ! The offers behind a walled source are skipped before the fetch, so they cost nothing.
    Check("a walled source is not bought again further down the same list", () =>
    {
        var source = new StubSource(["subbuzz"]);
        source.Results["subbuzz"] =
        [
            Aggregated("opensubtitles.com", "1"),
            Aggregated("opensubtitles.com", "2"),
            Aggregated("subsource.net", "1")
        ];

        source.FetchThrows[Aggregated("opensubtitles.com", "1").Id] = new RateLimitExceededException();

        var run = RunWith(
            Config(),
            source,
            id => id.Contains("subsource.net", StringComparison.Ordinal)
                ? CandidateVerdict.Kept
                : CandidateVerdict.Misaligned);

        if (run.Outcome.Result != AcquireResult.Kept)
        {
            return $"ended {run.Outcome.Result}";
        }

        return run.Outcome.Fetches == 1 ? null : $"charged {run.Outcome.Fetches} downloads";
    });

    // ! Nothing is in flight during a search, so there is no source to charge the wall to.
    Check("a wall reached while searching still retires the whole provider", () =>
    {
        var source = new StubSource(["subbuzz", "Open Subtitles"]);
        source.SearchThrows["subbuzz"] = new RateLimitExceededException();
        source.Results["Open Subtitles"] = [Offer("a1")];

        var retirement = new ProviderRetirement();
        RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        return retirement.ReasonFor("subbuzz") is not null
            ? null
            : "a provider that walled the search was left live";
    });

    // ! A source retirement must never leak onto a provider that happens to share the name.
    Check("a retired source does not retire the provider that shares its name", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.RetireSource("subbuzz", "opensubtitles.com", "has spent its download allowance");

        if (retirement.ReasonFor("subbuzz") is not null)
        {
            return "the provider was retired by one of its sources";
        }

        if (retirement.ReasonFor("subbuzz", "subsource.net") is not null)
        {
            return "a sibling source was retired";
        }

        return retirement.Live(["subbuzz"]).Count == 1 ? null : "the provider stopped being asked";
    });

    // ! A walled provider carries its sources, or a per-source check would ask one of them again.
    Check("a walled provider answers for every source under it", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.Retire("subbuzz", "has spent its download allowance");

        return retirement.ReasonFor("subbuzz", "subsource.net") is not null
            ? null
            : "a source under a walled provider read as live";
    });

    Check("the next sweep asks a retired source again", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.RetireSource("subbuzz", "opensubtitles.com", "has spent its download allowance");
        retirement.Reset();

        return retirement.ReasonFor("subbuzz", "opensubtitles.com") is null
            ? null
            : "a source retirement outlived its sweep";
    });

    Console.WriteLine();
    Console.WriteLine("Which source produced a result?");

    Check("the source is read off the id the label agrees with", () =>
        SubtitleSourceKey.For(Aggregated("opensubtitles.com", "1")) == "opensubtitles.com"
            ? null
            : "the source went unread");

    // ! The label is free text. Retiring on a token the id never confirms walls the wrong source.
    Check("a label the id does not confirm names no source", () =>
    {
        var invented = Aggregated("opensubtitles.com", "1");
        invented.Comment = "[subsource.net] release group";
        invented.ProviderName = "[subbuzz] subsource.net";
        invented.Name = "subsource.net";

        return SubtitleSourceKey.For(invented) is null ? null : "an unconfirmed label became a key";
    });

    // ! Aggregators bold the source. A tag left in matches no id and the source is silently lost.
    Check("markup around the source is stripped", () =>
    {
        var marked = Aggregated("opensubtitles.com", "1");
        marked.Comment = null;
        marked.ProviderName = "[subbuzz] <b>OpenSubtitles.com</b>";
        marked.Name = "a release name";

        return SubtitleSourceKey.For(marked) == "opensubtitles.com"
            ? null
            : "a bolded source went unread";
    });

    Check("a result carrying no source at all names none", () =>
        SubtitleSourceKey.For(Offer("a1")) is null ? null : "a plain result invented a source");

    // ! The costly direction. An invented source narrows a wall that should have stopped the whole
    //   provider, so a spent account goes on being asked for every offer behind it.
    Check("an ordinary provider whose label prefixes its own id names no source", () =>
    {
        var plain = Offer("12345_abcdef");
        plain.ProviderName = "abc";
        plain.Comment = "abc";

        return SubtitleSourceKey.For(plain) is null ? null : "a single-source provider invented one";
    });

    Check("a label the id confirms but no aggregator would use names no source", () =>
    {
        var spaced = Offer("12345_some release group");
        spaced.Comment = "[subbuzz] some release group";

        return SubtitleSourceKey.For(spaced) is null ? null : "a free-text label became a key";
    });

    Console.WriteLine();
    Console.WriteLine("What does an item behind a walled source report?");

    // ! These were buyable. Reporting them unusable blames the subtitles for a spent allowance,
    //   and the panel is the only view the user has.
    Check("a list entirely behind a walled source is set aside as unavailable", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.RetireSource("subbuzz", "opensubtitles.com", "has spent its download allowance");

        var source = new StubSource(["subbuzz"]);
        source.Results["subbuzz"] = [Aggregated("opensubtitles.com", "1"), Aggregated("opensubtitles.com", "2")];

        var run = RunWith(Config(), source, _ => CandidateVerdict.Kept, retirement);

        if (run.Outcome.Result != AcquireResult.ProvidersRetired)
        {
            return $"ended {run.Outcome.Result}";
        }

        if (run.Outcome.Fetches != 0)
        {
            return $"charged {run.Outcome.Fetches} downloads";
        }

        return run.Outcome.RefusedByAudio ? "a wall was reported as an audio refusal" : null;
    });

    // ! Anything the check actually decided outranks the wall; that is the more useful reason.
    Check("a refusal alongside a walled source still reports the refusal", () =>
    {
        var retirement = new ProviderRetirement();
        retirement.RetireSource("subbuzz", "opensubtitles.com", "has spent its download allowance");

        var source = new StubSource(["subbuzz"]);
        source.Results["subbuzz"] = [Aggregated("opensubtitles.com", "1"), Aggregated("subsource.net", "1")];

        var run = RunWith(Config(), source, _ => CandidateVerdict.Misaligned, retirement);

        return run.Outcome.Result == AcquireResult.Exhausted ? null : $"ended {run.Outcome.Result}";
    });

    Check("the wall is read off the exception type, whatever the provider threw", () =>
    {
        if (ProviderRetirement.RetirementReason(new InvalidOperationException("nothing to see")) is not null)
        {
            return "an ordinary error was read as a wall";
        }

        var wrapped = new InvalidOperationException("outer", new RateLimitExceededException());
        return ProviderRetirement.RetirementReason(wrapped) is not null
            ? null
            : "a wrapped rate limit went unread";
    });
}

// ---------------------------------------------------------------- the ledger

void Ledger()
{
    Console.WriteLine();
    Console.WriteLine("What does the ledger remember?");

    Check("every fetch lands in the ledger with its verdict", () =>
    {
        var run = Run(Config(), [Offer("a"), Offer("b")], _ => CandidateVerdict.Misaligned);
        var attempts = run.Record.AcquireAttempts;

        if (attempts.Count != 2)
        {
            return $"recorded {attempts.Count} attempts";
        }

        return attempts.TrueForAll(a => a.Outcome == AcquireAttemptOutcome.Misaligned)
            ? null
            : "an attempt lost its verdict";
    });

    Check("a filtered candidate never reaches the ledger", () =>
    {
        var run = Run(Config(), [Offer("a", forced: true), Offer("b")], _ => CandidateVerdict.Kept);

        return run.Record.AcquireAttempts.Count == 1
            ? null
            : $"recorded {run.Record.AcquireAttempts.Count} attempts";
    });

    // ! What makes a second run cost nothing where the first one failed.
    Check("a candidate already in the ledger is never bought twice", () =>
    {
        var record = NewRecord();
        record.AcquireAttempts.Add(new AcquireAttempt
        {
            SubtitleId = "a",
            ProviderName = "Open Subtitles",
            Outcome = AcquireAttemptOutcome.Misaligned
        });

        var run = Run(Config(), [Offer("a"), Offer("b")], _ => CandidateVerdict.Kept, record: record);

        if (run.Outcome.Fetches != 1)
        {
            return $"made {run.Outcome.Fetches} downloads";
        }

        return run.Record.AcquireAttempts[^1].SubtitleId == "b"
            ? null
            : "the rejected candidate was bought again";
    });

    // ! A re-uploaded fix carries a new id, so it has never been tried.
    Check("a re-uploaded candidate under a new id is offered", () =>
    {
        var record = NewRecord();
        record.AcquireAttempts.Add(new AcquireAttempt { SubtitleId = "a" });

        var run = Run(Config(), [Offer("a-v2")], _ => CandidateVerdict.Kept, record: record);
        return run.Outcome.Fetches == 1 ? null : "a new id was refused as already tried";
    });

    Check("the provider that answered is recorded with the attempt", () =>
    {
        var run = Run(Config(), [Offer("a")], _ => CandidateVerdict.Kept);

        return run.Record.AcquireAttempts[0].ProviderName == "Open Subtitles"
            ? null
            : $"recorded {run.Record.AcquireAttempts[0].ProviderName}";
    });

    Check("a kept hash match reports the strongest confidence", () =>
    {
        var run = Run(Config(), [Offer("a", hash: true)], _ => CandidateVerdict.Kept);

        return run.Outcome.Confidence == 1d ? null : $"reported {run.Outcome.Confidence}";
    });

    Check("a kept candidate further down the list reports less", () =>
    {
        var run = Run(Config(), [Offer("a"), Offer("b")], id => id == "b"
            ? CandidateVerdict.Kept
            : CandidateVerdict.Misaligned);

        return run.Outcome.Confidence < 1d ? null : $"reported {run.Outcome.Confidence}";
    });
}

// ---------------------------------------------------------------- fixtures

// The row the orchestrator would store, so the grouping is read the way the panel reads it.
static SyncRecord Panel(AcquireOutcome outcome) => new()
{
    Status = SyncStatus.Failed,
    RefusedByAudio = outcome.RefusedByAudio,
    Message = outcome.Message
};

// A result from an aggregator: the source is in the id, and in the labels it stamps beside it.
static RemoteSubtitleInfo Aggregated(string source, string tail)
{
    var offer = Offer("0123456789abcdef0123456789abcdef_" + source + "eyJJZCI6" + tail);
    offer.ProviderName = "[subbuzz] " + source;
    offer.Comment = "[" + source + "] release group";
    return offer;
}

// Whether a download the check could not measure has to be confirmed before it is saved.
static PluginConfiguration Conclusive(bool on)
{
    var config = Config();
    config.RequireConclusiveDownloads = on;
    return config;
}

static PluginConfiguration Config()
{
    var config = new PluginConfiguration
    {
        AcquireMissingSubtitles = true,
        LanguageAllowList = ["eng"]
    };

    config.Normalize();
    return config;
}

static PluginConfiguration WithSdh()
{
    var config = Config();
    config.AcquireHearingImpaired = true;
    return config;
}

static PluginConfiguration Wants(params string[] languages)
    => new()
    {
        AcquireMissingSubtitles = true,
        LanguageAllowList = languages
    };

static List<string> Gaps(PluginConfiguration config, MediaStream stream)
    => SubtitleDiscoveryService.Gaps([stream], config) ?? ["<unlabelled>"];

static MediaStream Stream(
    string? language,
    bool external,
    bool forced = false,
    bool sdh = false,
    string codec = "subrip",
    string? path = null)
    => new()
    {
        Type = MediaStreamType.Subtitle,
        Language = language,
        IsExternal = external,
        IsForced = forced,
        IsHearingImpaired = sdh,
        Codec = codec,
        Path = path
    };

static MediaStream External(string language) => Stream(language, external: true);

static MediaStream Embedded(string language) => Stream(language, external: false);

static MediaStream Forced(string language) => Stream(language, external: true, forced: true);

static MediaStream Sdh(string language) => Stream(language, external: true, sdh: true);

static MediaStream Image(string language)
    => Stream(language, external: true, codec: "hdmv_pgs_subtitle");

static MediaStream Ours(string language)
    => Stream(language, external: true, path: @"C:\m\Movie (2001).eng.autosubsync.srt");

static RemoteSubtitleInfo Offer(
    string id,
    bool forced = false,
    bool sdh = false,
    bool hash = false,
    bool ai = false,
    bool machine = false,
    string format = "srt",
    string? name = null)
    => new()
    {
        Id = id,
        Name = name ?? id,
        ProviderName = "Open Subtitles",
        Format = format,
        ThreeLetterISOLanguageName = "eng",
        Forced = forced ? true : null,
        HearingImpaired = sdh ? true : null,
        IsHashMatch = hash ? true : null,
        AiTranslated = ai ? true : null,
        MachineTranslated = machine ? true : null
    };

static SyncRecord NewRecord() => new()
{
    Id = Guid.NewGuid(),
    ItemId = Guid.NewGuid(),
    ItemName = "Movie (2001)",
    TargetKey = SubtitleTarget.AcquireKey("eng"),
    Origin = SubtitleOrigin.Acquired
};

// ---------------------------------------------------------------- the driver

int Spent(RemoteSubtitleInfo offer)
    => Run(Config(), [offer], _ => CandidateVerdict.Kept).Outcome.Fetches;

// The ids the loop actually fetched, in the order it fetched them.
List<string> Order(PluginConfiguration config, RemoteSubtitleInfo[] offers)
{
    config.MaxDownloadsPerItem = 0;

    var seen = new List<string>();
    Run(config, offers, id =>
    {
        seen.Add(id);
        return CandidateVerdict.Misaligned;
    });

    return seen;
}

(AcquireOutcome Outcome, SyncRecord Record) Run(
    PluginConfiguration config,
    RemoteSubtitleInfo[] offers,
    Func<string, CandidateVerdict> judge,
    bool sdhBytes = false,
    string[]? providers = null,
    SyncRecord? record = null,
    SubtitleTarget? target = null)
{
    var source = new StubSource(providers ?? ["Open Subtitles"]);

    foreach (var provider in source.Providers)
    {
        source.Results[provider] = offers;
    }

    return RunWith(config, source, judge, null, sdhBytes, record, target);
}

(AcquireOutcome Outcome, SyncRecord Record) RunWith(
    PluginConfiguration config,
    StubSource source,
    Func<string, CandidateVerdict> judge,
    ProviderRetirement? retirement = null,
    bool sdhBytes = false,
    SyncRecord? record = null,
    SubtitleTarget? into = null)
{
    // ! Real bytes, so the detector is the shipping one reading a file it would see in the field.
    source.Body = sdhBytes ? SdhCues : PlainCues;

    var acquirer = new SubtitleAcquirer(
        source,
        retirement ?? new ProviderRetirement(),
        NullLogger<SubtitleAcquirer>.Instance);

    var target = into ?? new SubtitleTarget();

    target.ItemId = Guid.NewGuid();
    target.ItemName = "Movie (2001)";
    target.VideoPath = @"C:\m\Movie (2001).mkv";
    target.Origin = SubtitleOrigin.Acquired;
    target.Language = "eng";
    target.Key = SubtitleTarget.AcquireKey("eng");

    var row = record ?? NewRecord();

    var outcome = acquirer.RunAsync(
        target,
        row,
        config,
        extension =>
        {
            var path = Path.Combine(sandbox, Guid.NewGuid().ToString("N") + extension);
            source.Wrote(path);
            return path;
        },
        (path, _) => Task.FromResult(judge(source.IdFor(path))),
        CancellationToken.None).GetAwaiter().GetResult();

    return (outcome, row);
}

// A provider that answers from memory. Nothing here reaches a network.
internal sealed class StubSource : ISubtitleSource
{
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);
    private string _lastFetched = string.Empty;

    public StubSource(IReadOnlyList<string> providers) => Providers = providers;

    public IReadOnlyList<string> Providers { get; }

    public Dictionary<string, RemoteSubtitleInfo[]> Results { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Exception> FetchThrows { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Exception> SearchThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Searched { get; } = [];

    public string Body { get; set; } = string.Empty;

    public IReadOnlyList<ProviderInfo>? Survey(PluginConfiguration config)
        => Providers.Select(p => new ProviderInfo(p, true, true)).ToList();

    public IReadOnlyList<string> Downloaders(Guid itemId, PluginConfiguration config) => Providers;

    public Task<IReadOnlyList<RemoteSubtitleInfo>> SearchAsync(
        Guid itemId,
        string provider,
        string language,
        CancellationToken cancellationToken)
    {
        if (SearchThrows.TryGetValue(provider, out var error))
        {
            throw error;
        }

        Searched.Add(provider);

        return Task.FromResult<IReadOnlyList<RemoteSubtitleInfo>>(
            Results.TryGetValue(provider, out var found) ? found : []);
    }

    public Task<SubtitleResponse?> FetchAsync(string subtitleId, CancellationToken cancellationToken)
    {
        if (FetchThrows.TryGetValue(subtitleId, out var error))
        {
            throw error;
        }

        _lastFetched = subtitleId;

        return Task.FromResult<SubtitleResponse?>(new SubtitleResponse
        {
            Format = "srt",
            Language = "eng",
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Body))
        });
    }

    // ! The scratch path is allocated after the fetch, so this is the file that fetch produced.
    public void Wrote(string path) => _written[path] = _lastFetched;

    public string IdFor(string path) => _written.GetValueOrDefault(path, string.Empty);
}

// ! Named for what the provider plugins throw. The reason is read off the type name alone.
internal sealed class RateLimitExceededException : Exception
{
}

internal sealed class AuthenticationFailedException : Exception
{
}
