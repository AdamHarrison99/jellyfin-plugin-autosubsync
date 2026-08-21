using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// How the pipeline judged one fetched candidate.
public enum CandidateVerdict
{
    Kept = 0,
    Misaligned = 1,
    Inconclusive = 2,
    Failed = 3
}

// Why the acquire loop stopped.
public enum AcquireResult
{
    Kept = 0,
    NothingOffered = 1,
    HearingImpairedOnly = 2,
    AllFiltered = 3,
    Exhausted = 4,
    CapReached = 5,
    ProvidersRetired = 7,

    // ! Every offer had already been downloaded and judged. The row keeps the verdict it holds.
    NothingNew = 8
}

public sealed record AcquireOutcome(
    AcquireResult Result,
    string? Message,
    int Fetches,
    double? Confidence,
    bool RefusedByAudio)
{
    // ! Every provider answered and its whole list was seen. A wall or a search failure leaves
    //   the language still worth asking about.
    public bool Answered { get; init; }
}

// Searches each provider in turn for a language the item has nothing in, one file at a time.
public class SubtitleAcquirer
{
    private readonly ISubtitleSource _source;
    private readonly ProviderRetirement _retirement;
    private readonly ILogger<SubtitleAcquirer> _logger;

    public SubtitleAcquirer(
        ISubtitleSource source,
        ProviderRetirement retirement,
        ILogger<SubtitleAcquirer> logger)
    {
        _source = source;
        _retirement = retirement;
        _logger = logger;
    }

    private sealed record Offer(RemoteSubtitleInfo Info, int Position);

    private sealed class Tally
    {
        public int Raw { get; set; }

        public int Offered { get; set; }

        public int SdhFiltered { get; set; }

        public int Fetches { get; set; }

        public int Refusals { get; set; }

        public int Abstentions { get; set; }

        public int Walled { get; set; }

        public int AlreadyTried { get; set; }

        public int Failures { get; set; }

        public int SearchFailures { get; set; }
    }

    public async Task<AcquireOutcome> RunAsync(
        SubtitleTarget target,
        SyncRecord record,
        PluginConfiguration config,
        Func<string, string> allocateScratch,
        Func<string, CancellationToken, Task<CandidateVerdict>> judge,
        CancellationToken cancellationToken)
    {
        if (target.Language is not { Length: > 0 } language)
        {
            return Set(AcquireResult.NothingOffered, 0);
        }

        var providers = _retirement.Live(_source.Downloaders(target.ItemId, config));

        if (providers.Count == 0)
        {
            return new AcquireOutcome(
                AcquireResult.ProvidersRetired,
                $"Set aside: no subtitle provider is available — {_retirement.Summary()}.",
                0,
                null,
                false);
        }

        var tally = new Tally();

        foreach (var provider in providers)
        {
            if (await SearchAsync(target, provider, language, cancellationToken).ConfigureAwait(false)
                is not { } offers)
            {
                tally.SearchFailures++;
                continue;
            }

            tally.Raw += offers.Count;

            foreach (var offer in Rank(offers, record, config, tally))
            {
                if (config.MaxDownloadsPerItem > 0 && tally.Fetches >= config.MaxDownloadsPerItem)
                {
                    // ! The audio check never saw these. Naming the limit blames a stage the
                    //   item never reached.
                    if (tally.Refusals == 0 && tally.Failures == 0 && tally.SdhFiltered > 0)
                    {
                        return new AcquireOutcome(
                            AcquireResult.HearingImpairedOnly,
                            "Set aside: the per-item download limit was reached, and every subtitle "
                            + "downloaded for this language is hearing-impaired.",
                            tally.Fetches,
                            null,
                            false);
                    }

                    // ! Set aside, never failed. The allowance stopped this item, and raising the
                    //   limit is what releases it.
                    return new AcquireOutcome(
                        AcquireResult.CapReached,
                        "Set aside: the per-item download limit was reached before a subtitle could "
                        + "be confirmed against the audio.",
                        tally.Fetches,
                        null,
                        false);
                }

                // ! Costs nothing and is checked per offer. A wall on one internal source of an
                //   aggregator leaves the rest of its list worth buying.
                if (_retirement.ReasonFor(provider, SubtitleSourceKey.For(offer.Info)) is not null)
                {
                    tally.Walled++;
                    continue;
                }

                var fetched = await FetchAsync(offer.Info, provider, allocateScratch, cancellationToken)
                    .ConfigureAwait(false);

                // ! A provider that hit a wall is charged nothing. No download was made.
                if (fetched.Retired)
                {
                    // ! Counted here as well as before the fetch. A wall usually surfaces on the
                    //   first fetch, and an uncounted one reads as a language answered in full.
                    tally.Walled++;

                    // ! Only a wall the whole provider is behind ends its list. One source's wall
                    //   is skipped by the check above on the offers that follow it.
                    if (_retirement.ReasonFor(provider) is not null)
                    {
                        break;
                    }

                    continue;
                }

                tally.Fetches++;

                if (fetched.Path is not { } path)
                {
                    tally.Failures++;
                    Ledger(record, offer.Info, provider, AcquireAttemptOutcome.Failed);
                    continue;
                }

                // ! Names the file that is placed. Only the kept candidate ever reaches the placer.
                target.IsHearingImpaired = offer.Info.HearingImpaired == true;

                // ! On the bytes in hand, never on the advertisement. The download is already spent.
                if (!config.AcquireHearingImpaired
                    && SdhDetector.Inspect(path) is { IsHearingImpaired: true } marks)
                {
                    tally.SdhFiltered++;
                    Ledger(record, offer.Info, provider, AcquireAttemptOutcome.HearingImpaired);

                    // ! The counts, not a sample. Subtitle content is never logged.
                    _logger.LogDebug(
                        "{Item}: discarded a {Provider} candidate the detector reads as hearing-impaired"
                        + ", {Marked} of {Total} cues marked ({Ratio:P1})",
                        target.ItemName,
                        provider,
                        marks.MarkedCueCount,
                        marks.CueCount,
                        marks.Ratio);
                    continue;
                }

                var verdict = await judge(path, cancellationToken).ConfigureAwait(false);
                Ledger(record, offer.Info, provider, Map(verdict));

                if (verdict == CandidateVerdict.Kept)
                {
                    _logger.LogInformation(
                        "Acquired a {Language} subtitle for {Item} from {Provider}, {Fetches} downloaded",
                        language,
                        target.ItemName,
                        provider,
                        tally.Fetches);

                    return new AcquireOutcome(AcquireResult.Kept, null, tally.Fetches, Confidence(offer), false);
                }

                // ! The ledger keeps the verdict the check actually reached, whatever the setting
                //   does with it.
                if (verdict is CandidateVerdict.Misaligned or CandidateVerdict.Inconclusive)
                {
                    tally.Refusals++;

                    if (verdict == CandidateVerdict.Inconclusive)
                    {
                        tally.Abstentions++;
                    }
                }
                else
                {
                    tally.Failures++;
                }
            }
        }

        // ! A download that produced no file was refused by nothing. One wording over both puts
        //   the same sentence on two cards at once.
        if (tally.Failures > 0)
        {
            return new AcquireOutcome(
                AcquireResult.Exhausted,
                "Failed: no subtitle offered for this language could be downloaded.",
                tally.Fetches,
                null,
                false);
        }

        if (tally.Refusals > 0)
        {
            return new AcquireOutcome(
                AcquireResult.Exhausted,
                tally.Abstentions == tally.Refusals
                    ? SyncOutcome.NoVerdictExhausted(language)
                    : "Failed: every subtitle offered for this language was refused by the audio check.",
                tally.Fetches,
                null,
                true);
        }

        // ! What the providers actually settled. The next scan reads it and stays quiet.
        var answered = tally.SearchFailures == 0 && tally.Walled == 0;

        // ! Carries the fetches it spent. A post-fetch discard is still a download made.
        if (tally.SdhFiltered > 0)
        {
            return Set(AcquireResult.HearingImpairedOnly, tally.Fetches, answered);
        }

        // ! A provider that threw offered nothing measurable. Reporting it as an empty answer
        //   states something the plugin never established.
        if (tally.Raw == 0 && tally.SearchFailures > 0)
        {
            return new AcquireOutcome(
                AcquireResult.NothingOffered,
                "Set aside: no subtitle provider could be searched for this language.",
                tally.Fetches,
                null,
                false);
        }

        // ! Last, so anything the plugin actually learned about a file outranks it. These were
        //   usable; naming them unusable blames the subtitles for a spent allowance.
        if (tally.Walled > 0)
        {
            return new AcquireOutcome(
                AcquireResult.ProvidersRetired,
                "Set aside: every subtitle offered for this language came from a provider that has "
                + "stopped answering this scan.",
                tally.Fetches,
                null,
                false);
        }

        // ! Nothing here was judged this run, so nothing here may restate the row. Overwriting a
        //   refusal with a set-aside empties the card that refusal belongs on.
        if (answered && tally.Fetches == 0 && tally.Offered == 0 && tally.AlreadyTried > 0)
        {
            return new AcquireOutcome(AcquireResult.NothingNew, null, 0, null, false)
            {
                Answered = true
            };
        }

        return tally.Raw == 0
            ? Set(AcquireResult.NothingOffered, tally.Fetches, answered)
            : Set(AcquireResult.AllFiltered, tally.Fetches, answered);
    }

    private static AcquireOutcome Set(AcquireResult result, int fetches, bool answered = false)
        => new(result, MessageFor(result), fetches, null, false) { Answered = answered };

    private static string MessageFor(AcquireResult result)
        => result switch
        {
            AcquireResult.HearingImpairedOnly =>
                "Set aside: every subtitle offered for this language is hearing-impaired.",
            AcquireResult.AllFiltered =>
                "Set aside: no subtitle offered for this language could be used.",
            _ => "Set aside: no subtitle was offered for this language."
        };

    // Null where the provider could not be asked at all.
    private async Task<IReadOnlyList<RemoteSubtitleInfo>?> SearchAsync(
        SubtitleTarget target,
        string provider,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _source
                .SearchAsync(target.ItemId, provider, language, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ! A search failure is this provider offering nothing, never an item failure. No
            //   candidate is in flight, so there is nothing to charge a wall to but the provider.
            if (!Retire(provider, null, ex) && _retirement.NoteFailure(provider))
            {
                _logger.LogWarning(ex, "{Provider} could not be searched and answered nothing", provider);
            }

            _logger.LogDebug(ex, "{Provider} could not be searched for {Item}", provider, target.ItemName);
            return null;
        }
    }

    private readonly record struct Fetched(string? Path, bool Retired);

    private async Task<Fetched> FetchAsync(
        RemoteSubtitleInfo candidate,
        string provider,
        Func<string, string> allocateScratch,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _source.FetchAsync(candidate.Id, cancellationToken).ConfigureAwait(false);

            if (response?.Stream is not { } stream)
            {
                return new Fetched(null, false);
            }

            await using (stream.ConfigureAwait(false))
            {
                var extension = Extension(response.Format) ?? Extension(candidate.Format);

                if (extension is null || !SyncEngine.Supports(extension))
                {
                    return new Fetched(null, false);
                }

                var path = allocateScratch(extension);
                var file = File.Create(path);

                await using (file.ConfigureAwait(false))
                {
                    await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                return new Fetched(path, false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retired = Retire(provider, SubtitleSourceKey.For(candidate), ex);

            if (!retired)
            {
                _logger.LogWarning(ex, "{Provider} could not deliver subtitle {Id}", provider, candidate.Id);
            }

            return new Fetched(null, retired);
        }
    }

    // ! A null source retires the whole provider. Naming one narrows the wall to that source, so
    //   it is passed only where a candidate in flight says which source the fetch went to.
    private bool Retire(string provider, string? source, Exception error)
    {
        if (ProviderRetirement.RetirementReason(error) is not { } reason)
        {
            return false;
        }

        if (_retirement.ReasonFor(provider, source) is null)
        {
            _logger.LogWarning(
                "{Provider} {Reason}; it will not be asked again this scan",
                source is null ? provider : provider + "/" + source,
                reason);
        }

        if (source is null)
        {
            _retirement.Retire(provider, reason);
        }
        else
        {
            _retirement.RetireSource(provider, source, reason);
        }

        return true;
    }

    // The order the provider returned, with one promotion on top of it.
    private static List<Offer> Rank(
        IReadOnlyList<RemoteSubtitleInfo> offers,
        SyncRecord record,
        PluginConfiguration config,
        Tally tally)
    {
        var kept = new List<Offer>();

        for (var i = 0; i < offers.Count; i++)
        {
            var info = offers[i];

            if (string.IsNullOrWhiteSpace(info.Id))
            {
                continue;
            }

            // ! Free, so it must never consume the per-item budget.
            if (!config.AcquireHearingImpaired
                && (info.HearingImpaired == true || SdhNaming.IsHearingImpaired(info.Name)))
            {
                tally.SdhFiltered++;
                continue;
            }

            // ! Counted apart from the rest. A file this target already downloaded is not an
            //   unusable one, and the outcome says so.
            if (Tried(info, record))
            {
                tally.AlreadyTried++;
                continue;
            }

            if (Filtered(info, record, config))
            {
                continue;
            }

            kept.Add(new Offer(info, i));
        }

        tally.Offered += kept.Count;

        // ! Stable. Re-sorting the rest fights an order the provider made with better information.
        return kept.OrderByDescending(offer => offer.Info.IsHashMatch == true).ToList();
    }

    private static bool Filtered(RemoteSubtitleInfo info, SyncRecord record, PluginConfiguration config)
    {
        // A forced track carries signs and songs, and cannot answer for the language.
        if (info.Forced == true)
        {
            return true;
        }

        // ! Null means the provider did not say, never no.
        if (info.AiTranslated == true || info.MachineTranslated == true)
        {
            return true;
        }

        if (Extension(info.Format) is { } extension && !SyncEngine.Supports(extension))
        {
            return true;
        }

        return false;
    }

    // ! The provider-scoped id, which a re-upload changes. A download already spent on this file
    //   is never spent on it twice.
    private static bool Tried(RemoteSubtitleInfo info, SyncRecord record)
        => record.AcquireAttempts.Exists(
            attempt => string.Equals(attempt.SubtitleId, info.Id, StringComparison.Ordinal));

    private static string? Extension(string? format)
        => string.IsNullOrWhiteSpace(format) ? null : "." + format.Trim().TrimStart('.').ToLowerInvariant();

    // What put this candidate at the top: the file hash, or the provider's own ranking.
    private static double Confidence(Offer offer)
        => offer.Info.IsHashMatch == true ? 1d : 1d / (offer.Position + 2);

    private static void Ledger(
        SyncRecord record,
        RemoteSubtitleInfo info,
        string provider,
        AcquireAttemptOutcome outcome)
        => record.AcquireAttempts.Add(new AcquireAttempt
        {
            SubtitleId = info.Id,
            ProviderName = provider,
            AttemptedUtc = DateTime.UtcNow,
            Outcome = outcome
        });

    private static AcquireAttemptOutcome Map(CandidateVerdict verdict)
        => verdict switch
        {
            CandidateVerdict.Kept => AcquireAttemptOutcome.Kept,
            CandidateVerdict.Misaligned => AcquireAttemptOutcome.Misaligned,
            CandidateVerdict.Inconclusive => AcquireAttemptOutcome.Inconclusive,
            _ => AcquireAttemptOutcome.Failed
        };
}
