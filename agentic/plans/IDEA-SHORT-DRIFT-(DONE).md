# IDEA-SHORT-DRIFT — investigation record — **IMPLEMENTED**

> Status: **DONE**, 2026-08-19. Everything in the implementation checklist below is in the repo
> except the items named under *Knowingly left undone*. `verify.ps1` green, audited as the
> thirty-eighth pass (`T1`). ¬committed — the repo rules require explicit approval and none has
> been given.
>
> **The proposal in one line:** at the stretch guard, where the post-sync verdict is already
> `Aligned` and a two-window-a-side drift is both measurable and within a new raw bound, release the
> result instead of refusing it. `DriftWindows` stays at 6. Nothing else changes.
>
> **What shipped:** `CoarseDriftWithinMs = 300` and `CoarseDriftMs` on `VerificationResult` ·
> the `Score` split into a judged drift and a carried coarse one · `SyncVerifier.ReleasedByCoarseDrift`
> · the release path in `SyncOrchestrator`'s stretch guard w/ an Information-level log · nine
> `verifycheck` cases, each mutation-proven. Documented in `ARCHITECTURE.md` (`SyncVerifier` *The
> drift test*, the gate-constants line, `SyncOrchestrator`) and `AUDIT.md` (thirty-eighth pass).
>
> **Four decisions taken during implementation, ¬in the plan below.**
> ① **C was dropped.** The user chose **no retroactivity lever at all**: the rows already refused stay
> `Failed` until *Retry failed subtitles* reopens them. ! A5 below is **wrong** on its own narrow
> claim — `CheckRevision` is ¬the only lever. `SyncRecord.CurrentMeasurementVersion` reopens `Failed`
> rows carrying a `RejectedOffsetMs` at load (≈356 rows, ¬1898), and the config-page button reopens
> every `Failed` row. Neither costs a full re-scan; neither was needed.
> ② **The predicate lives on `SyncVerifier`, ¬`SyncOrchestrator`**, beside `IsAligned`, which is the
> same shape — a pure predicate over a measurement, called from the guard. ∵ `SyncOrchestrator` is
> unlinkable from any harness, a private helper there would have left five of the eight D cases
> covered by inspection alone. On `SyncVerifier` all nine **execute**.
> ③ **The coarse fit runs only on an `Aligned` verdict**, built inside the returned result rather
> than computed up front as the shape at *Design* has it. Measured: the eager form roughly **doubled**
> the cost of scoring a short title (37.0 s → 64–76 s over 935 `Score` calls; 39.8 s w/ this).
> Every release decision is identical, and it makes *carried, never judged* structural — the value is
> constructed after every branch that could act on it. → `T1`.
> ④ **The guard's new clause is a nested `if`, ¬a literal `&& !ReleasedByCoarseDrift(verdict)`.**
> Identical semantics and still a fall-through — nothing returns — but it is what lets the
> Information log fire **exactly** on a release rather than on every title w/ no judged drift.
>
> **Measured wider than the plan asked**, at the user's instruction. 187 real titles decoded once
> and scored twice, by `HEAD`'s `SyncVerifier` and by the changed one: **935 rows identical on every
> judged field**, `calibrate.ps1` byte-identical on the fixed five, 0 structural violations, **19 of
> 30** measurable four-window titles released, and **0 false accepts at +800, −800, +1500 and +2500** —
> where the investigation's own 40-title sweep left one survivor at +800. The bound sweep reproduced
> the knee independently: 300 is full recall, and 500 admits a false accept 300 refuses.
>
> **Knowingly left undone**, by the user's decision:
> - **C in full** — no `CheckRevision` bump. See ① above.
> - No `README.md`, config-page or release-changelog change; none was asked for and no refusal
>   message moved.
>
> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with.

## The question

The stretch guard refuses a result the engine rescaled when the audio check never measured drift.
On a four-window title the check **can never** measure drift — `Score` only computes it at
`DriftWindows = 6` — so no short title can clear that gate however good the result is.

Can that bucket be reduced **without** writing a badly synced subtitle?

Binding constraint, from the user, verbatim:

> The plugin must not write a badly synced sub (to the best of our ability to measure) when
> 'Only sync when audio check is conclusive' is enabled, period.

## The population

`records-final.json`, copied from the live server after the second 1.5.0.0 scan. 1898 rows.

| n | rows | items | refusal |
|---|---|---|---|
| **248** | | | **stretch guard, both messages — 60% of all refusals** |
| | 169 | 155 | — *"… too short for the audio check to measure drift"* |
| | 79 | 57 | — *"… could not measure drift on this title"* |
| 58 | | | the audio check reached no verdict |
| 57 | | | the subtitle out of alignment |
| 51 | | | the offset drifting across the runtime |
| **414** | | | **all refusals** |

The stretch guard is the largest single refusal reason in the library and two thirds of it is the
short-title half. 150 of those 169 rows have an unchanged sidecar (SHA matched against
`SourceSha256`), a reachable video, and a four-window plan → the testable population.

! **Only the 169 are in scope.** The 79 are titles that planned six windows and whose drift fit
returned null. Nothing here is measured against them and nothing here changes them → *Audit A11*.

### The four-window regime is exactly the whole short bucket

Derived from `PlanWindows`, ¬assumed:

- `spanMs <= 10 min` → **one** window, whole track.
- otherwise `count = clamp(span / 6 min, 4, 16)`, then bumped to 6 if `span / 18 >= 90 s`
  (i.e. `span >= 27 min`).
- span 27–30 min truncates to 4 but is bumped → 6. Span 30–36 min truncates to 5 and is also
  bumped → 6.

→ **a five-window plan is unreachable.** Plans are 1, 4, or ≥6. The short bucket is precisely the
four-window plans, cue span between 10 and 27 minutes — the 21-minute animation this library is
full of. "Two a side" is therefore always exactly 2 + 2, and its baseline is exactly ⅔ of the
runtime. No untested intermediate case exists.

## The rule

At the stretch guard, before refusing, release instead if **all three** hold:

1. the post-sync verdict is `Aligned` — the whole-track fit, judged by the centred bound; **and**
2. a drift computed from **two windows a side** of a four-window plan is measurable; **and**
3. `|that drift| <= CoarseDriftWithinMs`.

Otherwise refuse exactly as today.

! The coarse drift is a **release condition only**. It never produces a verdict, never refuses
anything, and is never compared against `DriftWithinMs`. → *Audit A3, A4*.

## Method

A bench in scratch (`driftlab`) links the shipping `SyncVerifier`, `SubtitleOffsetProbe`,
`FfmpegProcess`, `PluginConfiguration` and `ISpeechOnsetSource` sources rather than copying them, so
every verdict quoted is the verdict the plugin reaches. It varies only the number of windows per
half. Sidecars are SHA-checked against the record and **vaulted into scratch before measuring** —
the library is the one the plugin writes to, and X4 is what happens when that is skipped.

Populations, all drawn from the live store by the same fixed shuffle `sweep.mjs` uses:

- **Safety set** — 40 titles from the `already aligned with the audio` bucket, 4-window plans,
  21–27 min. Their sidecars are ones the check already calls correctly timed.
- **6-window control** — 20 titles from the same bucket with six-window plans.
- **Recovery set** — 24 of the 150 short stretch refusals, run through the real pinned `assy-cli`
  with the shipping invocation, outputs written to scratch.

A synthetic rate error is injected by scaling cue times so the last cue moves by *n* ms and the
first stays put. ! That is the right model of the failure ∵ `ffsubsync` only ever applies a linear
scale plus a shift; it has no non-linear mode to model.

## Result 1 — safety, against injected rate error (n=40)

| injected | verdicts | rule releases |
|---|---|---|
| none | Aligned 40 | **25** — max \|drift\| 300 |
| +800 ms | Mis 16 / Inc 17 / Al 7 | **3** — all at exactly 500 |
| −800 ms | Mis 21 / Inc 17 / Al 2 | **0** |
| +1500 ms | Inc 30 / Mis 10 | **0** |
| +2500 ms | Inc 34 / Mis 5 / Al 1 | **0** |
| +5000 ms | Inc 36 / Mis 4 | **0** |

15 of 40 correct titles fail closed — the drift is unmeasurable, so they stay refused. That is the
design: no measurement, no release.

## Result 2 — is the instrument weaker than the one that already ships? (n=20)

The question that decides everything. On **six-window** titles the plugin already computes and
trusts a drift over three windows a side. Both reductions computed over the same audio:

| injected | \|2 a side\| ≥ \|shipping 3 a side\| | releases by 2 a side | releases by shipping |
|---|---|---|---|
| +800 ms | **9 of 9** measurable | 0 | 2 |
| +1500 ms | **7 of 7** measurable | 0 | 0 |
| none | 5 of 9 | 9 | 17 |

Unanimous under error. It is geometry, ¬luck: a drift reading measures the separation between the
two halves' centres, and two a side discards the middle.

| reduction | baseline | end-to-end error that trips a 500 ms bound |
|---|---|---|
| shipping, 3 a side of 6 windows | 0.60 of runtime | 833 ms |
| **proposed, 2 a side of 4 windows** | **0.667** | **750 ms** |
| 2 a side of 6 windows (the control above) | 0.80 | 625 ms |

→ the proposed gate is **stricter than the drift test already applied to every longer title**.
Measured against geometry: at +800 the shipping 3-a-side reads a median 450 ms (predicted 480) and
the 2-a-side reads a median 525 ms.

### ! Reconciling this with the recorded six-window rule

`ARCHITECTURE.md` says, and the constant's own comment repeats: *"Six windows, or no drift verdict.
… at the four-window floor a half is two windows of noise arguing w/ two more."* That evidence
stands and is ¬contradicted here. It says a 2-a-side reading is **¬sufficient to call a rate
error**. This proposal never calls one:

- a spuriously **large** coarse reading → the release condition fails → refuse, exactly as today.
  Simpsons S01E10 reads −3125 at four windows; under this rule that title's behaviour is unchanged.
- a spuriously **small** coarse reading on a genuinely stretched file is the only new risk, and it
  is what Result 1 measures directly: one survivor in 40 at +800, none at −800, +1500, +2500, +5000.

The 26 drift refusals recorded in the 1.2.4.0 field logs were four-window titles **refused** by a
2-a-side reading. Using the same reading to release inverts which direction its weakness costs.

## Result 3 — recovery, the real engine on 24 refused titles

All 24 produced a file. The rescales split two ways: **~53 000 ms** end to end (scale 1.043 — PAL
25 → 23.976, ten titles) and **~1 300 ms** (0.999/1.001, nine titles), plus two at 0.960 and two at
1.042.

| post-sync verdict | n | rule outcome |
|---|---|---|
| Aligned | 6 | **3 released**, 3 refused — drift unmeasurable |
| Inconclusive | 15 | all refused |
| Misaligned | 3 | all refused |

Released: `The Simpsons S20E05 Dangerous Curves` (coarse drift 50), `Teenage Mutant Leela's Hurdles`
(−25), `Lesser of Two Evils` (125). Two of the three are genuine 4.3% PAL conversions the check then
confirms aligned end to end — the case the guard has been over-refusing.

Refused and correctly so: `A Clockwork Origin` stayed `Misaligned` w/ 2275 ms of drift;
`Jurassic Bark` and `Spanish Fry` went `Inconclusive` → `Misaligned` post-sync.

**Projected field yield: ≈21 of the 169 rows, ≈5% of all 414 refusals.** Modest, and stated as such.

## Result 4 — independent corroboration

Per-window fits were tried first and are worthless: a lone 90 s window beats its rival ratio in 9
of 40 cases. ! ¬a usable signal; do ¬revisit it as a gate.

The replacement splits each title in half and measures each half **as its own title**, w/ its own
window plan and its own full sweep — twice the audio of a 2-a-side reading and nothing shared
between the two answers.

| title | rule | independent half-drift |
|---|---|---|
| Dangerous Curves | RELEASE | **150 ms**, both halves Aligned |
| Teenage Mutant Leela's Hurdles | RELEASE | **150 ms** |
| Lesser of Two Evils | RELEASE | **100 ms**, both halves Aligned |
| When Aliens Attack | refuse | 1050 ms |
| A Clockwork Origin | refuse | 2450 ms |

Nothing the rule releases reads above 150 ms on a measurement that shares no windows-per-half
assumption w/ it; both titles carrying real drift were refused.

! **The half-split is ¬a candidate gate.** Its baseline is only ≈0.5 of the runtime, so it
under-reports: at +800 injected its median reading is 300 ms and **all** of its readings sit inside
500. It corroborates flatness; it cannot discriminate.

## Result 5 — voice detection in the loop

The payload's own `vad` entry was driven exactly as `AssyVadOnsets` drives it, w/ the second pass
live, ∵ the governing setting names voice detection explicitly.

- On the 24 engine outputs: **VAD was called on all 15 `Inconclusive` titles and settled none.**
  Verdicts identical w/ and without it; recovery unchanged at 3.
- At +800 it settled 4 titles (3 Misaligned, 1 Aligned); at +1500, 3 (2 Misaligned, 1 Aligned).
- **Every VAD-settled `Aligned` under injected stretch was still refused**, ∵ the drift condition
  failed. Voice detection never manufactured a release the silence path would not have made.

## The bound — 500 is the wrong number here, 300 is right

Release counts across every run, sweeping the bound:

| run | ≤200 | ≤250 | **≤300** | ≤350 | ≤400 | ≤500 |
|---|---|---|---|---|---|---|
| correct files (n=40) | 21 | 24 | **25** | 25 | 25 | 25 |
| +800 | 1 | 1 | **1** | 1 | 1 | 3 |
| −800 / +1500 / +2500 / +5000 | 0 | 0 | **0** | 0 | 0 | 0 |
| +800 / +1500, VAD live | 1 / 0 | 1 / 0 | **1 / 0** | 1 / 0 | 1 / 0 | 3 / 0 |
| real engine output (n=24) | 3 | 3 | **3** | 3 | 3 | 3 |

**300 costs nothing and removes two thirds of the false accepts.** Full recall on correct files,
full recovery on real output (their coarse drifts are 50, −25, 125), and the +800 leak drops 3 → 1.

`CoarseDriftWithinMs = 300` over a ⅔ baseline ⇒ an effective end-to-end tolerance of **450 ms**,
against the **833 ms** the shipping six-window path already allows.

### The one survivor, named

`Terry Kitties` reads 125 ms of coarse drift under an 800 ms injected error. Its half-split cannot
corroborate either way (early half Misaligned, late half Inconclusive) → at that title's onset
density no available measurement contradicts the reading. The honest framing: **a six-window title
w/ the same 800 ms error measures ~480 ms and is released by the code that ships today.** This gate
refuses cases the current long-title gate accepts.

## Conformance w/ the governing constraint

`RequireAudioConfirmation` gates exactly one thing — the `Inconclusive` verdict, at
`SyncOrchestrator.cs:509-513`. Every other refusal stands w/ or without it. The rule fires **only** on
`Aligned` →

- it cannot turn an unconfirmed title into a write; `Inconclusive` still dies at the guard;
- it removes no test and loosens no bound;
- it **adds** a test to a class of titles that faces none today, over a baseline wider than the one
  already trusted, and refuses whenever that test cannot be run.

Under the setting, this strictly increases the evidence required before a short title is written.

## Design

**The measurement lives in `SyncVerifier`; the policy lives in `SyncOrchestrator`.** That split is
¬optional here → *Audit A1*.

`Drift(sample, starts, out strength)` already takes `half = Plan.Count / 2`, so on a four-window
plan it **is** the 2-a-side reading. No new fitting code is needed — only a second call site and a
place to carry the answer.

```
Score(sample, starts):
    whole   = Fit(whole plan)
    raw     = Plan.Count >= 4 ? Drift(sample, starts, out halves) : null
    judged  = sample.Windows >= DriftWindows ? raw : null      // unchanged semantics
    coarse  = sample.Plan.Count < DriftWindows ? raw : null     // carried, never judged

    if judged beyond DriftWithinMs        -> Misaligned            (unchanged)
    if whole.Shift is null                -> Nothing(...)          (unchanged)
    return VerificationResult(..., DriftMs: judged, CoarseDriftMs: coarse)
```

```
SyncOrchestrator, the stretch guard:
    if  verdict.DriftMs is null
    &&  !ReleasedByCoarseDrift(verdict)          <-- the only new clause
    &&  |change.DriftMs| > DriftWithinMs
        -> refuse, as today

    ReleasedByCoarseDrift(v) =
        v.Verdict == Aligned
        && v.CoarseDriftMs is { } c
        && |c| <= CoarseDriftWithinMs
```

Releasing is **falling through the existing condition**, ¬a new branch → everything downstream (the
`Inconclusive` shift backstop, `RequireAudioConfirmation`, the transform, placement) is reached
unchanged → *Audit A9*.

## Implementation checklist

! Nothing here is built. Each box is a single, checkable change.

### A. `SyncVerifier` — the measurement

- [x] `Services/SyncVerifier.cs:64` — add beside `DriftWithinMs`:
      `public const int CoarseDriftWithinMs = 300;`
      ! **raw, ¬centred** — drift is a difference and the authored lead cancels in it, the same rule
      as `DriftWithinMs`. Comment must say why it is *smaller* than a bound measuring the same
      thing: the ⅔ baseline reads a given end-to-end error larger than the ⅗ one does.
- [x] `Services/SyncVerifier.cs:87` — leave `DriftWindows = 6` **and its comment** exactly as they
      are. ! The comment ("Two windows a side is not enough to call a rate error") is still true and
      is ¬in conflict w/ this change — the coarse value calls nothing → *Audit A3*.
- [x] `Services/SyncVerifier.cs:18-26` — **append** `int? CoarseDriftMs = null` to
      `VerificationResult`, last, w/ a default. ! Appending, ¬inserting: `vadcheck/Program.cs:139`
      constructs it positionally w/ five arguments and must keep compiling → *Audit A13*.
- [x] `Services/SyncVerifier.cs:250-292` — `Score` (the split is at `:261-262`, the carrying return at `:284`): compute `raw` once, split into `judged` and
      `coarse` per the shape above. ! `judged` keeps the **`sample.Windows >= DriftWindows`** test
      it has today; `coarse` is gated on **`sample.Plan.Count < DriftWindows`**, ¬on `Windows`
      → *Audit A11*. Only the final `return` sets `CoarseDriftMs`; the `Misaligned`-by-drift return
      and both `Nothing(...)` paths leave it null.
      ! **Done, but ¬in this shape** — the fit runs inside the final `return` and only on an
      `Aligned` verdict → header ③. Same decisions, half the cost.
- [x] ! Confirm by inspection that `halves` (the out-parameter strength) is still read **only** in
      the drift-`Misaligned` branch. `Drift` now runs on short titles, so it is now set where it
      previously stayed 0; nothing may start reading it → *Audit A6*.

### B. `SyncOrchestrator` — the policy

- [x] `Services/SyncOrchestrator.cs:448` — add the `&& !ReleasedByCoarseDrift(verdict)` clause to
      the stretch guard's condition. ! Nothing else in that block moves; the two refusal messages
      and `RejectedOffsetMs` stay exactly as they are for the titles still refused.
      ! **Done as a nested `if`**, ¬a literal added clause → header ④. Still a fall-through.
- [x] `Services/SyncOrchestrator.cs` — add the private static `ReleasedByCoarseDrift` helper beside
      the guard.
- [x] Log the release at **Information**, naming item, key, the engine's stretch, the coarse drift
      and the window count. ! Without it the new path is invisible in a field log — the refusal it
      replaces logs a warning, and *which* rule released a title is exactly what the next
      investigation will need → *Audit A10*.

### C. Retroactivity — a user decision, ¬an implementer's

> ! **NOT DONE, deliberately — the user chose no lever at all.** The rows already refused stay
> `Failed` and `IsExhausted` keeps parking them until *Retry failed subtitles* on the config page
> reopens them, which the user triggers when they want it. Nothing in the code changed for this.
>
> ! **A5 below is wrong, and the error is worth keeping visible.** `CheckRevision` is **¬** the only
> lever. `SyncRecord.CurrentMeasurementVersion` → `SyncStore.Remeasure` reopens every `Failed` row
> carrying a `RejectedOffsetMs` **at load** — ≈356 rows rather than 1898, no full re-scan — and
> `SyncStore.ReopenFailed`, behind the *Retry failed subtitles* button, reopens every `Failed` row
> on demand. The audit checked `IsExhausted` and `ToleranceWouldNowAccept` and stopped there.

- [ ] ~~`Configuration/PluginConfiguration.cs:95` — `CheckRevision` `"check2"` → `"check3"`.~~
      ! **Required for the change to reach the 169 existing rows.** A `Failed` row is skipped by
      `IsExhausted` unless `OutcomeStamp()` differs, and `ToleranceWouldNowAccept` cannot reopen a
      stretch refusal ∵ `RejectedOffsetMs` there holds a rate magnitude (53 520, 1 281) and
      `IsAligned` of that is false. `CheckRevision` is the only lever → *Audit A5*.
      ! **Measured, ¬inferred:** all 169 rows carry a `RejectedOffsetMs` between 692 and
      153 754 ms and **none** falls inside the `IsAligned` band → that hook reopens zero of them.
- [x] ! **Cost to state to the user before doing it:** bumping the revision reopens **every** row,
      ¬only the 169. On this library that is a full re-scan — the check2 bump took ~2 h. Leaving it
      unbumped is a legitimate choice; it means the change only benefits targets that change for
      some other reason. → **raised, and this is the option the user took.**

### D. Harness debt — `verifycheck`

> **Nine cases, ¬eight.** One was added to pin the bound: a 600 ms end-to-end rate error reads
> ≈400 coarse — inside `DriftWithinMs`, outside `CoarseDriftWithinMs` — on a title the whole-track
> fit calls `Aligned`. Raising the bound to 500 fails it, and nothing else.

`SampleFor(starts, shiftMs, rate)` already synthesises a rate error and `PlanWindows` is reachable,
so every case below is buildable w/ what the harness has.

- [x] a four-window title, produced file flat → coarse drift measurable and inside the bound →
      **released**
- [x] a four-window title w/ a real rate error → coarse drift beyond the bound → **refused**
- [x] a four-window title where one half does not fit → coarse drift null → **refused**
      (the fail-closed case — 15 of 40 correct titles land here)
- [x] ! a title w/ a **large** coarse drift still returns its old verdict — coarse drift may never
      produce `Misaligned`. This is the S01E10 trap and the single most important case here
- [x] a six-window title: `CoarseDriftMs` is null and `DriftMs` governs → proves long titles are
      untouched
- [x] a six-window plan w/ only four windows read: `CoarseDriftMs` is null → proves the 79-row
      bucket is untouched → *Audit A11*
- [x] a one-window (whole-track, under 10 min) title: `Drift` returns null → refused, unchanged
      → *Audit A8*
- [x] the bound is raw: a coarse drift of `TypicalLeadMs + CoarseDriftWithinMs` is **refused**,
      ¬admitted by a centred test

### E. Calibration

- [x] `.\agentic\tools\verifycheck\calibrate.ps1` over the fixed five (Mad Men S02E06, MPFC S01E02,
      Simpsons S01E10, TNG S02E02, Twin Peaks FWWM). ! **No verdict, shift, hit count or floor may
      move.** S01E10 is the four-window title that reads a coarse drift of −3125 — it is the live
      proof that the coarse value is carried and ¬judged.
- [x] ! Measure from the vaulted fixtures, ¬the library copies (X4).

### F. Before any commit

- [x] `.\agentic\tools\verify.ps1` fully green
- [x] `check-comments.mjs` — the new comments carry no agent notation (`¬ ∵ → w/`); this file's
      notation is ¬a shipped comment's

### G. Documentation obligation

- [x] `agentic/ARCHITECTURE.md`, `### SyncVerifier`, **The drift test** — a paragraph for the coarse
      reading: what it is, that it is carried and never judged, the ⅔-vs-⅗ baseline, and the
      reconciliation w/ *"Six windows, or no drift verdict"*, which stays as written
- [x] `agentic/ARCHITECTURE.md` — add `CoarseDriftWithinMs=300` to the gate-constants line
- [x] `agentic/ARCHITECTURE.md`, `### SyncOrchestrator` — the guard now has a release path
- [x] ! **No `README.md` change** without the user asking for an exact wording. No config-page
      change either: this is a `const`, ¬a setting, and no refusal message changes
- [x] ! Nothing in the repo may name this file → the hard rule in `CLAUDE.md`

## Audit of the steps above

Performed against the source after the checklist was drafted; the checklist above already carries
the fixes. Findings are recorded ∵ each was a defect in the first draft.

| # | finding | resolution |
|---|---|---|
| **A1** | First draft computed the coarse drift **in the orchestrator**. It cannot: at `SyncOrchestrator.cs:406` the verdict comes from `ScoreAsync` (which has the sample) **or** `VerifyAsync` (which samples internally and returns no sample) → the guard would behave differently depending on which branch ran | moved into `SyncVerifier.Score`, carried on `VerificationResult` |
| **A2** | `VerificationResult` is a positional record struct w/ four construction sites | append the member w/ a default; only the final `Score` return sets it |
| **A3** | `DriftWindows`'s comment and `ARCHITECTURE.md` both state two a side cannot call a rate error — a careless implementer reads the change as contradicting them | both stay verbatim; the new constant's comment and the new ARCHITECTURE paragraph say explicitly that the coarse value is a release condition, never a verdict |
| **A4** | If the coarse value ever reached the drift-`Misaligned` branch, short titles would start being called misaligned — and would also lose the VAD second pass, which only `Inconclusive` reaches | `judged` and `coarse` are separate locals; D's fourth case asserts it |
| **A5** | Nothing reopens the 169 existing rows. `IsExhausted` skips a `Failed` row unless `OutcomeStamp()` changed, and `ToleranceWouldNowAccept` reads `RejectedOffsetMs` through `IsAligned` — checked against the store: all 169 hold 692–153 754 ms and none is inside the band | `CheckRevision` → `check3`, raised to the user as a decision w/ its full-rescan cost |
| **A6** | `Drift` now runs on four-window titles, pre- **and** post-sync — two extra `Fit` sweeps per short title, and it now sets the `halves` out-parameter where that previously stayed 0 | no extra audio decode (the sweep is CPU over onsets already read); checklist A carries an explicit confirmation that `halves` is read nowhere new |
| **A7** | The design assumed a five-window plan needed handling | derived from `PlanWindows` that plans are 1, 4, or ≥6 — five is unreachable → "two a side" is always 2 + 2 and the baseline is exactly ⅔ |
| **A8** | A title under 10 minutes of cues plans **one** window; `Drift` returns null at `half < 2` | fail-closed, unchanged behaviour; case added to D |
| **A9** | A separate release branch would bypass the `Inconclusive` shift backstop and `RequireAudioConfirmation` below it | implemented as one added `&&` clause so release is fall-through |
| **A10** | The release would be invisible in field logs — the refusal it replaces logs a warning | Information-level line naming stretch, coarse drift and windows |
| **A11** | **The worst finding.** Gating the coarse value on `sample.Windows < DriftWindows` (windows *read*) would also catch a six-window plan w/ only four windows read — i.e. part of the untested 79-row bucket, where `Drift` would compute **three** a side over sparse onsets | gate on `sample.Plan.Count < DriftWindows` (windows *planned*) → exactly the measured population, and the 79 stay untouched. Case added to D |
| **A12** | Reusing `DriftWithinMs = 500` for the coarse reading leaks 3 of 40 at +800 for no recall gain | separate `CoarseDriftWithinMs = 300`, chosen off the bound sweep |
| **A13** | Inserting the new member positionally would break `vadcheck/Program.cs:139` | append last, w/ a default |

! One pre-existing inaccuracy found and **deliberately left alone**: the `tooShort` message is
chosen by `verdict.Windows < DriftWindows`, i.e. windows *read* — so a six-window title w/ two
unreadable windows already reports *"too short"* when it is not. Out of scope here; fixing it inside
this change would move rows between the two refusal messages and confound the before/after count.

## Honest limits

- **One library, one genre.** The safety and recovery sets are heavy w/ 21-minute animation
  (Simpsons, Futurama, Kids Next Door, King of the Hill). Onset behaviour on live action at four
  windows is ¬separately characterised here.
- **n=24 for recovery.** The 3/24 rate carries an obvious sampling error; ≈21 rows is an estimate,
  ¬a promise.
- **The +800 survivor is real.** One title in 40 at an error 60% past the effective bound. The
  defence is that today's six-window gate would also pass it, ¬that it cannot happen.
- **15 of 24 produced files are `Inconclusive` even w/ voice detection.** That is an onset-supply
  problem and is the real ceiling on this bucket; no change to the guard touches it.
- **The 79 "could not measure drift" rows are untouched.** Whether a 2-a-side reading rescues any of
  them is measurable and was ¬measured.

## Rejected alternatives

- **Lower `DriftWindows` 6 → 4.** Simpsons S01E10 reads a coarse drift of −3125 → the change would
  fabricate a `Misaligned` verdict on a title that is fine, **and** rob it of the VAD second pass,
  which only `Inconclusive` reaches. Rejected on recorded calibration data.
- **More, shorter windows.** N5: a shorter window holds fewer onsets, which is what stops the check
  measuring in the first place. Independently rejected twice already (V9, V10).
- **Retry the sync without rescaling.** Four of every eight titles in the recovery set need a
  genuine 4.3% rescale → refusing the rescale refuses the correct answer.
- **Per-window fits as the signal.** Measurable on 9 of 40. Dead.
- **The half-title split as the gate.** Baseline ≈0.5 → under-reports; every reading at +800 sits
  inside 500. Useful as corroboration only.

## Verdict

**Adopt, at 300 ms, phrased as a release condition and nothing more.**

Safe by the strongest test available: unanimously sharper than the drift the plugin already trusts
(9/9, 7/7), zero false accepts at −800, +1500, +2500 and +5000, one marginal survivor at +800 that
today's long-title gate would also pass, corroborated on real engine output by an independent
measurement, and unchanged by voice detection. It fails closed on every title it cannot measure —
15 of 40 correct ones, 21 of 24 real ones.

Cost: ≈21 rows recovered of 169, plus whatever a `CheckRevision` bump costs in scan time.

**Ready for implementation.** Order of work: A → B → D → E → F → G, w/ C raised to the user first.
