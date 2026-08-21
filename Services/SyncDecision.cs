using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Which branch closed a synced result. The caller logs the line that matches.
public enum SyncDecisionKind
{
    Accept = 0,

    // Accepted after the coarse reading released an unmeasured rescale.
    AcceptStretchReleased = 1,

    Misaligned = 2,

    // The engine rescaled the subtitle and the check never measured drift.
    UnmeasuredStretch = 3,

    // No verdict, and the engine moved the subtitle further than is accepted unconfirmed.
    UnverifiedShift = 4,

    // No verdict, and audio confirmation is required.
    NoVerdict = 5,

    // No verdict, and the engine never scored its own alignment.
    NoEngineScore = 6,

    // No verdict, and the engine scored its own alignment below the floor.
    LowEngineScore = 7
}

// What the gates decided about one engine result, and what the record should carry.
public readonly record struct SyncDecision(
    SyncDecisionKind Kind,
    string? Message,
    long? RejectedOffsetMs,
    double? EngineScore)
{
    public bool Accepted => Kind is SyncDecisionKind.Accept or SyncDecisionKind.AcceptStretchReleased;
}

// The verdict-to-decision gates, shared by the sync path and the acquire path.
public static class SyncDecisionMaker
{
    // ! Cannot-align scores are about 10 a second, can-align 40 and up. Set at the lowest real
    //   reading: an unmeasurable title must clear what a genuine alignment scores.
    public const double MinimumEngineScore = 40;

    // Ceiling on a move nothing verified. Only reachable with audio confirmation turned off.
    public const long MaximumUnverifiedShiftMs = 60_000;

    // ! engineScore is called at most once, and only where a gate or the debug line reads it.
    //   It parses the produced file.
    public static SyncDecision Decide(
        VerificationResult verdict,
        OffsetChange change,
        Func<double?> engineScore,
        bool scoreWanted,
        PluginConfiguration config)
    {
        if (verdict.Verdict == SyncVerdict.Misaligned)
        {
            var drifting = verdict.DriftMs is { } spread && Math.Abs(spread) > SyncVerifier.DriftWithinMs;

            // ! Signed. The retroactivity hook reads it against a bound centred on the
            //   authored lead, and a magnitude cannot be judged against that.
            var miss = drifting ? verdict.DriftMs!.Value : verdict.BestShiftMs ?? 0;

            return new SyncDecision(
                SyncDecisionKind.Misaligned,
                drifting
                    ? "Rejected: the audio check found the offset drifting across the runtime."
                    : "Rejected: the audio check found the subtitle out of alignment.",
                miss,
                null);
        }

        var released = false;

        // ! Drift goes unmeasured on an Inconclusive verdict and on any title too short for
        //   six windows. Hold an unchecked stretch to the tolerance the check applies.
        if (verdict.DriftMs is null
            && change.DriftMs is { } stretch
            && Math.Abs(stretch) > SyncVerifier.DriftWithinMs)
        {
            if (!SyncVerifier.ReleasedByCoarseDrift(verdict))
            {
                // ! Two reasons, not one. A title too short to plan six windows can never be
                //   measured; one that planned them and reached no fit is a different refusal.
                var tooShort = verdict.Windows < SyncVerifier.DriftWindows;

                return new SyncDecision(
                    SyncDecisionKind.UnmeasuredStretch,
                    tooShort
                        ? "Rejected: the sync engine rescaled the subtitle across the runtime — this "
                          + "title is too short for the audio check to measure drift."
                        : "Rejected: the sync engine rescaled the subtitle across the runtime — the "
                          + "audio check could not measure drift on this title.",
                    stretch,
                    null);
            }

            released = true;
        }

        // ! Backstop for a check that confirmed nothing. Not a tight leash: reaching here means
        //   audio confirmation is off, and a sidecar for another release is legitimately late.
        if (verdict.Verdict == SyncVerdict.Inconclusive
            && change.ConstantMs is { } shift
            && Math.Abs(shift) > MaximumUnverifiedShiftMs)
        {
            return new SyncDecision(
                SyncDecisionKind.UnverifiedShift,
                "Rejected: the audio check reached no verdict and the sync engine moved the "
                + "subtitle too far to accept unconfirmed.",
                shift,
                null);
        }

        double? confidence = null;

        // ! Only where our own check could not measure the title, and only to refuse. The
        //   engine scoring its own alignment is not evidence that it is right.
        if (verdict.Verdict == SyncVerdict.Inconclusive)
        {
            // ! The check ran and returned no answer, which is not the same as a pass. Where
            //   confirmation is required that ends it, and the score is never read.
            if (config.RequireAudioConfirmation)
            {
                return new SyncDecision(
                    SyncDecisionKind.NoVerdict,
                    SyncOutcome.NoVerdictRefusal,
                    null,
                    null);
            }

            confidence = engineScore();

            if (confidence is not { } tooLow)
            {
                return new SyncDecision(
                    SyncDecisionKind.NoEngineScore,
                    "Rejected: the audio check could not measure this title and the sync engine "
                    + "never scored its alignment.",
                    null,
                    null);
            }

            if (tooLow < MinimumEngineScore)
            {
                return new SyncDecision(
                    SyncDecisionKind.LowEngineScore,
                    "Rejected: the audio check could not measure this title and the sync engine "
                    + "found no usable alignment.",
                    null,
                    confidence);
            }
        }
        else if (scoreWanted)
        {
            confidence = engineScore();
        }

        return new SyncDecision(
            released ? SyncDecisionKind.AcceptStretchReleased : SyncDecisionKind.Accept,
            null,
            null,
            confidence);
    }
}
