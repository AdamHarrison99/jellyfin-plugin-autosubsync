# Ideas

Unscheduled features live here until they are specified.

## `IDEA-VAULT` — backup vault explorer

A page listing what is in the backup vault: each backed-up subtitle, the item it belongs to, its
language and flags, when it was taken, and why (overwritten, or removed as a duplicate). Restoring
one is a single click, and the restored file is **not** overwritten by the plugin again.

Most of the data is already there — `SyncRecord` carries `ItemName`, `BackupPath`, `Provenance`
and `UpdatedUtc`, and `BackupVault.Restore` is the same call rollback makes. Listing and restoring
is close to mechanical.

**The unshaped part is "will not get overwritten".** A plain restore is undone by the next scan:
the file is discovered, its fingerprint no longer matches the record, and it is synced again. So
this needs a way to pin a subtitle as user-chosen, which does not exist:

- Where does the pin live — a flag on `SyncRecord`, or a separate set keyed by path? A record is
  currently keyed by `(ItemId, TargetKey)` and is rewritten by each pass.
- Does a pin survive the file being edited or replaced by a downloader? Pinning by path outlives
  content; pinning by hash does not.
- Does "clear database" drop pins? Doing so silently un-pins everything the user chose.
- What does the pin do to discovery — skip the target entirely, or process it and refuse to write?
  The second still burns a sync per scan.
- How is a pin removed once set?

Until those have answers this is a page over a store that cannot honour the promise its main
button makes.

## `IDEA-SUBCONV` — convert MicroDVD `.sub` instead of refusing it

`ffsubsync` reads `.srt`, `.ass`, `.ssa` and `.vtt`. A text `.sub` is MicroDVD, which it does not
read, so discovery now marks such a sidecar unsupported and leaves it alone. That was acceptable
while `alass` was in the chain — it was the one engine that read MicroDVD — and became a real gap
the moment the chain collapsed to one engine.

**No new dependency is needed.** `seconv` is already pinned, already downloaded on demand, and is
Subtitle Edit's own conversion CLI; `seconv <input> subrip --outputfilename <out>` is the same call
shape the OCR and HI-strip stages already make, minus `--ocr-engine`. MicroDVD is a Subtitle Edit
format, so this should be a text-to-text conversion needing neither Tesseract nor a bitmap path.

**Step one is to verify that**, because it has not been. Run `seconv` against a real MicroDVD file
and confirm it emits cues. If it does, the rest is wiring an existing stage to a new trigger.

What is unshaped:

- **Which stage does it.** `Convert` currently means OCR, gated on `ConvertImageSubtitles`, and
  reports itself that way in the status panel. A text conversion under an OCR-named switch is a
  lie to the user, but a second stage duplicates the machinery.
- **Whether it is opt-in.** OCR is opt-in because it is slow and needs Tesseract. This is neither,
  which argues for always-on — but always-on changes what a scan touches without the user asking.
- **Frame rate.** MicroDVD cues are frame numbers. The declared header rate is optional and often
  absent or wrong; the fallback is the video's rate, which the conversion would have to be handed.
  Getting this wrong stretches the whole file, and the result still parses.
- **What is written back.** The synced output would be `.srt`, so `Overwrite` mode cannot overwrite
  the `.sub` it came from. That collides with the write-mode contract in a way OCR sidesteps only
  because image tracks were never overwritable in the first place.

The same conversion would also retire the last use of `SubtitleContent`'s MicroDVD reader, which
was deleted when `.sub` support was dropped.

## `IDEA-VAD` — a real voice-activity detector inside the plugin's own check

> **Implemented → `agentic/plans/IDEA-VAD-(DONE).md`.** That file holds the decided
> semantics, the measurements, and the open questions. Read it before adding to this entry.

The audio check finds speech with `silencedetect` at a fixed −30 dB, and on a sizeable slice of the
library it returns nothing at all. That population is now well characterised and it is **not** a
tuning problem: V9 tried an adaptive threshold, V10 tried spectral-flux onsets, X3 tried tripling
the onset supply, N5 tried more and shorter windows. All four are `[R]` in `AUDIT.md`, and V9 and
V10 reached the same root cause from opposite directions — these mixes carry no silence between
lines, so no level threshold and no onset detector isolates speech.

**What makes this worth an entry rather than another threshold attempt**: `ffsubsync` already
carries a trained VAD, and V11 showed it sees what we cannot — Simpsons S01E10, which our check
cannot measure at any threshold by either method, separates 11× on the engine's score. The detector
exists and works on exactly the titles that defeat us. What we cannot do is *use* it: the engine
reports only its verdict on **its own** alignment, and V11's Futurama S01E04 row is the proof that
this is not a substitute — 49.5 a second, inside the honest range, while applying a bogus PAL
stretch. A high score is the engine agreeing with itself. See Z3, raised and refused a second time
at the twenty-seventh pass.

So the idea is narrow and specific: **get a speech track, not a verdict.** If the plugin could
obtain per-frame speech probability for the video, `Score` would keep its whole existing shape —
sweep, hit floor, rival ratio, half-against-half drift — and only swap what supplies the onsets. It
stays an independent check rather than the engine grading its own homework.

What is unshaped:

- **Where the VAD comes from.** `ffsubsync --vad` selects a detector but is not known to emit the
  track. If it cannot be made to, this is a new payload — and every payload is a pinned version, a
  per-RID download, a lock entry and a `verify.ps1` row.
- **Whether it is worth the decode.** A VAD pass is far more expensive than `silencedetect`, and the
  check already reads sampled windows rather than whole audio for that reason. Does the sampling
  plan survive, or does a VAD want the whole track?
- **What the gates become.** `MinimumHits`, `MinimumHitShare`, `PeakRatio` and `RivalRatio` were all
  calibrated against level-threshold onsets. A denser, cleaner onset supply moves every one of them,
  and X3 is the standing warning: three times the onsets pushed discrimination *through* the gate
  because the gained onsets were micro-gaps rather than line starts.
- **Whether it must be optional.** A second detector that disagrees with the first needs a rule for
  which wins, and "whichever says yes" is not that rule.

! Do **not** open this by re-running a threshold sweep. The measurement that would justify it is a
VAD-derived onset list scored against `check-vs-embedded` ground truth on the titles V9 and V10
failed on — if it cannot beat them there, nothing downstream matters.

## `IDEA-ACQUIRE` — download a subtitle, sync it, and keep it only if it verifies

> **Shaped → `agentic/plans/IDEA-ACQUIRE.md`.** That file carries the verified 10.11 API
> contract, the design, nine settled decisions and six pre-implementation checks — five
> answered or deferred, `AQ-P6` still open. Read it before adding to this entry.

For an item with no subtitle in a wanted language, ask Jellyfin's own `ISubtitleManager` for the
provider's result list and work down it: download one, sync it, and let the audio check decide. A
verdict that verifies → the file stays as a sidecar. Anything else → the file is removed and the
next candidate is tried, until one verifies or the list is exhausted. Every attempt is recorded
against the provider's ID for that subtitle so a later run never spends quota on a candidate this
one already rejected. **Only subtitles that both synced and verified are left in the library.**

! **This reverses a decision recorded as permanent.** `RM-SCOPE` in the plan states the plugin
never contacts a provider, and the Phase 10 spec is withdrawn on the same grounds; `QuotaLimiter`
was deleted rather than left as dead code. Opening this means amending both, in the plan, as a
deliberate reversal — ¬adding a phase beside them and leaving the contradiction for a reader to
find.

**What is new since that withdrawal** is the acceptance test. Phase 10 was refused partly ∵ *a bad
match is worse than no subtitle* and nothing could tell them apart at write time. The audio check
now can, on the titles it can measure — that is exactly the question it answers, against the video's
own audio, independently of the engine. The idea is therefore ¬"be a downloader"; it is **use the
check the plugin already has as the gate on someone else's downloader**, and that is the only part
worth building.

What is unshaped:

- ! **`Inconclusive` is ¬a rejection, and treating it as one deletes every candidate.** The check
  refuses a sizeable, well-characterised slice of the library at any threshold by either method —
  the whole basis of `IDEA-VAD`. On those titles the loop either accepts blind or exhausts the list
  and leaves the item worse off *and* poorer. A three-way verdict needs a third behaviour, and
  "keep the first one" is the honest candidate ∵ it matches what the user gets today.
- ! **A successful sync is ¬evidence of a correct match.** Z1: `ffsubsync` picks its rate from a
  fixed list of standard ratios, so every output lands on one — including output from a different
  show's subtitle. The engine's own score is worse still (Z3, refused twice). Only the check's
  verdict can carry this, which makes the point above load-bearing rather than an edge case.
- **The loop multiplies quota spend by design.** Today one item costs one download; this costs up
  to N, and the failures are pure loss. OpenSubtitles limits per account per day → a first sweep
  over a library of any size exhausts an allowance and then keeps failing for a reason the user
  cannot see. Needs a per-run and per-day cap, both persisted, and a dry run reporting intended
  downloads without performing them — the dry-run invariant is a **media-filesystem** lock and
  spending an allowance while it is on breaks the promise without writing a byte.
- **The ledger has no home in the current store.** `SyncRecord` is keyed by `(ItemId, TargetKey)`
  and `TargetKey` derives from a sidecar's path — a rejected download leaves no path to key on. The
  attempt log is keyed by provider subtitle ID and must outlive the file, which is a new store
  shape, ¬a field. Then: does *clear database* drop it? Doing so silently re-buys every rejected
  candidate. Does a rejection expire, given a provider can re-upload a fixed version under a new ID?
- **Deleting what it downloaded is a fourth destructive path.** Three exist (`Overwrite`,
  `Remove`, `RollbackService`) and each pays vault → gate → delete → restorable record. A rejected
  download is the one case where vaulting is arguably pointless — the user never had the file — but
  provenance `Downloaded` is already specced as *rollback deletes, not regenerable without spending
  quota again*, and a deletion nothing can undo needs that decided, ¬assumed.
- **Two downloaders still race.** The withdrawal's original objection is untouched by any of the
  above: if Jellyfin's own *Download missing subtitles* task or Bazarr is enabled, both fill the
  same gap, and this one now also *deletes* what the other just fetched → a loop where each run
  re-buys what the last run rejected. This needs a real "something else manages subtitles" story
  before anything is built.

The three features that used to be in this file — OCR of image-based subtitles, SDH stripping, and
subtitle downloading — have been rolled into the design document as
[*Roadmap: the staged pipeline*](../JellyfinPlugin-AutoSubSync%20plan.md), with specs, cross-cutting
design, and numbered implementation steps as Phases 8–10. They outgrew a scratch list: they share a
data model, a queue, and a rollback contract, and specifying them apart from the plan produced
three incompatible answers to the same questions.

**Before adding an entry here, check whether it belongs in the plan instead.** This file is for
something genuinely unshaped. Once a feature has a data model or touches an existing component, it
is a plan section, not an idea — keeping it here means the plan silently stops describing the
system.

## Conventions

- **Cite by stable ID, never by position.** `RM-SCOPE`, `IDEA-SOMETHING`, `B21` — not "idea 4" or
  "the third bullet". Positional references go wrong the moment anything is inserted or reordered,
  and nothing detects it. See `AGENT-HANDOFF.md`.
- A retired entry keeps its ID rather than renumbering the rest.

## Retired

- **`IDEA-EXTRACT`** — auto-extract embedded subtitles to sidecars. v1 already does this, so the
  entry had no content beyond what shipped.
- **`CC-DUPLICATES`** — a claim that plugin outputs would accumulate per video. Wrong: the plugin
  produces at most one file per (video, language, flags), and OCR/download/stripping compete for
  that one slot rather than adding to it. The real and much narrower version — Jellyfin listing
  both the container's embedded track and the sidecar — is a known v1 limitation documented in
  `README.md`, not a roadmap concern.
