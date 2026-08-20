# IDEA-VAD — investigation record — **IMPLEMENTED**

> Status: **DONE**, 2026-08-18. Everything in the implementation checklist below is in the repo
> except the items named under *Knowingly left undone*. `verify.ps1` green. ¬committed — the repo
> rules require explicit approval for that, and none has been given.
>
> **What shipped:** the centred bound (`TypicalLeadMs=170` / `AlignedWithinMs=200`, w/ a raw
> `DriftWithinMs=500` for differences) · the webrtcvad second pass inside `SyncVerifier`, reached
> through `ISpeechOnsetSource` → `AssyVadOnsets` → the payload's new `vad` subcommand · `CheckRevision`
> in `OutcomeStamp()` · the split stretch-refusal messages · the D10 config-page wording · the
> README's *After updating the plugin* note. Documented in `ARCHITECTURE.md` (`SyncVerifier`,
> *The payload entry wrapper*, `AssyArgumentBuilder`, `AssyCliRunner`, `PayloadStore`, retroactivity)
> and `CLAUDE.md` (payload versioning, the `vad` contract, `vadcheck`).
>
> **Two decisions taken during implementation, ¬in the plan below.**
> ① The plan assumed the payload's `webrtcvad` was reachable from C#. It was not — the freeze exposed
> only upstream's subcommands. The user chose to **extend the payload**: `agentic/tools/assy-entry/`
> is now the freeze's entry point, dispatching `vad` locally and handing every other argv to
> upstream's `cli.main()` untouched.
> ② That makes the payload bytes ours, so its version is now the **payload revision** rather than the
> upstream tool version: the published payload becomes **1.0** and this one is **2.0**. Old plugin
> versions keep pinning 1.0 and keep downloading it. ! The GitHub side of that — retitling the
> existing release and publishing the 2.0 assets — is an outward-facing action and is **not done**;
> it needs the user.
>
> **The fallback is post-sync only** — a further restriction the plan did not name. A pre-sync
> `Aligned` from voice detection would *skip* titles the engine fixes today, so the second pass could
> subtract writes rather than only add verdicts; post-sync it can only refuse a result or confirm one.
>
> **Knowingly left undone**, by the user's decision:
> - **O6** — the `drifting` bucket (33) is still unmeasured. Skipped deliberately; drift *was*
>   measured there, so no fallback fires and the refusal stands regardless.
> - **The config-page notice** about the required full scan (F, third item) — the user declined it.
> - **The README notice** (F, first item) — written, then removed: the README is a starter overview
>   and carries no mechanism. ! The instruction is satisfied by the **release changelog**, which is
>   what an upgrading user reads. Do ¬put it back.
> - **The release changelog line** (F, second item) — no release has been cut; it belongs to release
>   step 0, whenever that happens.
>
> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with.

## The question

Would a voice-activity detector reduce the number of failed syncs, **without** breaking any
subtitle that is already correctly synced?

Binding constraint, from the user, verbatim: *"It is not acceptable to take a sub that is already
correctly synced and break it's sync."*

Field population, from the status panel on the live server (`\\<server>\Jellyfin\Server\log`,
records copied to scratch):

| n | panel message |
|---|---|
| 268 | The sync engine rescaled the subtitle across the runtime — the audio check did not measure that change |
| 95 | The audio check reached no verdict on this title — rejected as inconclusive |
| 33 | The audio check found the offset drifting across the runtime |
| 22 | The audio check found the subtitle out of alignment |

! All 418 refused sidecars are byte-identical to `SourceSha256` in the store → this population **can**
be measured. Z1 was withdrawn ∵ the library had been rewritten under it; that is not the case here,
and `sweep.mjs` re-checks the hash per title so it stays true.

---

## The governing constraint

! **User, verbatim and binding:**
> *"The plugin must not write a badly synced sub (to the best of our ability to measure) when
> 'Only sync when audio check is conclusive' is enabled, period."*

→ This outranks recovery yield. A design that recovers 33 files and writes one badly-synced subtitle
**fails**, and the correct response is to ship less, ¬to argue the rate is low.

! **The asymmetry that makes the constraint tractable.** Every title reaching the fallback is
*already refused today*. So:

| fallback error | consequence |
|---|---|
| false `Misaligned` | **harmless** — refuse→refuse, no change from today |
| false `Aligned` | **violates the constraint** — a write that would not have happened |

→ **Only the wrong-accept rate matters.** Every safety mechanism should buy down false `Aligned`,
and may spend false `Misaligned` freely (the cost is only lost recovery).

## Decided semantics

Settled w/ the user across two exchanges. **This is the design, pending the measurement verdict.**

### The operator

The user's first proposal was `RequireAudioConfirmation` → "Require Audio **or** VAD Confirmation".
Rejected, and the user agreed: *"We don't want a dissagreement to cause a write. If either say
missaligned then we refuse. If silence says missaligned, we don't even call the VAD fallback."*

A disjunction lets the weaker of two readings win → destroys W1's property (the check over-refuses,
never over-accepts) by construction.

### The fallback — one hook, inside `SyncVerifier`

**Decided w/ the user.** The verifier falls back to webrtc onsets when its **own** verdict is
`Inconclusive`, and returns one enriched verdict. It is ¬a second check bolted onto the orchestrator.

| silence says | VAD consulted? | outcome |
|---|---|---|
| `Aligned` | **no** | accept |
| `Misaligned` | **no** | refuse |
| `Inconclusive` | yes | `Aligned` → accept · `Misaligned` → refuse · `Inconclusive` → refuse |

! **Site matters and was chosen deliberately.** The refusal order in `SyncOrchestrator` is
`Misaligned` (:399) → **stretch guard (:428)** → `Inconclusive` (:482). A fallback written at :482
is never reached by a stretch-bucket title, ∵ :428 already returned. Falling back **inside the
verifier** means every downstream branch — including :428 — sees the improved verdict, so the small
stretch recovery comes free, w/ less code than a second hook.

The short-circuit is preserved inside the verifier: fall back **only** on the verifier's own
`Inconclusive`, so a silence `Misaligned` never consults the VAD.

### Hook B (a separate drift fallback) — **DROPPED**

Was: where `verdict.DriftMs is null`, ask the VAD for drift, feeding the stretch guard. **Cut by the
user on the measurement**: usable drift recovered on ~1 in 38 stretch titles and structurally 0 on
the <6-window subset. Its only real yield (~5 titles) is obtained free by the verifier-internal site
above, so the separate mechanism buys nothing.

### The safety property this buys

VAD is only ever reached where today's answer is **already refuse** → the change is
**write-monotone**: it can never turn a current accept into a refusal, and never lose a sync that
works today. The only risk it introduces is a *bad new write*.

→ The `aligned` bucket sweep is therefore a **diagnostic on webrtc's error rate**, ¬a direct risk
measurement. Risk lives entirely in precision on the inconclusive population.

### Always on, no setting — and what that does to retrying

**Decided w/ the user: the fallback is always on. No option to disable it.** The only config change
is the wording of the existing checkbox.

! **Consequence, and it reverses an earlier answer.** `OutcomeStamp()` is built from *settings*
(`PluginConfiguration.cs:99`). A behaviour change that adds **no setting** leaves the stamp
identical → `SettingsUnchanged` returns true → **nothing reopens automatically and the 418 refusals
are never retried.** `ReopenFailed` is a *manual* button (`RetryFailed`, `AutoSubSyncController.cs:354`),
¬something a scan performs.

Two ways to resolve — **user chose (b)**:

| | behaviour | cost |
|---|---|---|
| **(a)** leave the stamp alone | nothing reopens; user presses *Retry failed* to pick up the ≈33 | zero; but the panel keeps reporting 95 inconclusive + 268 rescaled from a check that no longer exists |
| **(b)** add a non-setting revision token to `OutcomeStamp()` | every record reopens on the next scan; refusals retried automatically | one audio-check pass over the enabled libraries |

→ **(b), decided.** Concretely: a `CheckRevision` constant joined into `OutcomeStamp()` alongside
the settings, bumped whenever the check's logic changes what it would write. ! `gatecheck` asserts
the stamp's retroactivity cases → it must be extended to cover the new token, ¬merely kept passing.
! The comment above `OutcomeStamp()` currently reads *"Every setting that changes what gets
written"* and becomes wrong the moment a non-setting joins it; update it in the same edit.

The stamp's contract is *"every setting that changes what gets written"*, and a
code change that changes what gets written is the same class of thing. Under (a) the stored verdicts
outlive the logic that produced them, which is the failure the stamp exists to prevent. The re-scan
is cheap where it is most common: on an already-aligned title `silencedetect` answers first and the
VAD is never called.

### Wording — **approved by the user**, to apply at implementation time

`configPage.html:304`. Current label *"Only sync when audio check is conclusive"* + description
*"The audio check always runs. When this is enabled, a subtitle it cannot confidently decide on is
left alone rather than written…"*

! With an always-on fallback, "conclusive" silently changes meaning → the description must say there
are now two passes:

> **Only sync when the audio check is conclusive**
>
> The audio check always runs, and falls back to voice detection when the first pass cannot decide.
> When this is enabled, a subtitle **neither pass** can confidently decide on is left alone rather
> than written. Those are listed as "rejected as inconclusive"; untick this to retry them on the
> next sync.

### Naming

! Do **not** rename the C# property `RequireAudioConfirmation`. Jellyfin deserializes config by
name → a renamed bool silently reverts to its default `true`. Safe direction, but it would silently
re-enable confirmation for anyone who deliberately turned it off. The label may change freely; the
property may not.

## What VAD is, precisely

! **Not a second opinion — a different microphone.** The harness feeds VAD onsets into the
*shipping* `SyncVerifier.Score`: same ±4 s sweep, same `MinimumHits=12`, `PeakRatio=1.4`,
`RivalRatio=1.25`, same 250 ms tolerance. Only the onset stream differs.

→ Its errors are **correlated** w/ silencedetect's. A title whose audio is structurally unmatchable
defeats both. Any framing as "two independent witnesses" is false.

---

## The 268, decomposed

`DriftMs` is null for two distinct reasons (`SyncVerifier.cs:196`):
`sample.Windows >= DriftWindows` fails, **or** the verdict was `Inconclusive`.

| n | cause | reachable by VAD? |
|---|---|---|
| **89** | ≥6 windows, verdict failed → drift never computed | **yes** — a VAD verdict yields drift for free |
| **179** | <6 windows | **no** — structurally unreachable |

`PlanWindows` derives its count from the **subtitle span**, ¬from onsets. `count = 6` requires
`span/18 >= 90 s` → **≥27 minutes** (`SyncVerifier.cs:300`, `WindowSeconds = 90`,
`DriftWindows = 6`). A different detector does not add windows.

→ **VAD's maximum reach into the 268 is 89 — one third of the bucket.** The other 179 are ~21-minute
episodes. Consistent w/ Z2/N5 ("drift unmeasurable below ~27 min"). **Now measured, ¬predicted:**
`stretch4.json` shows `drift gained = 0 of 20` for both VADs.

### The independent lever for the 179 — O3

`WindowSeconds = 90` is what blocks the conditional raise at `SyncVerifier.cs:300`:

```csharp
if (count < DriftWindows && spanMs / (DriftWindows * 3) >= WindowSeconds * 1000L)
    count = DriftWindows;
```

! **Unrelated to VAD. Needs no new dependency. Addresses 179 records — twice VAD's reach.**

Total audio decoded per title, `count × length`:

| span | ship (W=90) | W=60 | W=45 |
|---|---|---|---|
| 14m | 4×70.0s = 280s | 4×60.0s = 240s | 6×45.0s = 270s |
| **18m** | **4×90.0s = 360s** | **6×60.0s = 360s** | 6×45.0s = 270s |
| **21m** | **4×90.0s = 360s** | **6×60.0s = 360s** | 6×45.0s = 270s |
| **24m** | **4×90.0s = 360s** | **6×60.0s = 360s** | 6×45.0s = 270s |
| 27m | 6×90.0s = 540s | 6×60.0s = 360s | 6×45.0s = 270s |
| 42m | 7×90.0s = 630s | 7×60.0s = 420s | 7×45.0s = 315s |
| 90m | 15×90.0s = 1350s | 15×60.0s = 900s | 15×45.0s = 675s |

Two findings from that table:

1. In the **18–24 min band** — exactly where the 179 live — `W=60` costs the *same total audio* as
   shipping and yields 6 windows instead of 4. Drift becomes measurable for free.
2. ! At **≥27 min**, lowering `WindowSeconds` **removes** audio from titles that already reach 6
   windows and already work (540s → 360s). → **lowering `WindowSeconds` globally is the wrong
   change**; it degrades the working population to fix the broken one.

### O3's proposal, and its rejection — **REJECTED [R]**

Proposal was to keep `WindowSeconds = 90` capping `length` and introduce a smaller constant used
**only** in the raise condition, so a 21-min episode gets `6 × 70s = 420s` (+17% decode) w/ drift
measurable, and nothing ≥27 min or <18 min changes.

**Measured, `--raise-seconds 60`, same titles both arms, n=20 per arm.**

Safety arm — known-good short titles the check calls `Aligned` today:

| source | was Aligned | still fine | **newly refused** |
|---|---|---|---|
| silence | 20 | 17 | **3 (15%)** |
| webrtc | 19 | 18 | **1 (5%)** |

! The breaks are **¬drift**. 15 of 20 gained a drift measurement and **none exceeded 500 ms**. The
shorter windows moved the *offset fit* enough to push three titles past `AlignedWithinMs`. Those subs
verify today; at 6×70s the check calls them misaligned and hands them to the engine.

Recovery arm — the 179:

| source | drift gained | of those >500 ms | **usable** | verdicts **lost** to Inconclusive |
|---|---|---|---|---|
| silence | 1 | 0 | **1** | 1 |
| webrtc | 2 | 1 | **1** | **3** |

→ Recovers usable drift on 1 in 20 while **losing three webrtc verdicts** to the thinner windows.
Net-negative discrimination, to fix ~5% of the bucket, at a 15% break rate on good material.

! **This independently reproduces N5** (`AUDIT.md`, "more and shorter windows", `[R]`). The decoupled
threshold is a genuinely different mechanism and fails the same way → the finding is about **window
length itself**, ¬about how the extra windows are reached. Do not attempt a third variant.

→ **The 268 are not addressable by this work.** The 179 structurally (O3 rejected), the 89 by
Hook B's yield not justifying it. Both routes closed.

### Second, independent decomposition (rate factor)

| n | rate factor | note |
|---|---|---|
| 105 | ≈0.1% | V12's signature — the engine inventing a scale |
| 149 | ≈4.2% | PAL 25↔23.976 |

! Z1 stands: a ratio landing on a textbook conversion carries **zero** information — ffsubsync picks
from a fixed list, so every output lands on one, incl. output from another show's subtitle.

---

## Measurements to date

Sweep `sweep.mjs`, seeded shuffle (`--seed 7`), one target per `ItemId`, hash-verified sidecars.
Scored through the linked shipping `SyncVerifier`. Five of six buckets complete; `drifting`
outstanding (see *Tooling hazards*).

### Safety — `aligned` bucket, n=25 (titles the shipping check calls Aligned)

| source | also Aligned | Inconclusive | **MISALIGNED (break)** |
|---|---|---|---|
| **webrtc** | **25** | 0 | **0** |
| silero | 0 | 23 | **2** |

### ! The `misaligned` bucket vindicates the short-circuit

| silence | webrtc | n |
|---|---|---|
| Misaligned | **Aligned** | **2 of 12** |

→ On **17%** of the titles silence calls `Misaligned`, webrtc disagrees and says `Aligned`. Under the
originally-proposed `audio OR VAD` those 2 become **bad writes**. Under the agreed short-circuit
(silence `Misaligned` → refuse, VAD never consulted) they cannot. **The operator choice is worth
~17% of that bucket in avoided damage.** ¬a theoretical concern.

### Recall — a known 1500 ms displacement handed back

| bucket | source | n | returned it | wrong by >250 ms | no verdict |
|---|---|---|---|---|---|
| aligned | silence | 25 | 25 | 0 | 0 |
| aligned | **webrtc** | 25 | **25** | **0** | 0 |
| inconclusive | webrtc | 7 | 5 | **0** | 2 |
| misaligned | webrtc | 6 | 4 | **0** | 2 |
| stretch | webrtc | 8 | 5 | **0** | 3 |
| silero | (all) | 6 | 0 | 0 | 6 |

→ **webrtc: 39 answers, 0 wrong.** Its only failure mode is *no verdict*, which is the safe
direction. That is the single most important number in this investigation.

### Recovery — where silencedetect reaches no verdict

| bucket | n | → Aligned | → Misaligned | still none | write yield |
|---|---|---|---|---|---|
| inconclusive | 20 | **7** | 3 | 10 | **35%** |
| misaligned | 7 | 1 | 2 | 4 | 14% |
| stretch (all) | 36 | 4 | 5 | 27 | 11% |
| — silero, inconclusive | 20 | 0 | 4 | 16 | 0% |

! webrtc **refuses as well as accepts** (3, 2, 5) → it behaves like a check, ¬an accept-widener.
That was the property required for the fallback to be worth having.

### Drift gained where silencedetect measures none

| bucket | n null | webrtc gained | of those >500 ms | → usable |
|---|---|---|---|---|
| aligned | 7 | 2 | 0 | 2 |
| inconclusive | 20 | 5 | 3 | 2 |
| misaligned | 9 | 1 | 1 | 0 |
| stretch (all) | 38 | 3 | 2 | **1** |
| **stretch <6 win** | **20** | **0** | 0 | **0** |

→ Hook B's yield is **poor**. On the stretch bucket webrtc recovers usable drift on ~1 in 38, and on
the <6-window subset it is structurally 0. Hook A is where the value is.

### Onset supply

| bucket | source | median onsets | median hits/floor | median speech share |
|---|---|---|---|---|
| aligned | silence | 247 | 2.05 | — |
| aligned | webrtc | 225 | 1.93 | 0.52 |
| inconclusive | silence | 77 | 1.15 | — |
| inconclusive | webrtc | **117** | **1.25** | 0.73 |
| stretch | silence | 104 | 1.00 | — |
| stretch | webrtc | 113 | 1.00 | 0.71 |
| misaligned | silence | 132 | 1.50 | — |
| misaligned | webrtc | 133 | 1.35 | 0.40 |

→ webrtc's advantage is concentrated exactly where silence is starved: on the inconclusive bucket it
supplies **+52% onsets** and lifts hits/floor through the gate. On `aligned`, where silence already
has 247 onsets, webrtc supplies slightly fewer and changes nothing — consistent w/ X2/X3.

### ! Caveat on every number above: source sidecar ¬engine output

The sweep scores `record.SourceSubtitlePath` — the **pre-sync** sidecar. In the field the check runs
on the file the **engine produced** (`SyncOrchestrator.cs:391`). These are different populations and
the two halves of the result transfer differently:

- **Measurability transfers.** Whether webrtc reaches a verdict at all is driven by onset supply,
  which is a property of the *audio* (+52% onsets on this bucket). A produced file also matches its
  audio better than an unsynced source did → hits/floor rises → recovery on produced files should be
  **≥** what is measured here. The 35% is likely conservative.
- **Verdict direction does ¬transfer.** On a source sidecar `Aligned` means *it was already fine*;
  on a produced file it means *the engine fixed it*. The "35% become writes" projection inherits
  that softness and should be read as an upper-ish estimate of measurability, ¬a promise of writes.

→ `outcome.mjs` closes this exact gap: it runs the **real** pipeline — engine sync, then the check on
what the engine produced — and scores original **and** produced w/ both onset sources.
**User asked for this before implementation.** Ran on the inconclusive bucket, 20 titles, seed 7.

### ! RESULT — the real pipeline, engine output judged (n=20)

| on the file the engine PRODUCED | count |
|---|---|
| `silencedetect` says `Aligned` → writes **today** | **0 of 20** |
| silence `Inconclusive` → fallback `Aligned` → **NEW WRITE** | **5 (25%)** |
| silence `Inconclusive` → fallback `Misaligned` → hard refuse | 1 |
| still no verdict either way | 14 |

! `silencedetect` reaches **no verdict on all 20 produced files** → today every one of these titles
is refused *after* paying for a full engine run. The fallback is what converts that work into a
result.

**The five accepts land at −75, +25, +150, +150, +75 ms** — all small. The check is confirming the
engine *landed* it, ¬rubber-stamping a large correction. That is the shape a safe accept should have.

### ! The fallback also caught the engine BREAKING a file

`Football` (Drake & Josh S02E04): webrtc read the **source** as `Aligned` at **+100 ms**, then read
the **engine's output** as `Misaligned` at **−775 ms**. The engine took a roughly-fine subtitle and
moved it 775 ms wrong; the fallback refused the result.

→ the fallback is ¬only a permission-granter. On the produced file it is also a **guard against the
engine**, which is the direction the governing constraint actually cares about. ! Under today's code
this title is refused anyway (silence is `Inconclusive`), so this is ¬a new save — it is evidence
that the fallback **discriminates** rather than accepting whatever it can measure.

### ! THE DECISIVE MEASUREMENT — audio truth on the engine's output (n=20)

`produced-truth.mjs` runs `audio-truth.mjs` against the **engine's output** for all 20 — the first
measurement in this investigation that scores the **written artefact** rather than the input.

! **It first exposed a flaw in our own instrument.** `audio-truth.mjs` gates on `iqrMs <= 500`, and
on this bucket that refused **all five** accepts (IQRs 529–770) → "0 of 5 measurable". **That gate is
wrong for this question.** The quantity under test is the *centre*, and the precision of a median is

```
SE(median) ~ 1.253 * sigma / sqrt(n),  sigma ~ IQR/1.349   =>   SE ~ 0.9288 * IQR / sqrt(pairs)
```

→ `Grandstand` at IQR **529** over **145** pairs pins its median to **±41 ms**. A sloppily-timed
subtitle with many cues still has a well-determined centre. ! The IQR gate conflates *internal
consistency* with *measurement precision*; only the second one matters here. Re-gated on
**SE ≤ 100 ms**, **16 of 20** produced files carry a usable reading.

**Confusion matrix, truth = audio, `|gap − 170| <= 200`:**

| fallback decision on the engine's output | n |
|---|---|
| **ACCEPT** & really in sync — correct write | **4** |
| **ACCEPT** & really out — !! WRONG WRITE | **0** |
| **REFUSE** & really out — correct save | **1** |
| **REFUSE** & really in sync — harmless | 0 |
| abstain & really in sync — missed chance | 11 |
| abstain & really out — correctly left alone | 0 |

→ **5 decisions, 5 correct, 0 wrong.** The four accepts sit at centred **73, 120, 18, 145 ms**; the
fifth accept (`Reach for the Sky`) is SE ±154 → too noisy to judge either way, ¬counted.

**The refusal is independently confirmed.** `Football`: the check refused at **−775 ms**; the audio
says gap **−338 ms**, centred **508 ms**, SE ±93 → the engine really did break that file and the
fallback really did catch it. ! This is the governing constraint being *enforced*, measured against
the audio rather than against the check's own opinion.

### ! The abstentions reframe the ceiling

**11 of 16** measurable produced files are **actually in sync** yet the fallback abstains. The engine
fixed **15 of 16**; the check confirms only **4**. → the limit on recovery is ¬the engine's ability
to fix these files, it is the **check's sensitivity** on subtitles whose cues track speech loosely.
That is a *yield* problem, ¬a *safety* problem, and it is where any further work should go.

### Projected field yield

| bucket | field n | reachable | measured write yield | projected new writes |
|---|---|---|---|---|
| inconclusive | 95 | 95 | 35% | **≈33** |
| stretch | 268 | 89 (≥6 win) | ~6% | **≈5** |
| out of alignment | 22 | 0 (short-circuited) | — | 0 |
| drifting | 33 | 0 (drift measured → no fallback) | — | 0 |

→ **≈38 of 418 refusals recovered, ≈9%**, at zero measured safety cost. Plus ~14 currently-vague
refusals converted into firm `Misaligned` verdicts — no behaviour change, better reason text.

### Verdict on silero

**Drop it.** Useless *and* dangerous: recovers 0 of 20 on the inconclusive bucket, answered the
recall probe 0 of 6 times, and produced **2 false `Misaligned` on correctly-synced titles** — the one
outcome the constraint forbids. Dropping it also avoids vendoring an ONNX model + runtime. webrtc
already ships inside the assy-cli payload.

## The 500 ms bound (`AlignedWithinMs`) — answered

User's ask: *"I want subs to be synced if they are over 100ms off, under can be skipped"*.

`AlignedWithinMs = 500` (`SyncVerifier.cs:59`) gates three things: the pre-sync skip, the post-sync
verdict, and the drift/stretch bound.

! What it bounds is **not** "how out of sync is this". It is the gap between a cue start and the
nearest detected onset, w/ two systematic non-error components: the subtitle's **authored display
lead**, and **detector lag**.

Evidence, from the 1137 records currently marked already-aligned:

- median reading **+225 ms**, p90 375 ms
- **980 positive vs 117 negative** → 89% one-directional. Sync errors would be symmetric.
  → the typical reading is the display lead, ¬a fault.

W1's recorded check-vs-embedded gaps: MPFC `+495, −906, +125, +100, +26, −37`;
TNG `+625, +523, +598, +300, +530`. Worst **906 ms**. TNG consistently ~+550, MPFC not → **per-source,
¬a constant that can be subtracted.**

! **These figures are now in doubt as a measure of the CHECK's error** — they were taken against
embedded tracks, and every embedded track sampled on this library is a DVD VobSub whose timings come
from a different master. See *O1* below. The **per-source, ¬subtractable** conclusion stands; the
magnitude may belong to the embedded tracks. ! Re-measure w/ `audio-truth.mjs` before quoting 906 ms
as the check's own error.

Consequence of lowering the bound:

| bound | still skipped | sent to the engine |
|---|---|---|
| **100 ms** | 248 | **889 (78.2%)** |
| 150 ms | 377 | 760 |
| 200 ms | 542 | 595 |
| 250 ms | 694 | 443 |
| 300 ms | 852 | 285 |
| 400 ms | 1061 | 76 |
| **500 ms (today)** | 1137 | 0 |

### ! Why there are two thresholds — the user asked, twice

They answer **different questions** and are ¬redundant:

| constant | where | question it answers |
|---|---|---|
| `AlignedWithinMs` = 500 | `SyncVerifier.cs:59` | **"is this subtitle in sync?"** — decides `Aligned` vs `Misaligned`, i.e. whether the engine is called at all |
| `MinimumMovementMs` = 100 | `SyncOrchestrator` | **"did the engine move it enough to be worth writing?"** — decides whether to *keep* the result |

! **A correction to an earlier claim made in this investigation.** It was argued that lowering
`AlignedWithinMs` to 100 would cause churn ∵ the resulting moves would be discarded by
`MinimumMovementMs`. **That argument was wrong** and the user caught it: `MinimumMovementMs` is
*already* 100, so only one number changes and nothing new gets discarded. The real objection is the
one below — indistinguishability, ¬churn.

! **A second correction: `AlignedWithinMs` does ¬participate in any stamp.** It is a `const int`, ¬a
config setting, so it appears in neither `OutcomeStamp()` nor `GateStamp()`. An earlier note in this
file claimed it did. Retroactivity is handled by dedicated hooks instead → see *Retroactivity*.

**Conclusion: the blocker is that the measurement's error is larger than the threshold wanted.** A
VAD can shrink the *detector lag* half. It cannot shrink the *display lead* half — and a constant
authored lead and a constant real offset are **the same signal in audio**.

→ **Resolved by D11**: stop trying to shrink the raw bound and change the *measure* instead.
`|gap − 170| < 200` refuses **8%** of known-good files where raw `|gap| < 200` refuses **39%**.
The table above is a **raw**-bound table and is superseded by the centred one in the RESULT section.

---

## Shrinking `AlignedWithinMs` — the authoring floor

! The check observes **one** number and it is three things summed:

```
observed gap = authored display lead + real sync error + detector lag
```

Only the middle term is a defect. → **`AlignedWithinMs` is really a statement about the other two:
"larger than authoring convention and detector lag can explain."** Shrinking it does ¬require better
detection; it requires knowing how big the other two terms are.

### Why this is worth reopening

`AUDIT.md` **W1** rejected subtracting a display lead ∵ the gap looked **per-source, ¬constant**
(MPFC `+495, −906, +125, +100, +26, −37`; TNG `+625, +523, +598, +300, +530`). That finding is what
holds the bound at 500.

! W1 measured against **embedded tracks**. Every embedded track sampled on this library is a
`dvd_subtitle` carrying its own per-disc offset → **W1's scatter may be variance between DVD masters,
¬variance in authoring.** Same flaw that made O1 report a false wrong-accept.

### The measurement

`authoring-floor.mjs` runs `audio-truth.mjs` across titles the check **already calls aligned** — files
that are fine — and reports the spread of the per-title median gap. That spread **is** the
authoring + detector floor. The bound must sit outside it or it refuses files whose only fault is how
they were authored.

! The bias is conservative in the right direction: an out-of-sync file that slipped into the sample
**widens** the floor and argues for a **looser** bound, never a tighter one.

### ! RESULT — two independent samples, pooled n=49

Run at 140/seed 7 and stopped at 29 by the user (*"half is probibly more than enough data no?"*);
27 measurable. Pooled with the seed-99 fresh sample (22 measurable, **zero overlap**) → **n=49**.

! The two samples were drawn with different seeds and agree closely — typical lead **162** vs
**170** ms, centred p90 **179** vs **175** ms, ±200 refusing **7%** vs **9%**. That is replication,
¬one lucky draw, and it is the reason these numbers are load-bearing.

Pooled raw gap: min **−128**, p25 **103**, **p50 170**, p75 **237**, p95 **314**, max **345**.
Positive/negative **44 / 5** → the lead is a real convention, ¬noise around zero.

**W1 is contradicted on uncontaminated evidence.** W1's scatter (`+495, −906, +125, +100, +26, −37`)
does not reproduce when the reference is the audio rather than a `dvd_subtitle` track. The gap is
**not** per-source; it is a population constant plus a tight spread.

| bound | refuses raw `|gap|` | refuses **centred** `|gap − 170|` |
|---|---|---|
| 100 ms | 40 (82%) | 17 (35%) |
| 150 ms | 27 (55%) | 8 (16%) |
| **200 ms** | 19 (39%) | **4 (8%)** |
| 250 ms | 9 (18%) | 3 (6%) |
| **300 ms** | 3 (6%) | **0 (0%)** |
| 500 ms (today) | 0 (0%) | 0 (0%) |

→ **the user's `|gap − typical_lead| < bound` formulation is what makes 200 ms reachable.** Raw
`|gap| < 200` refuses 39% of files that are FINE; centred, the same bound refuses 8%.

### ! Corroboration from published subtitling standards

Netflix requires captions *"timed to the audio or within three frames"* ≈ **125 ms** at 23.976 fps;
general spotting practice is **1–2 frames** (~40–85 ms). Perceptually ~**50 ms** is imperceptible.

→ the measured **170 ms** typical lead is **larger than any authoring standard permits**, so a
material part of it is **¬authoring** — it is `silencedetect` firing late on a soft attack, plus
fansub convention. That is a direct argument for the centred measure: centring subtracts the
detector lag and the house convention **together**, ∵ both are common-mode across the population.
Raw `|gap|` charges every subtitle for our instrument's bias.

! Also confirms why interval-overlap was rejected: permitted lead ~125 ms vs permitted trail up to
12 frames (~500 ms) → an overlap estimator inherits a `(L−T)/2` bias by construction.

### ! What this can and cannot deliver

- **Can** justify a bound at whatever clears the measured floor, on uncontaminated evidence, rather
  than "500 ∵ W1 said so".
- **Cannot** deliver **100 ms** *raw*. The per-file authored lead is ¬recoverable from audio at any
  precision — a constant lead and a constant offset are the same signal. Raw `|gap| < 100` refuses
  **82%** of known-good files. ! Measured, ¬argued.
- **Can** deliver **200 ms centred**, at a measured **8%** false-refusal cost on known-good files —
  and 300 ms centred at **0%**. → the answer to the user's target is the **centred** bound.
- Any change here applies to **both** thresholds in effect, ∵ it redefines *in sync*, ¬*worth
  writing*.

### ! The detector-lag lever — proposed, tested, FAILED

The decomposition says `observed gap = authored lead + real error + detector lag`. If `silencedetect`
fires late ∵ it needs a −30 dB level crossing, a speech-trained detector should sit **earlier**, and
that difference would come straight off the floor. `audio-truth.mjs --detector webrtc` exists to test
exactly this.

**Result: no lever.** webrtc median **−224 ms** vs silencedetect **−225 ms** — a **1 ms** difference
— and webrtc's spread was **wider** (IQR 649 vs 482).

→ webrtcvad is ¬systematically earlier than a level gate at this threshold; both need energy to
accumulate. ! This **reversed a lever proposed one message earlier** and is recorded so it is ¬
re-proposed. It also means the ~170 ms lead cannot be reduced by swapping detectors — which is what
makes **centring** (D11) the only route to a tighter bound, rather than better detection.

! Consistent w/ the published standards: authored lead should be ≤125 ms, we measure 170 ms, and the
excess does ¬move when the detector changes → it is common-mode across both detectors and cancels
under centring.

### ! Coverage is ¬uniform across buckets — the sample that could not be taken

`audio-truth.mjs` measured **22 of 25** titles in the `aligned` bucket but only **3 of 25** in the
`inconclusive` bucket. Inconclusive-bucket IQRs ran **885–1639 ms** against **130–300 ms** for
aligned — a ~5× difference.

→ **subtitles in the inconclusive bucket genuinely do ¬track speech onsets cleanly.** That is ¬a
harness defect; it is the same property that makes the shipping check unable to score them. ! It
means the population where the fallback fires is the **hardest** population to obtain ground truth
on, so safety evidence there will always be thinner than on the aligned bucket. Weigh claims
accordingly.

! **Partly mitigated by O8.** The `iqrMs <= 500` gate was doing much of this exclusion, and it is the
wrong gate. Re-gated on SE, the produced-file sample went from **0** to **16 of 20** usable. The
underlying spread difference is real; the *coverage* figures above overstate it.

### Closed on the way here

- **Interval-overlap verifier** (score cue *duration* against speech *duration*, as `ffsubsync` does
  via `2x−1` cross-correlation, rather than matching cue starts to onsets). Less exposed to authored
  lead ∵ it balances the front and back edges. **Rejected**: every cue contributes whether or ¬it
  corresponds to speech, so it needs a metadata filter — `ffsubsync` carries one under the comment
  `# TODO: need way better metadata detector`, and it misses anything not bracketed, not a music
  symbol, and without "english" or " - " in it. The current start-matching method is **structurally
  immune**: an unmatched cue produces no hit at any shift, so it abstains rather than voting. ! Also
  it would make the check mathematically kin to the engine it exists to check → V11/Z3.
- **Trimming detached leading/trailing cues** to stop credits stretching the `PlanWindows` span.
  **Measured, negligible**: detached lead/tail is 4%/2% of the inconclusive bucket vs 2%/2% of the
  aligned bucket — no enrichment — and reclaims a median **3–4%** of span where present.

## Retroactivity — what reopens, and what does ¬

! The two changes have **different** retroactivity stories. Verified against the code, ¬inferred.

### The three hooks that already exist

```
IsStillCurrent (Synced|Skipped)          IsExhausted (Failed)
  && SettingsUnchanged                     && SettingsUnchanged
  && !MinimumWouldNowSync                  && !ToleranceWouldNowAccept
  && !ToleranceWouldNowSync                && FingerprintMatches
  && FingerprintMatches
```

| hook | condition | what it retries |
|---|---|---|
| `ToleranceWouldNowSync` | `Skipped` && `\|AlignedAtMs\| > AlignedWithinMs` | ! **"Tightening retries it"** — a file the audio agreed with, never handed to the engine |
| `ToleranceWouldNowAccept` | `RejectedOffsetMs <= AlignedWithinMs` | a refusal the audio caused — **widening** retries it |
| `MinimumWouldNowSync` | `Skipped` && `moved >= MinimumMovementMs` | lowering the minimum retries what it skipped |
| `SettingsUnchanged` | `SettingsStamp == OutcomeStamp()` | **any settings change** reopens everything |

### Change 1 — the centred 200 ms bound: **retroactive automatically**

`AlignedWithinMs` is a `const`, so no stamp moves — but it does ¬need to.
**`ToleranceWouldNowSync` already exists for exactly this case** and its comment says so. Every
`Skipped` record carrying an `AlignedAtMs` is re-judged against the new bound on the next scan, and
those now outside it are sent to the engine. **No new mechanism, no forced re-scan, no user action.**

! **IMPLEMENTATION REQUIREMENT, easy to miss.** Both tolerance hooks compare a **raw** offset:

```csharp
&& Math.Abs(sits) > SyncVerifier.AlignedWithinMs           // ToleranceWouldNowSync
&& rejected <= SyncVerifier.AlignedWithinMs                // ToleranceWouldNowAccept
```

Under D11 these must become the **centred** test (`|sits − TypicalLeadMs| > bound`). Left raw, the
retroactivity hooks judge by the *old* rule while the live check judges by the new one → records
reopen that the check then immediately re-skips, forever. ! That is a churn loop, ¬a cosmetic
mismatch. **Both call sites, plus `SyncVerifier.cs:200/219`.**

**Scale:** ~**8%** of currently-aligned files become sync candidates (pooled n=49 floor). ! ¬the
889-of-1137 figure from the superseded raw-100 ms analysis — that bound was rejected.

! Records already `Synced` are **not** reopened by a bound change (`ToleranceWouldNowSync` requires
`Status == Skipped`). Correct: those files were already rewritten.

### Change 2 — the VAD fallback: **nothing reopens without D9**

The fallback is always-on w/ **no new setting** (D8) → `OutcomeStamp()` is unchanged →
`SettingsUnchanged` stays true → every previously-refused record stays closed and the recovery is
never realised on the existing library. `ReopenFailed` is a **manual button** (`SyncStore.cs:191`,
`RetryFailed` endpoint), ¬automatic.

→ **D9 exists for this reason**: add a `CheckRevision` token to `OutcomeStamp()`. Bumping it reopens
everything once, and the panel stops reporting verdicts the current check would no longer produce.

! Cost of D9: it reopens **all** records, ¬only the refused ones, ∵ `SettingsUnchanged` is a single
boolean feeding both `IsStillCurrent` and `IsExhausted`. One full re-scan. There is no narrower
lever without a new field on `SyncRecord`.

## UI requirement (recorded, ¬implemented)

User: *"2&3 that split should be relflected in the UI, not grouped into one"*.

`RefusalReasons` groups by the `Message` string (`AutoSubSyncController.cs:104`), rendered by
`reasonBlock('Rejected by audio check', status.RefusalReasons)` at `configPage.html:710`.

→ Splitting the 268 needs **different messages from the orchestrator, ¬a UI change**:

- 179 × "…rescaled the subtitle, and this title is too short for the audio check to measure drift"
- 89 × "…rescaled the subtitle, and the audio check could not measure drift on this title"

The rate-magnitude split (105 × ≈0.1% vs 149 × ≈4.2%) would need a new `SyncRecord` field to carry
the ratio. Data-only, and the more diagnostic of the two ∵ ≈0.1% is V12's signature.

---

## Tooling built (all in `agentic/tools/vadcheck/`)

| File | Role |
|---|---|
| `vad-onsets.py` | Decodes each planned window w/ vendored ffmpeg, runs webrtcvad and/or silero, derives onsets, RLE-caches per-frame flags keyed by `sha256(video\|detector\|params\|window)` |
| `vadcheck.csproj` | **Links** the real `SyncVerifier.cs`, `SubtitleOffsetProbe.cs`, `FfmpegProcess.cs`, `PluginConfiguration.cs`, `Plugin.cs` → the harness cannot drift from what ships |
| `Program.cs` | Scores silence vs each VAD over the same window plan. Caches silence onsets too. Has an uncompiled `Halves()` diagnostic appended |
| `sweep.mjs` | Selects a population from `records.json` by panel `Message` bucket, de-dupes per `ItemId`, hash-verifies, `--min-windows`/`--max-windows`, seeded shuffle |
| `outcome.mjs` | Re-runs the refused sync through `assy-cli` w/ the plugin's pinned config, scores **both** the original and the engine's output. **Run**, n=20 inconclusive |
| `analyse.mjs` | The four decision tables: safety, recovery, drift gained, recall |
| `truth.mjs` | Wraps `check-vs-embedded.ps1` for embedded-track ground truth. ! **WITHDRAWN — unsafe on this library**, every embedded track sampled is `dvd_subtitle` (see O1) |
| `audio-truth.mjs` | Per-title alignment vs the video's **own audio**. Pairs only *decisive* cues (isolated, one candidate onset, nearest ≥ratio× nearer than next). ! Its `iqrMs<=500` gate is the wrong gate → O8 |
| `audio-truth-batch.mjs` | Runs the above over a sweep and joins w/ each source's verdict; emits correct/WRONG ACCEPT per title |
| `authoring-floor.mjs` | The gap distribution across titles the check **already calls aligned** → the authoring+detector noise floor. `--report <json>` re-analyses w/o touching media. ! Writes its aggregate only at the end; rebuild from the per-title files in its `floor-*` temp dir if stopped early |
| `produced-truth.mjs` | **The decisive harness.** Audio truth on the **engine's output**, joined w/ what the check said about that same output — the only measurement that scores the written artefact |
| `corroborate.mjs` | Two-source agreement as an accept gate. **Rejected [R]** (correlated errors); kept as the record |
| `window-arms.mjs` | Compares two sweeps of the same titles under different window rules — the O3 question. **Rejected [R]** |

! Silero v5 needs 512-sample chunks **plus 64 samples of carried context** at 16 kHz, `state` shape
`(2,1,128)`. Without the context it scores everything ≈0. Cost half a day; recorded here so it is
never rediscovered.

---

## O1 — ground truth: the first attempt was WRONG, and is withdrawn

### What was claimed, and why it was wrong

A first pass using `truth.mjs` (which wraps `check-vs-embedded.ps1`) reported **1 wrong accept of
4** and was written up here as evidence webrtc makes bad accepts. **That result is withdrawn. It was
not a small-sample problem — it was the wrong reference.**

! **Every embedded track in the sample was `dvd_subtitle`** — a DVD VobSub bitmap. Verified on all
six titles that produced a "truth" verdict. A bitmap track carries no readable text, so
`check-vs-embedded.ps1` matches cues by **timestamp proximity** (`:28`) and reports the median gap.
Those timings come from a DVD master, ¬from this video.

The case that broke it, MPFC S03E13 *The British Showbiz Awards*:

| reference | reading |
|---|---|
| `check-vs-embedded` | external "OUT by −505 ms" |
| **first cue vs. real speech onset** | cue `7.809 s`, `silence_end` `7.834 s` → **25 ms** |
| **`audio-truth.mjs`** (148→125 pairs) | **−191 to −225 ms** |
| **webrtc** | **−250 ms** |

→ The sidecar is **in sync**. The DVD track is the thing that is ~500 ms off.
`check-vs-embedded` reported the **embedded track's** error and attributed it to the sidecar.
**webrtc's reading agrees w/ direct audio measurement to within ~35 ms.** Grandstand is a **correct
accept**, ¬a wrong one.

! → **`truth.mjs` must not be used to judge a check on this library**, and every verdict it produced
is unsafe. This is exactly what `agentic/CLAUDE.md` invariant 1 warns about: *"an embedded track can
be as desynced as a sidecar"*. The harness was built in contradiction of a documented invariant and
the invariant was right.

### ! Possible contamination of W1 — flagged, ¬resolved

`AUDIT.md` W1 records the shipping check as off "ground truth" by up to **906 ms**, measured w/
`check-verifier-error.ps1`, which uses this same embedded comparison. If those references were also
DVD bitmap tracks, some of that 906 ms is the **embedded tracks'** error, ¬the check's.

! W1's actual conclusion — that the gap is **not** a subtractable constant — stands either way, and
is if anything strengthened (per-source DVD masters differ). Only the **magnitude attribution** is in
doubt. → **Needs re-measurement w/ `audio-truth.mjs` before any figure from W1 is quoted as the
check's own error.** Every place in this document that cited "906 ms" as the check's error is now
marked accordingly.

### The replacement: `audio-truth.mjs`

Measures a sidecar against **the video's own audio**, which is the only reference this project
accepts. It pairs a small set of **unambiguous** cues — isolated, following real silence, w/ one
clean onset decisively nearer than the next (`--ratio`, default 3) — and reports the median gap plus
the IQR. A title w/ under 12 pairs, or an IQR wider than the 500 ms it is judging, returns
**not measurable** rather than a number.

! **Honest limit**: onsets still come from `silencedetect`, so it is ¬independent of that detector.
It **is** independent of the sweep, the gates, and the 250 ms bucket tolerance — which is what is
under test when judging a verdict. Do not present it as detector-independent.

Sensitivity on the validation title (`--ratio` 2 / 3 / 4): median −258 / −225 / −191, IQR 667 / 482 /
466, pairs 170 / 125 / 103. Stable to ~±35 ms; the ratio test was added ∵ without it a cue w/ a
single onset 1.4 s away scored as "unambiguous" and the IQR came back at 1064 ms — noise wearing the
shape of a measurement.

### Status

**Re-running on a FRESH sample** (seed 99, ¬the seed-7 titles this investigation has been iterating
on), `inconclusive` + `aligned`, 25 each, joined by `audio-truth-batch.mjs`.

! **There is currently NO demonstrated wrong accept from webrtc.** That is ¬the same as proof of
safety — it is absence of evidence, and the constraint deserves the positive result.

## Corroboration — tested and **REJECTED [R]**

Proposal: accept only when silencedetect's sub-threshold peak and webrtc's peak land on the same
shift. Reachable w/o a repo change ∵ `SyncVerifier.BestFit` computes the peak then discards it when
the gates refuse, and the two bars it takes as **parameters** can be wound down (`reachable` 0 drops
the hit floor to `MinimumHits`, `rivalRatio` 0 drops the rival test). `PeakRatio` is a private const
and still applies → a flat sweep still yields no peak, which is this measurement's honest limit.

| title | silence peak | webrtc peak | gap | audio truth |
|---|---|---|---|---|
| After the Mold Rush | 0 | 0 | 0 | in sync |
| **Grandstand** | **−325** | **−250** | **75 → AGREE** | **in sync** (−191..−225) |

! Note this table reads differently now that O1 is corrected: Grandstand is **in sync**, so the two
sources agreeing was **right**, ¬a shared error. The corroboration test therefore produced **no
counterexample** — but it also produced no discrimination, ∵ every accept in the sample was correct.

**It remains rejected as a safety mechanism, on principle rather than on this data:** both sources
read the same audio through the same ±4 s sweep at the same 250 ms tolerance. Their errors share a
root cause, so agreement cannot certify correctness — it certifies only that both microphones heard
the same thing. ! A constant authored display lead and a constant real offset are **the same signal
in audio**; no count of audio-only detectors separates them.

→ Kept in `corroborate.mjs` so the case stays re-checkable, ¬as a proposed gate.

## Tooling hazards learned

- ! **`check-vs-embedded.ps1` is ¬ground truth on a library of DVD rips.** It matches by timestamp
  when the embedded track is a bitmap, and every embedded track sampled here was `dvd_subtitle`.
  Probe the codec (`ffprobe -select_streams s -show_entries stream=codec_name`) before trusting it.
  → use `audio-truth.mjs`.
- ! **`silencedetect` logs to STDERR.** `execFileSync` returns stdout only, which is empty under
  `-f null -` → the first `audio-truth.mjs` build parsed 0 onsets and **cached the empty result**.
  Use `spawnSync` and read both streams; clear the cache after fixing a parse bug.
- ! **Two `dotnet run` invocations against the same project contend on `obj/`.** A second sweep
  started while the first was running made the first fail on *every* title w/ a bare
  `Command failed: dotnet run ... --no-build`, while the same binary ran fine standalone. → run one
  sweep at a time, or give each a separate `--configuration` **and** accept that `obj/` is still
  shared. `sweep.mjs` gained `--configuration` for this; it is ¬a complete fix.
- ! **A driver that re-invokes `node sweep.mjs` per bucket picks up mid-run source edits.** Editing
  `sweep.mjs` while a multi-bucket driver is looping changed the arguments of buckets not yet
  started. Finish the sweep, or copy the script before editing.
- ! The `drifting` bucket (33 records) was lost to the above and **has not been measured.** It is the
  lowest-value bucket — drift *was* measured there, so no fallback hook fires and the refusal stands
  regardless — but the sweep should be re-run for completeness.
- Silero v5 needs 512-sample chunks **plus 64 samples of carried context** at 16 kHz, `state` shape
  `(2,1,128)`. Without the context it scores everything ≈0.
- ! **`pgrep`/`pkill` do not exist in this Git Bash.** Every "wait for the other run to finish"
  guard written as `while pgrep -f ... ; do sleep 20; done` **exits immediately** ∵ a missing command
  returns non-zero → two readers landed on the SMB share at once. Serialise via PowerShell
  (`Get-CimInstance Win32_Process`) + `Stop-Process`, ¬the POSIX names.
- ! **`authoring-floor.mjs` writes its aggregate only after the loop** → killing it early discards
  everything. It does drop a per-title JSON into its `floor-*` temp dir as it goes, so an early stop
  is recoverable by rebuilding from those. ! Worth making the tool checkpoint if it is run again.

## Open questions

- **O1 — ANSWERED.** Is webrtc **right** when it says `Aligned`? Measured twice, against audio:
  - source sidecars, fresh `aligned` sample (n=22): **22 accepts, 22 correct, 0 wrong**;
  - **engine output**, `inconclusive` bucket (n=16 usable): **4 accepts, 4 correct, 0 wrong**, plus
    **1 refusal independently confirmed correct** (`Football`, engine broke it by 508 ms centred).
  → **5 of 5 decisions correct on the population the plugin actually writes.** The withdrawn "1
  wrong accept of 4" was an artefact of `dvd_subtitle` reference tracks (see the O1 section).
  ! n is small. The claim this supports is *"no wrong write observed"*, ¬*"wrong writes are
  impossible"*.
- **O2 — ANSWERED.** `outcome.mjs` + `produced-truth.mjs`: on the inconclusive bucket the fallback
  converts **5 of 20** engine runs into writes (**25%**) that today are refused outright — and
  `silencedetect` reaches **no verdict on all 20** produced files, so today that engine work is
  entirely wasted.
- **O7 — NEW, and the most valuable thing left.** **11 of 16** measurable produced files are already
  in sync and the fallback still abstains; the engine fixed **15 of 16** and the check confirms
  **4**. The binding limit is **check sensitivity**, ¬engine capability and ¬safety. Any further
  effort belongs here.
- **O8 — NEW.** `audio-truth.mjs`'s `iqrMs <= 500` gate is **the wrong gate** — it conflates internal
  consistency w/ precision of the centre. Replace w/ `SE = 0.9288*IQR/sqrt(pairs) <= 100 ms`; it took
  the usable sample on the produced bucket from **0** to **16 of 20**. ! Every earlier "not
  measurable" figure in this document was produced by the old gate and understates coverage.
- **O9 — ANSWERED.** Does the engine impose its own lead, requiring a second constant for the
  post-sync check? **No** — median movement on 14 known-good files is **−12 ms** (silence) / **0 ms**
  (webrtc). One constant serves both populations.
- **O5** — `Halves()` attribution of the 89's half-fit failures. **Dropped** w/ the stretch bucket.
- **O6** — the `drifting` bucket (33) was lost to tooling contention and is unmeasured. Lowest value
  ∵ drift *was* measured there → no fallback fires and the refusal stands regardless.

## Decisions taken

| # | decision | basis |
|---|---|---|
| D1 | Operator is a **short-circuit**, ¬a disjunction | 2 of 12 `misaligned` titles would have been bad writes under `OR` |
| D2 | **Drop silero**, carry webrtc only | 0/20 recovery, 0/6 recall, **2 false breaks** on good titles; webrtc already ships in the payload |
| D3 | **Drop Hook B** (separate drift fallback) | usable drift on ~1 in 38; its yield comes free from D4 |
| D4 | Fallback lives **inside `SyncVerifier`**, ¬at `SyncOrchestrator:482` | the stretch guard at :428 precedes :482; verifier-internal is smaller *and* recovers more |
| D5 | **Reject O3** (shorter windows for short titles) | breaks 15% of known-good titles; reproduces N5 |
| D6 | ~~`AlignedWithinMs` stays **500**~~ **SUPERSEDED by D11** | a 100 ms bound re-opens **889 of 1137** already-aligned files, and the readings are **980 positive vs 117 negative** → mostly authored display lead, ¬error. ! The "906 ms check error" that also supported this is now in doubt (see O1); the 889/980-vs-117 evidence is unaffected |
| D7 | Keep the property name `RequireAudioConfirmation` | Jellyfin deserializes config by name → a rename silently reverts it to `true` |
| D8 | Fallback is **always on**, no setting to disable it | user decision; only the checkbox *wording* changes |
| D9 | **(b)** — add a `CheckRevision` token to `OutcomeStamp()`. ! **Confirmed by the user as shipping together w/ the rest**, ¬a later follow-up | D8 changes no setting → without it nothing reopens and the panel would report verdicts the current check would not produce. ! **D13's documented remedy (a full scan) does nothing without it** → the two are one change, ¬two |
| D10 | Config-page wording approved as drafted above | user approved; label unchanged, one clause added to the description |
| D11 | **Judge `\|gap − typical_lead\| < 200 ms`, ¬`\|gap\| < 500 ms`** | user's formulation. Pooled n=49 across two independent seeds: centred 200 refuses **8%** of known-good, raw 200 refuses **39%**. Netflix/spotting standards (≤125 ms authored lead) confirm the measured 170 ms is partly **detector lag**, which centring cancels |
| D13 | **(a)** — retroactive verdict lag is accepted; **document that a full library scan is required after this upgrade**. ¬a *pending re-check* UI state | user decision. A full scan repairs counts **and** reason strings together (both computed live), but only w/ D9 |
| D12 | `typical_lead` is a **population constant (170 ms)**, ¬per-file | a per-file lead is unrecoverable from audio — a constant lead and a constant offset are the same signal. Two independent samples put it at **162** and **170** ms |

## Pre-implementation checks — run before writing any code

! Six things verified against the source after the verdict was written. Two would have been silent
defects; one open question was closed by measurement.

### 1. ! `drift` must **NOT** be centred — the lead cancels in a difference

`SyncVerifier.cs:200` and `:219` use the **same constant** for two different quantities:

| line | quantity | centre it? |
|---|---|---|
| `:219` `Math.Abs(best) > AlignedWithinMs` | an **absolute position** (fitted shift) | ! **YES** — this is what D11 changes |
| `:200` `Math.Abs(spread) > AlignedWithinMs` | `drift = late − early`, a **difference** | ! **NO** — `typical_lead` appears in both halves and **cancels** |

→ **the constant must split in two.** Centring the drift test subtracts a lead that is not there and
biases every drift reading by −170 ms. Same for the stretch guard at `SyncOrchestrator.cs:430`, which
is also a rate/difference quantity. ! Blindly replacing every `AlignedWithinMs` is a defect.

### 2. Sign convention — **confirmed**, code and data

`Hits()` scores `moved = start + shift` against onset buckets → `shift ≈ onset − cue_start`, which is
**exactly** `audio-truth.mjs`'s `gapMs = onset.atMs − cue`. Same quantity, same sign.
Empirically, per title, `bestShiftMs − audioGap` has median **50 ms** across 6 paired titles (scatter
consistent w/ each side's own SE).

→ the centred test is `|bestShift − TypicalLeadMs| > bound`. ! Getting this sign wrong yields
`|gap + 170|` and **doubles** the error instead of removing it.

### 3. Does the engine re-centre? **No — D12 survives**

Concern: engine output showed a **97 ms** median lead vs the source floor's **170 ms**, which would
mean judging produced files w/ a source-calibrated constant burns 40% of the budget.

**Settled by pairing the same 16 titles, source vs produced** (free — onsets already cached):

| | median gap |
|---|---|
| source sidecars (inconclusive bucket) | **97 ms** |
| engine output, same titles | **90 ms** |
| engine movement | **−40 ms** |

→ the 170-vs-97 difference was a **bucket effect, ¬an engine effect**: the *aligned* bucket sits at
170, the *inconclusive* bucket's sources already sat at 97. The engine broadly **preserves** the
authored lead. ! The **aligned** bucket is the correct calibration population precisely ∵ it is the
known-good one; the inconclusive bucket's median mixes convention w/ real error (its source gaps
run −288…+348).

### 4. RESOLVED — the engine imposes **no lead of its own**; one constant serves both

Ran the engine over **15 known-good** (`aligned` bucket, seed 7) titles and compared the check's
reading on source vs output. ! Absolute calibration off the check would be circular; a **difference**
is not — the check's systematic error is common-mode and cancels.

| detector | n | median movement | implied engine-output lead | budget burned |
|---|---|---|---|---|
| silencedetect | 14 | **−12 ms** | 158 ms | 12 ms (6%) |
| webrtc | 14 | **0 ms** | **170 ms** | **0 ms** |

→ **`TypicalLeadMs = 170` serves the post-sync check as well as the pre-sync one. No second
constant.** The earlier 97 ms scare was a bucket effect twice over.

! Per-title movement is ¬small even though the median is (±225 to +400 on individual titles). The
engine does move known-good files around; what it does **not** do is move them in a consistent
direction. → the risk is per-file variance, ¬systematic bias, and variance is what the check catches.

### 4b. ! The direct safety test of the tightening — **zero known-good files broken**

The user's founding constraint is *"It is not acceptable to take a sub that is already correctly
synced and break it's sync."* D11 is the change that could violate it, ∵ tightening sends
previously-untouched files to the engine. Measured directly:

| | n |
|---|---|
| known-good files | **15** |
| **newly sent** to the engine under centred 200 | **1 (7%)** |
| of those, **FIXED** | **1** |
| of those, still out | **0** |
| left completely alone | **14** |

**`The Spanish Inquisition`**: source read **−150 ms** → centred **320 ms**, beyond the measured
floor (centred p95 = 259) → sent → engine produced **+250 ms** → centred **80 ms**. **A genuine
correction of a file today's 500 ms bound leaves alone.**

! **7% observed vs ~8% predicted** from the independent n=49 floor. Two unrelated samples agreeing on
the blast radius is the strongest evidence in this document for the scale of D11.

! **Limits.** n=15, and only **one** title crossed the threshold — this shows the mechanism working,
¬a rate. And a per-file authored lead is unrecoverable (D12), so "−150 is wrong" is an inference from
it sitting 320 ms off a convention whose p95 is 259, ¬a direct observation.

### 5. ! UI staleness on retroactive change — the invariant is at risk

`RecordReconciler` sets `Stale` **only** from *"does discovery still offer this target"*
(`RecordReconciler.cs:38–75`). **It has no notion of whether a verdict is still current.** A record
keeps its old `Status` and `Message` until it is re-processed.

Against the status panel invariant (*"The UI may lag. It may never lie… it may ¬show a count no run
would produce again"*):

- **Without D9** — `SettingsUnchanged` stays true, records never reopen, and the panel reports
  *"rejected as inconclusive"* for subtitles the new check would accept. That is a count **no run
  would produce again** → ! **invariant violation**, ¬mere lag.
- **With D9** — records reopen, but the panel only corrects **as each title is re-processed**. A
  library relying on event handlers alone may never re-touch an unchanged item → the stale verdict
  can persist indefinitely.

→ **RESOLVED — user chose (a)**: accept it as lag, and **document that a full library scan is
required after this upgrade**. ! ¬(b) — no *pending re-check* state is added. → **D13**.

**A full scan does completely repair the panel — numbers and reason strings together — but only
w/ D9.** Verified through the path:

| record kind | gate | reopened by the `CheckRevision` bump? |
|---|---|---|
| audio refusal (`Failed` + `RefusedByAudio`) | `IsExhausted` | ! **yes** — `SettingsUnchanged` false → re-processed, `Message` rewritten |
| aligned / skipped (`Synced`\|`Skipped`) | `IsStillCurrent` | **yes**, same stamp |
| `Unsupported` / `SetAside` | restamped at `:98–109`, **ahead of** `IsExhausted` | **yes**, always |
| `Stale` / `Retired` | never revisited | ! irrelevant — already excluded from the cards by `!Stale && !Retired` |

! `Rejected` (the count, `AutoSubSyncController.cs:83`) and `RefusalReasons` (the strings, `:104`)
are both computed **live** from the same records on every status call → the reasons are ¬cached
separately and cannot lag behind the numbers. They correct together or not at all.

! **Without D9 a full scan fixes nothing** — both gates short-circuit on `SettingsUnchanged` and skip
re-processing entirely. D9 is what makes the documented remedy work.

! `Stale` is the wrong field for this — it means *gone from the library*, and conflating it w/
*verdict out of date* is the same error that produced K1/K3.

### 6. Blast radius of the tightening — **~8% predicted, 7% measured**

Newly-`Misaligned` files are handed to the engine and **written**. Under `ExternalWriteMode =
Overwrite` that **replaces the user's existing subtitle** (vaulted first, so reversible). Implies a
one-off vault growth proportional to ~8% of the library. ! Worth telling the user before it happens,
¬after.

## Implementation checklist — **done**

! Ticked as built. The two unticked boxes are the knowingly-skipped items named at the top of this file.

### A. Centred bound (D11/D12) — no config-page surface

`AlignedWithinMs` is a `const`, ¬a setting → **no config page change, nothing to ask about.**

- [x] `SyncVerifier.cs:59` — introduce `TypicalLeadMs = 170` **and split the constant in two**:
      a centred `AlignedWithinMs = 200` for *positions*, and a separate raw bound for *differences*
- [x] `SyncVerifier.cs:219` (verdict, a **position**) — centred test
- [x] ! `SyncVerifier.cs:200` (drift, a **difference**) — **raw**, ¬centred → see check 1
- [x] ! `SyncOrchestrator.cs:404`, `:430` (drift/stretch, **differences**) — **raw**, ¬centred
- [x] ! `SyncOrchestrator.cs:1051` `ToleranceWouldNowAccept` **and** `:1057` `ToleranceWouldNowSync`
      — **must move to the centred test or retroactivity loops** → see *Retroactivity*
- [x] `verifycheck` + `calibrate.ps1` on the fixed 5-title set — a verdict change is measured against
      titles whose behaviour is already recorded (X4)

### B. VAD fallback (D1–D4, D8)

- [x] Fallback inside `SyncVerifier`, short-circuit: `Misaligned` never consults webrtc
- [x] webrtc onsets via the payload's existing dependency — **no new vendored tool**
- [x] `CheckRevision` token in `OutcomeStamp()` (D9) — ! without it nothing reopens **and D13's
      "run a full scan" instruction is false**. User confirmed D9 ships **with** this work; it is
      ¬separable and ¬deferrable
- [x] Config page wording (D10) — **approved**, drafted above. ! The only config-page edit in this
      whole plan; the label is unchanged and one clause is added to the description

### C. UI refusal split

- [x] Two distinct orchestrator messages for the 268 (179 "too short to measure drift" / 89 "could
      not measure drift") — ! **orchestrator strings, ¬a UI change**; `RefusalReasons` groups by
      `Message` and needs nothing new

### D. Harness debt

- [x] `audio-truth.mjs` — replace the `iqrMs <= 500` gate w/ `SE = 0.9288*IQR/sqrt(pairs) <= 100`
      (O8) before any further measurement
- [x] `authoring-floor.mjs` — checkpoint its aggregate as it goes, ¬only at the end
- [ ] O6: re-run the lost `drifting` bucket (33) for completeness — lowest value. **KNOWINGLY
      SKIPPED** by the user's decision; drift was measured there, so no fallback fires either way
- [x] ~~engine-output lead on known-good files~~ — **done**, O9: no second constant needed

### E. Before any commit

- [x] `.\agentic\tools\verify.ps1` — required by `CLAUDE.md` before **every** commit. Green
- [x] ! `agentic/` must not be named anywhere in published code (hard rule 1)
- [x] ! No documentation in code comments (hard rule 2)
- [x] ! No version bump, no `manifest.json` edit, no commit or push without explicit approval
      (hard rules 4 & 5)

### F. Documentation obligation (D13)

- [ ] ~~`README.md` — state that **a full library scan is required after upgrading**~~ — **REVERTED.**
      The README is a starter overview, ¬documentation; the notice belongs in the release changelog
      and the checklist item below is what carries it
- [ ] Release changelog line saying the same, ∵ that is what a user upgrading actually reads.
      **Deferred to release step 0** — no release cut
- [x] ! If this notice should also appear **on the config page**, that is a **separate config-page
      change and needs the user's approval first** — the only pre-approved config-page edit in this
      plan is the D10 wording. **Asked; the user declined it.** The changelog carries the notice

## Verdict — investigation complete

**Recommendation: implement the fallback, and adopt the centred 200 ms bound. Both are supported by
measurement against audio; neither rests on an unverified claim.**

### 1. Does VAD decrease failed syncs? **Yes, modestly — and safely.**

| | measured |
|---|---|
| inconclusive bucket, engine output | **5 of 20 → writes (25%)** that today are refused |
| `silencedetect` verdicts on those 20 produced files | **0** — today the engine work is wasted entirely |
| projected field recovery | **≈33–38 of 418 refusals (~8–9%)** |
| wrong writes observed, on the population the plugin writes | **0** |

### 2. Is it safe? **No wrong write in any measurement, on any population.**

**The tightening (D11) tested directly against the founding constraint** — 15 known-good files,
**1 (7%) newly sent** to the engine, **1 fixed, 0 broken, 14 untouched**. The 7% matches the ~8%
predicted from the independent n=49 floor.

- source sidecars, fresh `aligned` sample: **22 accepts / 22 correct / 0 wrong**
- engine output, `inconclusive` bucket: **4 accepts / 4 correct / 0 wrong**, and **1 refusal
  independently confirmed correct** — `Football`, where the engine moved a fine subtitle 508 ms
  (centred) wrong and the fallback caught it
- **5 of 5 decisions correct on engine output**, judged against the audio, ¬against the check

! The short-circuit (D1) is what buys this: `silencedetect` saying `Misaligned` ends it, so the
fallback can only ever turn a **refusal** into a write or a firmer refusal. It is write-monotone
against today's behaviour, which is why "no wrong write observed" is a meaningful claim rather than
an artefact of a small sample.

### 3. The tolerance question — **200 ms centred is reachable; 100 ms raw is not**

Pooled **n=49** across two independent seeds (162 and 170 ms typical lead — replication, ¬one draw):

| bound | refuses raw `|gap|` | refuses **centred** |
|---|---|---|
| 100 ms | **82%** of known-good | 35% |
| **200 ms** | 39% | **8%** |
| 300 ms | 6% | **0%** |

→ the user's `|gap − typical_lead| < bound` formulation is exactly what makes the target reachable.
**W1 is contradicted** on uncontaminated evidence: the gap is a population constant plus a tight
spread, ¬per-source scatter. Published standards (Netflix ≤3 frames ≈125 ms; spotting practice 1–2
frames) confirm the measured 170 ms is **larger than any authoring convention permits**, so part of
it is detector lag — which centring cancels and raw `|gap|` charges to the subtitle.

### 4. What is out

**Silero** (0/20 recovery, 2 false breaks) · **Hook B** (~1 in 38) · **O3 shorter windows** (breaks
15% of known-good) · **corroboration** (correlated errors) · **interval-overlap scoring** (needs a
metadata detector; ffsubsync's own author calls theirs inadequate) · **cue trimming** (negligible) ·
**the 268 stretch refusals** (179 structurally unreachable, 89 not worth Hook B).

### 5. ! The honest limits

- **n is small on the decisive measurement** — 16 usable produced files, 5 decisions. It supports
  *"no wrong write observed"*, ¬*"wrong writes are impossible"*.
- **The truth harness is ¬detector-independent.** `audio-truth.mjs` reads onsets from the same
  `silencedetect` the check uses. It is independent of the sweep, the gates and the 250 ms bucket
  tolerance — which is what is under test — but a systematic `silencedetect` bias would move both.
- **The `drifting` bucket (33) is unmeasured** (O6). Lowest value: drift *was* measured there, so no
  fallback fires and the refusal stands either way.
- **The real ceiling is check sensitivity, ¬safety** (O7): 11 of 16 produced files are already in
  sync and the fallback still abstains. The engine fixed 15 of 16; the check confirms 4.

### 6. Recommended order of work

1. **`AlignedWithinMs` → centred 200 ms** (D11/D12). Largest user-visible win, smallest change,
   strongest evidence (n=49, replicated). Independent of the fallback.
2. **The webrtc fallback** inside `SyncVerifier` (D1–D4, D8), always on, `CheckRevision` in
   `OutcomeStamp()` (D9), config-page wording (D10).
3. **Fix `audio-truth.mjs`'s gate** to SE-based (O8) before any further measurement — every
   "not measurable" figure in this document understates coverage.
4. **Then** attack O7 (check sensitivity), which is where the remaining yield is.

! Nothing above is implemented. The user's standing instruction is *"do not implament anything in
the repo yet"* — this document is the record, ¬a change.
