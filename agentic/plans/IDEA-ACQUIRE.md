# IDEA-ACQUIRE — download a subtitle, sync it, keep it only if it verifies — **SHAPED, ¬BUILT**

> Status: **discovery complete, five decisions taken w/ the user, nothing built, no code touched.**
> The 10.11.0 API surface below was read out of the shipped `Jellyfin.Controller` assembly by
> reflection, ¬from memory or from a Jellyfin source tree. *Decisions taken* is settled and is the
> design; *Pre-implementation checks* are measurements that must happen before any code is written.
> `AQ-P4` — the control that could have invalidated the whole feature — **has been run: 0 of 18
> mismatched pairs were accepted.** ! `AQ-P6` has since been **answered and closed**. ! Read its two qualifications before relying on that number.
>
> ! This entry **reverses `RM-SCOPE`**, which is recorded in the design document as permanent. See
> *The reversal* — opening this means amending the plan deliberately, ¬adding a phase beside a
> contradiction and leaving a reader to find it.
>
> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with.

---

## The question

For an item that has **no subtitle at all** in a wanted language, can the plugin obtain one and keep
it **only when its own audio check says the result is aligned** — so that nothing lands in the
library that has not been verified against the video's own audio?

The feature is ¬"be a downloader". Jellyfin already has a downloader and the OpenSubtitles plugin
already has the account, the credential store and the provider quota. The only part worth building
is **the plugin's existing check used as the gate on someone else's downloader.**

Binding rule, carried unchanged from `IDEA-VAD` and from invariant ① in `CLAUDE.md`:

> The plugin must not write a badly synced subtitle when audio confirmation is on, period.

→ For acquisition that rule reads: **a downloaded file the check cannot confirm never reaches the
library.** Recovery yield is subordinate to it.

---

## The reversal

`RM-SCOPE` in the design document decided acquisition is out of scope **permanently**, and the
Phase 10 spec is marked WITHDRAWN on the same grounds. `Services/QuotaLimiter.cs` was deleted rather
than left as dead code. Two of that decision's four supports have moved:

| `RM-SCOPE`'s argument | status now |
|---|---|
| *A bad provider match is worse than no subtitle, and nothing can tell them apart at write time* | **Moved.** The audio check answers exactly this question, against the video's own audio, independently of the engine — see *The thesis* |
| *Two downloaders race to fill the same gap* | **Gone** (`AQ-P6`). Jellyfin has no fetcher of its own — it calls the same provider plugins — and the gap test closes the race w/out a refusal (`AQ-F4`) |
| *A second credential store and a second rate limit to respect* | **Gone by construction.** The plugin holds no credential and speaks to no provider; it calls `ISubtitleManager` and inherits whatever the admin configured |
| *It changes the plugin from "synchronize subtitles" to "obtain and manufacture subtitles", w/ a different support burden* | **Unchanged, and still true.** This is the part the user is deciding, ¬the part evidence can settle |

! If this is built, `RM-SCOPE` and the Phase 10 spec are **amended in place** — the withdrawal
rewritten as a superseded decision naming this document — ¬left standing beside a phase that
contradicts them. The `Acquire` enum value, `SubtitleStage.Confidence` and the `Downloaded`
provenance comment in the plan are all residue of the withdrawal and are reconciled in the same
edit (`AQ-F5`).

---

## The thesis, and the one measurement it rests on

**Z1 (`AUDIT.md`, `[R]`): a successful sync is ¬evidence of a correct match.** `ffsubsync` picks its
rate from a fixed list of standard ratios, so *every* output lands on one — including output
produced from a different show's subtitle. **Z3, raised and refused twice**: the engine's own score
is a statement about its agreement w/ itself; `IDEA-VAD`'s Futurama row scored 49.5 a second, inside
the honest range, while applying a bogus PAL stretch.

→ Neither the engine's exit code, nor its score, nor "the file parses" can separate a right match
from a wrong one. **Only the post-sync audio check can**, and only ∵ it reads the video's audio
rather than the engine's opinion.

! **The first draft of this section claimed the check reads a wrong-title result as `Misaligned`.
`AQ-P4` measured it and that is wrong — it reads it as `Inconclusive` 16 times in 18.** The
protection is real and was measured at **0 accepts in 18**, but its mechanism is **abstention, ¬
detection**, and the difference decides the design:

- the check does ¬*catch* a wrong match; it **fails to confirm** one;
- → what keeps it out of the library is `AQ-Q1` — *confirmation on → an unconfirmed candidate is
  discarded* — and **nothing else**;
- → on the confirmation-**off** path there is no protection left, and `AQ-P4` measured a wrong-show
  subtitle clearing the engine-score gate. See `AQ-P4`.

The honest mechanism: a subtitle for the wrong title cannot be brought into alignment w/ this
video's speech by any constant shift or standard rate factor, ∵ its cue pattern was authored against
different dialogue. The engine still produces an output and still claims a standard rate — and the
check, finding no fit that stands above the noise, **reaches no verdict**. A subtitle for the *right
title, wrong release* is the case the engine genuinely fixes and the check confirms. **The gate
separates those two only ∵ "unconfirmed" is treated as "discard".**

---

## What discovery changed

Five things `IDEAS.md` could not know when the entry was written. Each one removes or shrinks a
concern it lists.

### `AQ-F1` — the bytes can be fetched without writing to the library

`ISubtitleManager.GetRemoteSubtitles(string id, CancellationToken)` returns a **`SubtitleResponse`
carrying an open `Stream`**, ¬a file. `DownloadSubtitles(video, …)` is the one that writes a sidecar
into the media folder under Jellyfin's own naming.

→ **The acquire loop uses `GetRemoteSubtitles` and never `DownloadSubtitles`.** A candidate lands in
the plugin's existing scratch directory, is synced there, is judged there, and reaches the library
only through `SubtitlePlacer` — the same path OCR output already takes.

Three of `IDEAS.md`'s concerns dissolve on this alone:

- **"Deleting what it downloaded is a fourth destructive path"** — there is no fourth path. A
  rejected candidate is a scratch file deleted in the `finally` block that already deletes scratch
  files. Nothing in the media tree is ever touched, so vault → gate → delete → restorable record
  does ¬apply and is ¬weakened.
- **"This one now also *deletes* what the other just fetched"** — it cannot. The plugin's only
  `File.Delete` against a media path remains `RollbackService`, and remains gated on the marker
  suffix **and** a matching `OutputPath`.
- **"Provenance `Downloaded` is specced as *rollback deletes*, and a deletion nothing can undo needs
  that decided"** — the deletion does not exist.

! `DownloadSubtitles` is a **trap, ¬an alternative**: it writes a sidecar Jellyfin names itself, so
the file carries no marker → the next scan discovers it as an ordinary unsynced sidecar, syncs it,
and the plugin has now both downloaded *and* duplicated. Do ¬reach for it ∵ it is one call.

### `AQ-F2` — the ledger has a home, and the shape is already proven

`IDEAS.md`: *"`SyncRecord` is keyed by `(ItemId, TargetKey)` and `TargetKey` derives from a sidecar's
path — a rejected download leaves no path to key on."*

`TargetKey` is **a string, ¬a path**. `SubtitleTarget.EmbeddedKey` already produces
`emb:<index>:<codec>`, which names no file and is stable across rescans; the record it keys survives
having no source file, holds an `OutputPath` after placement, and is never re-discovered ∵ the
output carries the marker suffix. **An acquire target is exactly that shape**:

```
acq:<lang>[:forced][:sdh]        one per wanted, unfilled slot on the item
```

→ the attempt ledger is a **field on the record that key addresses**, ¬a new store. It outlives every
rejected candidate ∵ it was never tied to a file in the first place.

! **The trap this creates.** After placement the slot is filled by `<name>.<lang>.autosubsync.srt`,
and `SubtitleNaming.IsPluginOutput` makes `SubtitleDiscoveryService` **skip** plugin output → the
sync discovery path would report the slot empty again → re-acquire, for ever, spending quota every
night. **The gap test must read the item's raw `MediaStream` list, ¬the discovered target list**, ∵
Jellyfin sees our sidecar as a stream even though our own discovery filters it out.

### `AQ-F3` — Jellyfin's own downloader is detectable in code

`ILibraryManager.GetLibraryOptions(BaseItem)` → `LibraryOptions`, carrying
`SubtitleDownloadLanguages`, `SaveSubtitlesWithMedia`, `DisabledSubtitleFetchers`,
`SubtitleFetcherOrder`, `RequirePerfectSubtitleMatch`, `SkipSubtitlesIfEmbeddedSubtitlesPresent`.

`SubtitleDownloadLanguages` being non-empty means **the server is already fetching subtitles for
that library on its own metadata refreshes.** That is a fact, ¬a config note → the plugin can refuse
to acquire in that library and say so on the status panel, rather than racing it.

### `AQ-F6` — a local subtitle provider is indistinguishable from a downloader

Read off the shipped 10.11.0 assemblies by reflection:

```
MediaBrowser.Model.Providers.SubtitleProviderInfo  { string Name; string Id; }   ← the whole type
MediaBrowser.Controller.Subtitles.ISubtitleProvider { string Name;
                                                      IEnumerable SupportedMediaTypes;
                                                      Task Search(…); Task GetSubtitles(…); }
```

**There is no flag, capability, or interface that says whether a provider touches the network.**
Plugins such as `jellyfin-plugin-localsubs` — which resolves subtitles from **local template paths**
like `Subs\%fn%.%l%.srt` — register through the *same* `ISubtitleProvider` as OpenSubtitles and
appear in `GetSupportedProviders` identically.

→ three consequences, and the first one **invalidates the preflight as first written**:

- ! **"a provider is installed" ≠ "a download can happen".** A server whose only provider is a local
  one returns a non-empty list and will never fetch a byte from the internet. Any copy or logic
  reading provider-count as *downloading works* is **wrong**.
- → **classify by name** (`AQ-Q9`, user decision). The only available discriminator **is** the
  `Name` string, so a maintained whitelist of known downloaders is what the plugin uses. ! This was
  argued against on brittleness grounds and the user decided otherwise — recorded so the
  **mitigations travel w/ the decision**, ¬so the argument gets had twice. The list goes stale by
  construction; `AQ-Q9`'s escape hatch is what stops that being a dead end.
- **Acquiring *from* a local provider is ¬a problem — it is a bonus.** The bytes are fetched, synced,
  verified and placed by the same path regardless of origin (`AQ-F1`), it costs **no allowance**,
  and under *one subtitle per item per language* it can only ever fill a language that had
  **nothing** → it cannot duplicate an existing file. A local-only server simply produces cheap
  *exhausted* outcomes where no local file matched, which is correct behaviour, ¬a fault.

### `AQ-F4` — the Bazarr race is smaller than the withdrawal assumed

A Bazarr-fetched sidecar is an ordinary external subtitle: discovery sees it, the slot is filled,
**no gap → no acquire target → no download.** The two only collide when both act inside the same
window, and the outcome of that collision is a duplicate — which `DeduplicateSubtitles` already
exists to collapse, and which the plugin backs up before removing.

→ What remains of the concern is real but ordinary: *wasted quota*, ¬library damage and ¬a re-buy
loop. The re-buy loop `IDEAS.md` describes needed the plugin to delete what the other downloader
fetched, and per `AQ-F1` it never can.

### `AQ-F5` — half the data model was already built, then orphaned

Left behind by the Phase 10 withdrawal, still in the shipping code:

- `SubtitleStageKind.Acquire = 0` — kept so persisted enum numbering would ¬shift. It sorts **first**
  in `SyncRecord.RecordStage`, which is the correct pipeline position, for free.
- `SubtitleStage.Confidence`, commented *"Provider match confidence, on Acquire only"* — a field
  that exists for a stage that never runs.
- `SubtitleStageKind` ordering `Acquire → Convert → Sync → Transform → Deduplicate → Verify`.

! And one piece of **stale documentation**: the design document's `SubtitleProvenance` block names a
`Downloaded` value. **The enum has no such value** — it is `Retimed=0, Created=1, Superseded=2`. Per
*Read the docs before concluding*, that mismatch is itself a finding and is fixed in the same edit
as `RM-SCOPE` — **deleted, ¬implemented**, per `AQ-Q4`.

---

## The API contract — verified, ¬assumed

Read by reflection out of `Jellyfin.Controller` / `Jellyfin.Model` **10.11.0**, the pinned build.
This is the interface most likely to drift, so it is written down rather than rediscovered.

```
ISubtitleManager
  Task<RemoteSubtitleInfo[]> SearchSubtitles(Video video, string language,
                                             bool? isPerfectMatch, bool isAutomated, ct)
  Task<RemoteSubtitleInfo[]> SearchSubtitles(SubtitleSearchRequest request, ct)
  Task<SubtitleResponse>     GetRemoteSubtitles(string id, ct)         ← the only fetch used
  Task                       DownloadSubtitles(Video, [LibraryOptions,] string id, ct)   ← ! never
  SubtitleProviderInfo[]     GetSupportedProviders(BaseItem item)
  event                      SubtitleDownloadFailure

SubtitleResponse   { string Language, string Format, bool IsForced, bool IsHearingImpaired, Stream Stream }
RemoteSubtitleInfo { string Id, ProviderName, Name, Format, Author, Comment, ThreeLetterISOLanguageName,
                     DateTime? DateCreated, float? CommunityRating, float? FrameRate,
                     int? DownloadCount, bool? IsHashMatch, AiTranslated, MachineTranslated,
                     Forced, HearingImpaired }
SubtitleSearchRequest { Language, TwoLetterISOLanguageName, VideoContentType ContentType (Episode|Movie),
                        MediaPath, SeriesName, Name, IndexNumber, IndexNumberEnd, ParentIndexNumber,
                        ProductionYear, RuntimeTicks, IsPerfectMatch, ProviderIds,
                        SearchAllProviders, DisabledSubtitleFetchers, SubtitleFetcherOrder, IsAutomated }
SubtitleProviderInfo  { Name, Id }
```

Notes that decide code:

- **`Video`, ¬`BaseItem`.** `Movie` and `Episode` both derive from `Video`; the scope resolver
  already returns exactly those two kinds → a cast is total, but is still guarded.
- ! **Do ¬use the `Video` overload — `AQ-P2` measured it and it ignores the admin's provider
  configuration.** It builds a good request (path, content type, provider IDs, runtime, and for an
  `Episode` also `SeriesName` + `IndexNumberEnd`) but leaves `DisabledSubtitleFetchers` and
  `SubtitleFetcherOrder` at their empty defaults, and the request overload filters and orders
  providers **from those very fields** → a fetcher the admin disabled is still searched, and their
  preferred order is not applied. **Build the request by hand**, mirroring that field list and
  filling the two fetcher fields from `ILibraryManager.GetLibraryOptions(video)`. Full reading under
  `AQ-P2`.
- **`isAutomated: true`.** This traffic is automated and providers use the flag to say so. Passing
  `false` misrepresents a nightly sweep as a user clicking *search*.
- **`SubtitleResponse.Stream` must be disposed**, and its `Format` is what names the scratch file's
  extension — ¬the `Format` on `RemoteSubtitleInfo`, which is what the provider *advertises*.
- **`Id` is provider-scoped** (Jellyfin splits it to route the fetch). It is the ledger key, and it
  is why a rejection expires naturally: a re-uploaded, corrected file gets a new ID and is therefore
  a candidate the plugin has never tried.
- **`GetSupportedProviders`** is how the status panel says *why* nothing is being acquired when no
  provider is installed — the same dependency-row pattern Tesseract already uses.

---

## Design

### The gap test

Per item in scope, per wanted language slot: **does any subtitle stream serve this slot?**

- Read the raw `MediaStream` list for the item — external and embedded, **plugin output included**
  (`AQ-F2`'s trap). Image tracks count as serving the slot ∵ the user *has* a subtitle there; OCR is
  the tool for that case, ¬a download.
- ! **An embedded track fills the slot, and that is the default.** A video that carries a `subrip`
  track in a wanted language is **¬**a gap and is never acquired for. This is the single most
  important line in the section ∵ `AQ-P1` measured that *most* items w/out a sidecar do have an
  embedded track — so the opposite default would point the feature at nearly the whole library on
  the day it is switched on. See `AcquireWhenEmbeddedExists` for the opt-out and what it costs.
- ! **One subtitle per item per language — full stop** (user rule, and it governs this whole
  section). **A slot *is* a language.** Anything already in that language fills it: external or
  embedded, plain, SDH or forced, the user's file or one this plugin wrote.
  - → the gap test compares on **`LanguageKey` alone**. It does **¬**use the plugin's
    `(language, forced, SDH)` slot key — that key exists to match and de-duplicate *existing*
    targets, and it is the wrong instrument here. ! An implementer reaching for the nearest
    available helper will pick the wrong one; this is the line that says so.
  - → an item w/ an SDH English track is **¬**a gap in English. The feature never puts a second
    English file beside one that is already there.
  - → ∵ the plugin's own output appears in that stream list (`AQ-F2`), the same comparison is what
    stops a later run buying a second copy of what the last run bought.
  - ! **Known consequence, recorded rather than hidden: a *forced-only* track fills its language
    too.** A viewer wanting full dialogue on such an item gets nothing, ∵ forced tracks carry signs
    and songs. This follows from the rule exactly as stated — if it ever warrants an exception,
    that is a **decision to reopen**, ¬a defect to quietly patch.
  - ! **Second consequence, the same shape: a subtitle naming *no* language makes the whole item
    ineligible.** This document does ¬cover the case and it is common — `Movie.srt` beside the video
    carries no tag at all. An unlabelled track could be any language, so no language can be *proved*
    empty → the item yields **no** acquire target, in any language, rather than a target per wanted
    language. The alternative reading — treat unlabelled as serving nothing — points the feature at
    every item w/ a bare `.srt`, which is a large share of a typical library, and buys a second copy
    of a subtitle the user already has. Recorded here as a **decision**: reopen it, do ¬patch it.
- A gap is a wanted slot w/ nothing in it → one acquire target, ranked **last** so every cheap text
  sync in the library finishes before the first network call is made.
- Costs **zero** extra I/O: `IMediaSourceManager.GetMediaStreams(item.Id)` is already called once per
  item by discovery.
- ! **Preflight: no provider installed → no acquire targets are created at all** (user rule).
  `ISubtitleManager.GetSupportedProviders` is consulted **once per sweep**, before target creation.
  ! It takes a `BaseItem`, so *"once per sweep"* means **once, against the first in-scope item the
  sweep resolves** — ¬once per item, and ¬a separate query. The installed set is server-wide; the
  parameter only lets a provider decline a media *type*.
  An empty list means the feature **does nothing**: it does ¬walk the library, ¬create a target per
  item, and ¬record a failure per item.
  - ! **The gate counts *known downloaders*, ¬providers** (`AQ-Q9`). A server whose only provider
    resolves subtitles from local paths has a non-empty provider list and can never fetch →
    counting providers would arm the feature on a machine that cannot use it.
  - ! **Matching is exact, case-insensitive, on the trimmed `Name`** — **¬substring**. A plugin
    named *"Local Subs (OpenSubtitles naming)"* contains a whitelisted name and is ¬a downloader;
    substring matching would let exactly the plugin this rule exists for through the gate.
  - ! This is a **gate before target creation, ¬a per-item failure**. The naive shape — make the
    targets, let each one discover there is no provider, fail it — produces one failed row per item
    in the library, all saying the same thing, and is precisely the panel-flooding failure the
    *stop the whole sweep* rule exists to prevent elsewhere.
  - → the *only* visible effect is the provider dependency row on the config page. Nothing is
    queued, nothing is counted, nothing is marked failed.
  - ! **The parallel `AQ-F3` rule — refusing to acquire in a library w/ `SubtitleDownloadLanguages`
    set — was `AQ-P6` and is now **deleted**, ¬deferred.** Jellyfin downloads nothing itself; it
    calls the same provider plugins this feature calls, so that field can only ever aim the same
    downloaders at the same gap. → the preflight gates on the **known-downloader count** (`AQ-Q9`)
    and on nothing else, permanently. ! **Nothing in the shipped code reads `LibraryOptions`.**

### Wanted languages

**Reuse `LanguageAllowList`. Add no second list** (user decision). The Scope group already carries a
**Languages** box (`#txtLanguages` → `config.LanguageAllowList`). That list already answers *which
languages does this user care about* → a second one would be a second answer to the same question,
and the two would disagree the first time somebody edited one of them.

! The reuse is ¬free, ∵ a filter and a want list differ in exactly one place, and it is the empty
case. Four consequences, each read out of the code, ¬assumed:

- ! **Empty means *all*, and *all* is ¬acquirable.** `LanguageCodes.Matches` returns `true` on an
  empty list — correct for *filtering* what exists, meaningless for *fetching*, ∵ there is no such
  thing as downloading every language. → **acquisition is inert while that box is empty.** That is
  the right behaviour and it matches `EnabledLibraryIds`' *opt in, never opt out* — but it is
  **silent**, and a user w/ an empty box would have a working plugin and a dead feature w/ no
  indication why. → the toggle's description says so in those words, and *The UI surface* carries
  the line.
- ! **The acquire path reads the raw list, ¬`PassesLanguageFilter`.** That helper passes an
  *unidentified* language on purpose (`Normalize(language) is null || …`), ∵ an untagged track may
  hold signs and songs. An unidentified language cannot be **requested** from a provider → the
  acquire path normalizes each entry through `LanguageCodes.Normalize` and drops what does ¬resolve.
- ! **`Normalize()` does ¬de-duplicate this list** — unlike `EnabledLibraryIds`, which gets
  `.Distinct()`. The box accepts two- and three-letter forms mixed, so `eng, en` is two entries and
  **one** language. → the acquire path distincts the *normalized* codes, or one language claims two
  slots and two lots of the per-item budget.
- **Order is preserved for free.** `Normalize()` trims and filters; it does ¬sort. → the box doubles
  as the preference order, first listed tried first, w/out a new control.

### The candidate list

**One search per provider consulted** (`AQ-Q8` — ! this said *one search per item* before
fall-through existed), then downloads one at a time from that provider's ranked list. A search does
¬spend **download** allowance; a fetch does.

! **Searches are ¬free just ∵ they spend no download quota.** Fall-through can now issue one search
per provider per item, so an item that fails everywhere costs *N* searches instead of one. That is
the price of consulting a second provider at all, and it is paid only by items that need it — the
common case, success on the first provider, still costs exactly one search. ! Providers may
rate-limit **searches** separately from downloads; a search failure is treated as *this provider
offered nothing*, ¬as an item failure.

Filter out before spending anything — each of these costs nothing and saves a download:

| dropped | ∵ |
|---|---|
| a `Format` the engine cannot read | `ffsubsync` reads `.srt .ass .ssa .vtt` and nothing else; a `.sub` is refused today |
| `Forced` | a forced track carries signs and songs, ¬dialogue → it cannot answer a gap for the language. ! Dropped **always**, w/ no setting |
| `HearingImpaired`, **only while `AcquireHearingImpaired` is off** | *SDH* — ! this row is **conditional**, and it is the only conditional filter in the table |
| `AiTranslated` / `MachineTranslated` | a machine translation that syncs perfectly is still ¬what the user asked for. ! `null` means *the provider did not say*, ¬*no* |
| an `Id` already in this record's ledger | this candidate has already been bought and judged |

! **The old wording of the two flag rows said they *"would fill a different slot"*. That reasoning
died w/ *one subtitle per item per language*** — there are no forced or SDH *slots* to fill any
more; a slot is a language. The rows survive for different reasons, stated above, and an implementer
reading the old rationale would build slot logic this design no longer has.

Then rank — **lightly, and ¬from scratch.** `AQ-P5` found the OpenSubtitles provider already sorts
its results by hash match, download count, rating and trusted-uploader status before returning them,
using fields it has and we do not. → **preserve the provider's order** and apply exactly one
promotion on top of it: **`IsHashMatch` first**, ∵ a provider matching the file's own hash means the
same release, which is the one candidate likely to need no sync at all. ! Re-sorting the whole list
on `DownloadCount` fights a sort made w/ better information; a **stable** promotion does not.

`Confidence` on the `Acquire` stage records what put the kept candidate at the top — hash match, or
position in the provider's own ranking — ∵ the field exists for exactly this, and a user auditing a
bad result needs to know which of the two it was.

### The loop, and where it lives

The unit of work is unchanged: one `SubtitleTarget`, one record, one `SyncQueue` admission. Inside
it the acquire target runs

```
for each provider, in the admin's configured order:      ← AQ-Q8
  search that provider alone
  → for each candidate, in rank order, while under the per-item cap:
       fetch bytes to scratch          (spends one download)
       sync                            (engine, as today)
       verify                          (the audio check, as today)
       Aligned      → strip if enabled → place → keep → STOP everything
       Misaligned   → delete scratch   → next candidate
       Inconclusive → STOP everything  (AQ-Q1 / AQ-R4 — ! ¬next provider)
  → this provider's list exhausted, budget remains → next provider
  → cap reached                    → record the refusal, park the record
  → provider refuses on quota      → mark THAT provider exhausted for the rest of
                                     the sweep, no fetch charged → next provider
→ every provider exhausted → record the refusal, park the record
→ every known downloader exhausted → stop the sweep (AQ-P3, as corrected by AQ-Q8)
```

! **`MaxDownloadsPerItem` is a *per-item* budget spanning every provider, ¬a per-provider one.**
Moving to the next provider does ¬reset it. W/ the default of 3 and three providers, the item still
makes at most three fetches in total; w/ `0` (unlimited) it will walk every provider's list to the
end.

! **`Inconclusive` does ¬fall through, and this is the one place the shape surprises people.**
`AQ-R4`: an abstention is a property of **this video's audio**, ¬of the candidate → the next
provider's file buys a second abstention at the price of another download. Fall-through happens on
**`Misaligned` and list-exhaustion only**.

! **The terminal semantics of two existing branches invert for an acquire target**, and getting this
wrong is how a downloaded subtitle silently vanishes:

| branch | existing target | acquire target |
|---|---|---|
| pre-sync check says `Aligned` | leave the file alone → `Skipped` / `Synced` | the download is already correctly timed → **keep it, no engine run** |
| engine moved < `MinimumMovementMs` | discard the output, the original stands | there **is** no original → **no minimum-movement exit at all** |

→ *"the engine found nothing to move"* means *"this file was already right"*, which for a download is
success, ¬a no-op. Both branches currently `TryDelete` the produced file and return.

! **The second row was written as *keep the download as fetched* and is ¬built that way.** Keeping
an output the check has ¬confirmed would write an unverified subtitle into the library on the one
path where no original exists to fall back to — the governing rule forbids exactly that. → the
acquire path has **no minimum-movement branch**: a download the engine barely touched goes to the
audio check like every other candidate and is kept or refused on the verdict alone. This is strictly
safer than the row it replaces, costs one check the sync path also pays, and never silently loses a
file — the branch it removes could only ever have *kept* something unconfirmed.

**Where the loop lives is the largest implementation risk in this document.** `RunPipelineAsync` is
~470 lines carrying every gate the check owns — the stretch guard, the `Inconclusive` branch, the
unverified-shift ceiling — and `orchestratorcheck` covers its decisions precisely ∵ they are subtle.

→ **`AQ-Q2`, decided: extract the verdict→decision logic into one shared helper**, called by the
sync path and the acquire path alike. ! That refactor is a **standalone piece of work w/ its own
proof**: the shared helper replaces the inline gates in `RunPipelineAsync`, `orchestratorcheck` is
extended to cover it, and **the sync path is green before a line of acquire code is written.** Doing
the two together is how a subtle change to the sync gates arrives disguised as a new feature.

### The three verdicts

`Aligned` → keep. `Misaligned` → this candidate is the wrong release or the wrong title; discard and
try the next one. That is the loop working as designed and costs one download.

**`Inconclusive` is the load-bearing case**, and `IDEAS.md` is right that treating it as a rejection
would delete every candidate. Two facts bound it:

1. **Field base rate ≈ 5%.** The 1.4.0.0 field store, idle at 1924 records, holds **95** rows whose
   refusal is *the audio check reached no verdict*, against ~1789 counted targets. So roughly one
   attempt in twenty, ¬a majority. ! That rate was measured on titles that **have** sidecars. The
   acquire population is by definition the titles that do ¬ — plausibly older, more obscure, more
   foreign-language content, whose audio may behave worse. **This is unmeasured and is `AQ-P1`.**
2. **Abstention is overwhelmingly a property of the video, ¬of the candidate.** V9, V10, X3 and N5
   all reached the same root cause from different directions: these mixes carry no silence between
   lines, so the onset supply is too thin or too flat for any subtitle to be fitted against. The
   `webrtcvad` fallback from `IDEA-VAD` already fires here and still abstains on **11 of 16**
   measurable titles.

→ **Trying candidate 2 after an `Inconclusive` is near-guaranteed waste**: the same video yields the
same unmeasurable audio, so the second download buys a second abstention. **The abstention
short-circuits the whole item, ¬just the candidate** — which is also the cheapest direction, and the
one that cannot spend an allowance chasing a verdict that will not come.

What happens to the candidate **already in hand** is `AQ-Q1`, **decided: the existing
`RequireAudioConfirmation` setting governs it**, exactly as it governs a sidecar that already exists.

| `RequireAudioConfirmation` | an `Inconclusive` acquire candidate |
|---|---|
| **on** (default) | discarded from scratch, item stops, the ledger records the attempt → nothing unverified is ever written, and the governing rule holds w/ no exception carved for this feature |
| **off** | falls through to the **existing** unconfirmed path — the engine's score gate (`MinimumEngineScore`) and the `MaximumUnverifiedShiftMs` ceiling — the same code, on the same terms, as any other subtitle |

→ no new setting, no third behaviour, and the user who has already chosen how much they trust an
unmeasurable title has that choice honoured here too.

! **The engine's own score is ¬a tiebreak this feature introduces.** Z3, refused twice. It is
reached only on the path where the user has *already* turned confirmation off and accepted that
trade for every subtitle in their library — ¬as an acquisition-specific fallback.

### SDH

**Off — the default — the plugin acquires the *language*, and nothing else.** SDH is refused, and
an item whose only offers are SDH ends *exhausted* w/ nothing downloaded.

**On, SDH is merely allowed.** ! It is ¬a preference and the list is ¬re-sorted: the ranking stays
exactly as *The candidate list* built it, and the loop's existing **first success wins** rule
decides — the first candidate that syncs and verifies is kept, whether it is SDH or plain.
- ! **→ an SDH file can win while a plain one sat further down the list**, ∵ nothing promotes plain
  over SDH and nothing looks ahead. That is the direct consequence of *first in the list that works*
  and is recorded here so it is ¬discovered as a surprise.
- **The clean pairing is `AcquireHearingImpaired` on + `RemoveHearingImpairedTags` on**: the wider
  pool raises the chance of finding anything at all, and an SDH winner is stripped in the
  `Transform` stage → the user ends up w/ an ordinary subtitle either way. This resolves the tension
  noted below rather than arguing w/ it.

When **off**, the refusal is enforced **twice**, and the two places are ¬redundant.

- **Pre-fetch, on `RemoteSubtitleInfo.IsHearingImpaired`** — free, drops the candidate before any
  download is spent, and therefore **must ¬consume the per-item budget** (the standing rule for
  every pre-fetch filter).
- **Post-fetch, on the bytes, w/ the existing `SdhDetector`** — the plugin **already owns a
  content-based hearing-impaired detector**, which is a far stronger instrument than a provider's
  flag. ! This is `AQ-P5`'s rule applied again: *validate the bytes in hand; never trust the
  advertisement.* The fit is exact and needs no new code: `SdhDetector.Inspect(path)` takes a
  **path**, and the candidate is already on disk in scratch at that point → one call, no re-read of
  the bytes we hold. `AQ-P5` established that OpenSubtitles derives `IsHearingImpaired` from the ID the
  plugin itself built, so for **that** provider the flag is reliable by construction — and that is a
  statement about one provider. Any other provider may omit it, or lie.
- ! **The detector gates only while the setting is off.** W/ it on, SDH is acceptable → the
  detector's verdict changes no decision and no candidate is discarded for it.
- ! **The post-fetch rejection has already spent a download, and it counts.** It is a *fetch
  actually made* → it consumes `MaxDownloadsPerItem`. Anything else would let a provider w/ bad
  metadata drive an unbounded number of fetches on one item.

! **The tension w/ `RemoveHearingImpairedTags` is real and must ¬be papered over.** The plugin can
already **strip** hearing-impaired tags in the `Transform` stage → for a user who has that on and
`AcquireHearingImpaired` **off**, an SDH download would have become a clean subtitle anyway, and
refusing it denies them a working result for no benefit. That configuration is the wasteful one, and
the description should point at the pairing above. The two settings answer different questions — *what may be fetched* vs *what
is done w/ what was fetched* — and the description for `AcquireHearingImpaired` says so, so an
admin can see that turning it off while stripping is on is a **choice to have fewer subtitles**, ¬a
cleanliness measure. ! It is ¬wired to `RemoveHearingImpairedTags` in code: inferring one from the
other would silently override an explicit setting.

- **The refusal gets its own reason string**, distinct from *no candidate offered*. An item whose
  every candidate was SDH and was refused has **¬**been failed by the provider, and the user must be
  able to tell those apart — the fix for one is a different provider, the fix for the other is this
  toggle.
- ! **This setting is about *which candidates are acceptable*, ¬about slots.** Under *one per
  language* an existing SDH track already fills its language → `AcquireHearingImpaired` can only
  ever decide **what gets bought for a language that has nothing**, and can never cause a second
  file to join one that is already present. → it does **¬**widen the acquire population at all,
  unlike `AcquireWhenEmbeddedExists`.

### Quota

- **The plugin cannot read the remaining allowance.** `ISubtitleManager` exposes none, and the
  OpenSubtitles plugin's own *remaining* figure never crosses that boundary. The only signal is a
  **failure** from `GetRemoteSubtitles`, whose exception type is not part of any contract this repo
  controls → `AQ-P3`.
- → **No run cap and no day cap of our own** (user decision). `AQ-P3` measured why: the provider
  tracks `RemainingDownloads` and a `ResetTime`, **checks that counter before issuing the request**,
  and raises `RateLimitExceededException` locally at **zero HTTP cost**. → a cap of ours would be a
  second, worse copy of a number the provider holds exactly — ours guesses the tier, the provider
  *is* the tier, and the two drift the moment the user's account changes. `QuotaLimiter.cs`, deleted
  at the withdrawal, **stays deleted**; so does the persisted daily counter.
- ! **The trade is real and is stated, ¬buried: one sweep may now spend the entire day's
  allowance.** Nothing in the plugin rations it. What bounds *one title* is `MaxDownloadsPerItem`;
  what bounds *the day* is the account, enforced where the true number lives. The feature's
  description must promise no more than that.
- ! **Stop *that provider*, ¬the whole sweep** — **corrected by `AQ-Q8`.** Before fall-through
  existed there was one provider, so *"stop the sweep"* and *"stop the provider"* were the same
  sentence. They are ¬any more: an exhausted OpenSubtitles allowance says **nothing** about whether
  `subbuzz` can still answer. → on `RateLimitExceededException`, mark that provider **exhausted for
  the rest of the sweep**, skip it for every remaining item, and continue w/ the others.
  - **The sweep stops only when every known downloader is exhausted**, which is the original
    intent: an account w/ nothing left must ¬produce one failed record per item in the library, all
    w/ the same cause, filling the panel w/ hundreds of rows describing one fact.
  - ! **A provider marked exhausted must ¬consume `MaxDownloadsPerItem`** — no fetch was made.
  - ! **`AuthenticationException` is per-provider too**, and it is ¬transient (`AQ-P3`): bad
    credentials on one provider must ¬disable the others, and retrying that provider within the
    sweep cannot help → same treatment, different reason string.
- **Do ¬hard-code the provider's limits.** They are OpenSubtitles' to set and to change; a number
  compiled in here is wrong the day they move it.

### Dry run

`CLAUDE.md`: *dry run is a media-filesystem lock*; the design document widens it for this feature to
**no action observable outside the plugin's own record store.**

! **A search is such an action.** It is a network request to a third party, made under the admin's
account, subject to the provider's rate limits. → **dry run performs no search and no fetch**, which
means the design document's stated goal — *"would download 412 subtitles"* — **is not reachable, and
the plan is wrong to promise it.**

What a dry run **can** report, at zero cost and w/ no network: **how many wanted slots are empty**,
i.e. how many items it *would search*. That is the number an admin needs before enabling the feature,
it is honest, and it is free. → the promise is amended to that.

### The ledger

On the acquire record, **one entry per attempt**: the provider subtitle `Id`, the provider name,
when, and the verdict that ended it. It is what makes a second run cost nothing where the first one
failed — **and it is what the Pipeline table's `Acquire` row is rendered from**, which is why it is
per-attempt rather than one verdict per target.

! → **the ledger must survive a kept download being deleted by the user.** The card drops that row;
the stage row must not, ∵ the fetch happened and the allowance was spent. Nothing may prune ledger
entries on the grounds that their file is gone.

- **Expiry: none, by construction.** A re-uploaded fix carries a new `Id` → it has never been tried
  → it is offered. A TTL would re-buy the *same* rejected file on a timer.
- **Exhaustion:** every offered candidate tried and refused → record `Failed`, `RefusedByAudio` true
  (every one of them *was* refused by audio) → the existing *rejected by audio checks* card and its
  reason block report it, `IsExhausted` parks the row so the next scan does ¬re-search, and the
  existing **Retry failed subtitles** button is what reopens it. No new endpoint, no new card.
- **Nothing offered at all:** `Acquire` stage `Skipped`, record `SetAside` → on **no card**, visible
  as a skip on the `Acquire` stage row. ! ¬`Unsupported` — there is no track to be unsupported, and
  400 rows of *"no subtitle was offered"* in the unsupported reason block is noise, ¬information.
- **`Clear database` drops the ledger**, ∵ the ledger is a field on a record. → every rejected
  candidate is re-bought on the next sweep. `AQ-Q3`, **decided: accepted, and the existing confirm
  dialog gains one clause saying so.** ! Its current promise *"No files will change"* stays true and
  stays in place — the cost is **quota, ¬files** — and the new clause must say that without implying
  data loss.
- **A cap stopping the loop is ¬exhaustion.** An item that ran out of `MaxDownloadsPerItem` w/
  candidates still on the list has ¬been answered, and its record must say which of the two happened
  → the reason block distinguishes *every candidate was refused* from *the per-item limit was
  reached*, ∵ only the second is fixed by changing a setting.

### Rollback

A kept download is a file the plugin created, w/ no backup and no original — byte-for-byte the
existing `Created` case, and `RollbackService` already deletes exactly that, gated on the marker
suffix **and** a matching `OutputPath`. Nothing in rollback needs to change to be *correct*.

What is genuinely different is **cost**: rolling back a retimed sidecar restores a file; rolling back
a downloaded one destroys something that cost an allowance to obtain and needs another to replace.

→ **`AQ-Q4`, decided: reuse `Created`.** No `Downloaded` provenance value; no store migration; the
cost is carried by **one clause in the rollback confirm text** instead of by a persisted enum. →
the design document's `Downloaded` comment is deleted rather than implemented (`AQ-F5`).

! One consequence to state rather than discover: after a rollback the ledger goes w/ the record —
`RollbackService` removes every row it undid. → the next sweep re-acquires from scratch and re-buys
whatever it rejected before, the same way `AQ-Q3` describes for *Clear database*. Both dialogs say
so; neither is silent about it.

### The UI surface

The user's constraint: **one new UI element** — a *downloaded* count for subtitles the plugin
downloaded, synced and saved.

! **The status panel invariant splits this into two different numbers, and they must not be
confused.** *The cards describe the library as it is now; the stage table describes work that ran.*
A download that was kept last month and deleted by the user yesterday **is** work that ran and **is
not** in the library → it belongs on the stage table and **¬** on the card.

**The card counts survivors only** — subtitles that are in the library right now ∵ this plugin
fetched them:

```
Downloaded = records on the cards (¬Stale, ¬Retired)
             w/ Status   == Synced
             and Provenance == Created
             and an Acquire stage whose outcome is Succeeded
```

! **`Status == Synced` is what makes it a survivor count, and it is ¬optional.** Without it, a user
deleting a downloaded sidecar leaves the record on the cards — the slot re-opens, so discovery still
offers the target and `RecordReconciler` never marks it stale — and the card would go on counting a
file that is gone. That is the exact failure the invariant calls a defect, ¬a cosmetic issue.

! **The card is computed from records alone — no `File.Exists`.** `/Status` polls every **2 s** while
a scan is running, and the media sits on a slow share; a stat per record per poll is thousands of
round trips a minute over SMB. → the card **lags** by up to one scan after a user deletes a file,
which the invariant permits (*"lag is acceptable, staleness is not"*), and the next scan re-opens the
target and restamps it.

A twelfth card in the existing `statGrid`: no new layout, no new endpoint, no new poll.

**The lifetime figure goes on the Pipeline table's `Acquire` row**, where a number that only grows
is correct rather than forbidden:

| column | counts |
|---|---|
| **Done** | every download ever **kept** — including ones the user has since deleted |
| **Failed** | every download ever **made and refused** by the audio check |
| **Skipped** | targets where nothing was fetched at all: no candidate offered, every candidate filtered out before fetching, library excluded. ! **¬*no provider*** — the preflight means no target is ever created in that case, so it cannot appear here |
| **Avg** | mean time of the acquire stage |

! **The `Acquire` row counts *fetches*, ¬records — the only row on that table that does**, ∵ one
target can buy and refuse several candidates before it keeps one, and a row that counted records
would report *one* against an item that spent three downloads. The counts come from the **ledger**,
which is why the ledger holds an entry per attempt rather than a verdict per target. Every other row
keeps its per-record meaning, and the difference is stated here ∵ a reader comparing the columns
across rows will otherwise assume they are the same unit.

→ **Card ≠ `Acquire`.Done, by design**, and the gap between them is exactly *"downloads that no
longer survive"*. Neither number is wrong; they answer different questions.

Two further surfaces are **existing data-driven elements gaining a row they were built for**, ¬new
elements. `AQ-Q5`, **decided: both approved**, alongside the card:

- the **`Acquire` row on the Pipeline table** — `SummarizeStages` is a gated list whose comment says
  *"Acquire is unbuilt, so it is not here"*; it appears when the setting is on, exactly as `Convert`
  does when OCR is on;
- a **provider dependency row**, rendered by the same `Dependency(...)` helper as Tesseract, w/ the
  **four** states under *Provider row states*. ! An earlier draft gave its second state as *this
  library downloads its own subtitles* (`AQ-F3`) — that claim was **wrong** (`AQ-P6`), and the state
  was replaced by *providers installed, none of them a downloader* (`AQ-F6`), which is measured.

### Settings

! **The first draft listed five new settings. The user cut it to two**, and neither cut is
tidying — each removes a control that duplicates a number something else already holds exactly: the
wanted languages are the **Languages** box (*Wanted languages*), and the run/day budget is the
**provider's own allowance** (*Quota*). The user then asked for two back — the embedded opt-out and
the whitelist escape hatch — so the count is **five**, and they live in a **new `Download`
section**, ¬in `Scope` (*Placement*):

- `AcquireMissingSubtitles` (bool, default **false**)
- **`AcquireHearingImpaired`** (bool, default **false**) — **off, only ordinary subtitles are
  downloaded**: an SDH candidate is never fetched, and one that turns out to be SDH after the fact
  is discarded rather than kept. **On, SDH becomes *acceptable*, ¬preferred** — the list is ¬
  re-ranked, and whichever candidate syncs and verifies first wins, SDH or not.
  ! Default **off**: the plain track is what the feature is for, and refusing SDH also **shrinks
  every candidate list**, which is the cheap direction now that no run or day cap of ours remains
  (*Quota*). See *SDH*.
- **`AcquireWhenEmbeddedExists`** (bool, default **false**) — when on, an item whose only subtitle in
  a wanted language is an **embedded** track is treated as a gap and acquired for anyway. The case
  it serves is real: an embedded track can be a poor rip, mistimed, SDH-only, or a burned-in
  language the user cannot read, and the user may genuinely want an external file to sync instead.
  ! **Default off, and the default is a cost decision, ¬a taste one** — see below
- **`MaxDownloadsPerItem`** — how many candidates one item may download and attempt before it gives
  up on that slot. **The user's setting**, and the one that bounds the worst case a single title can
  cost. Default **3** — the ranked list puts a hash match first, so three attempts reach past the
  hash match into the popularity-ranked ones w/out letting one title eat an allowance. `1` makes the
  feature *try the best candidate and nothing else*, a legitimate and much cheaper mode.
  ! **`0` means unlimited** (user decision) — keep going until the list is exhausted or a terminal
  verdict stops the item. → `Normalize()` clamps to **≥ 0**, ¬≥ 1, and `0` must be read as *no
  ceiling*, never as *disabled*: the master toggle is what disables the feature.
  ! **`0` leaves the account's allowance as the only brake on a single item**, ∵ no run or day cap
  of ours remains (*Quota*). One item w/ a long candidate list can spend the lot. That is the
  user's call to make, and the description says what it does rather than discouraging it.
  ! It counts **fetches actually made**, ¬candidates considered, ∵ the pre-fetch filters drop
  candidates for free and must never consume the budget

- **`AdditionalDownloadProviders`** (comma-separated string, default **empty**) — provider names to
  treat as downloaders on top of the shipped whitelist (`AQ-Q9`). ! This is the **only** thing
  standing between a newly released or renamed downloader and a feature that silently does nothing,
  ∵ nothing in Jellyfin's API identifies a downloader (`AQ-F6`). Same matching rule as the shipped
  list: exact, case-insensitive, trimmed. ! It **adds** to the whitelist and can ¬remove from it —
  a subtractive form would let an admin disable OpenSubtitles here and then hunt for why, when
  `DisabledSubtitleFetchers` in the library settings is the correct place for that.

**Placement — a new `Download` section, per the user.** Five settings bolted onto **Scope** would
make an already long list hard to read → they get their own `<h2>`, in the page's existing section
idiom (`Safety` · `Scope` · `Output` · `Throttling` · `Automation` · `Actions` · `Danger zone`).

**`Download` sits between `Scope` and `Output`.** ! That position is reasoned, ¬aesthetic: the
section **depends on Scope's Languages box** (*Wanted languages*) and acquisition **happens before**
anything in `Output` → the page then reads in the order the work actually runs.

| control | binds | shape |
|---|---|---|
| *Download missing subtitles* | `AcquireMissingSubtitles` | the section's master toggle |
| *Maximum downloads per item* | `MaxDownloadsPerItem` | dependent field, numeric |
| *Download even when the video has an embedded track* | `AcquireWhenEmbeddedExists` | dependent field |
| *Accept hearing-impaired subtitles* | `AcquireHearingImpaired` | dependent field |

- ! **All three dependents grey out while `AcquireMissingSubtitles` is off** (user rule) — the
  numeric field **and** both checkboxes, w/out exception. They use the page's established
  **`dependentOff` + disabled-while-parent-is-off** pattern, the same one *Run OCR when text exists*
  uses under *Convert image subtitles*. ! Reuse it; do ¬invent a second disclosure style, and do
  ¬leave one control live ∵ it 'seems harmless' — a live control under a dead toggle is the page
  telling a lie about what it will do.
- **The provider dependency row lives here**, beside the toggle, ¬in the status panel. It is the
  answer to *why did nothing download*, so it belongs where the user just switched the thing on.
  This refines `AQ-Q5`'s placement; the row itself is unchanged.
  ! **The row has exactly three states and says nothing about Jellyfin's own downloading** — see
  *Provider row states*. A draft line claiming subtitles *"may arrive from Jellyfin as well as from
  this plugin"* was **removed at the user's instruction**, ¬softened.
**Provider row states.** The page already owns the shape — `dependencyRow(dependency)` renders
`{ Name, Message, Ready }`, w/ `Ready: false` adding the `isWarning` class. The provider row is one
more of those, `Name` = *Subtitle providers*, and it has **three** states:

| providers | `Ready` | `Message` |
|---|---|---|
| none | `false` | *None installed. Nothing will be downloaded.* |
| one | `true` | *Open Subtitles.* |
| several | `true` | *Open Subtitles, then subbuzz.* |

! **There is a fourth state, and it is the one this row exists for** (`AQ-F6`, `AQ-Q9`):
providers **are** installed but **none is a known downloader**.

| providers | `Ready` | `Message` |
|---|---|---|
| installed, none a known downloader | `false` | *Local Subs is installed, but it does not download subtitles. Nothing will be downloaded.* |

→ the row distinguishes **three** situations a naive count collapses into one: nothing installed ·
something installed that cannot download · a real downloader installed. ! The middle one is the
common failure this whole finding is about, and it is the only one whose message must name the
provider that misled the admin.

- ! **The several-provider wording says *then*, ¬*and*, ∵ that is what the code does.** The API
  contract sets `SearchAllProviders = false` → the manager walks providers **in the admin's
  configured order** and returns the **first non-empty** list. Naming them in that order tells a
  user which one their downloads are actually coming from; *"and"* would imply a merged pool that
  does ¬exist.
- ! **The order shown must be the order used** — read from `SubtitleFetcherOrder` in the library
  options, ¬the order `GetSupportedProviders` happens to return.
- **Getting the list costs no media I/O.** `GetSupportedProviders` takes an item, so the config
  endpoint resolves **one** video by DB query (`Limit = 1`) and asks about that. ! That is a database
  read, ¬a library scan and ¬a media-file read — the standing no-recursive-scan rule is untouched.
- ! **Disabled fetchers are ¬providers.** A provider present but listed in `DisabledSubtitleFetchers`
  must ¬appear in the row and must ¬count toward the preflight, or the page will claim a provider is
  available while every search skips it.
- ! **`Message` goes through `escapeHtml`** like every other dependency — provider names come from
  third-party plugins and are ¬trusted markup.

**The shipped whitelist — every string read from the plugin's own source, ¬from its listing name.**

| plugin | `ISubtitleProvider.Name` | read from |
|---|---|---|
| `jellyfin/jellyfin-plugin-opensubtitles` (official) | **`Open Subtitles`** | `OpenSubtitleDownloader.cs:56` |
| `jarod46/Jellyfin.Plugin.Addic7ed` (via gestdown.info) | **`Addic7ed/Gestdown Subtitles`** | `Addic7edDownloader.cs:48` |
| `josdion/subbuzz` | **`subbuzz`** | `Providers/SubBuzz.cs:28` + `Plugin.cs:27` |

- ! **The displayed name is ¬the provider name, and guessing costs the whole feature.** The official
  plugin reports **`Open Subtitles`** — *w/ a space* — ¬`OpenSubtitles`. An earlier draft of this
  document used the spaceless form. Under `AQ-Q9`'s exact matching that entry matches **nothing**,
  so a server w/ OpenSubtitles installed would report *no downloader* and acquire **nothing**,
  silently and forever. → **every entry is quoted from source, and a new entry may ¬be added from a
  README, a marketplace listing, or memory.**
- ! **`subbuzz` registers exactly one provider**, ¬one per source. Its per-source strings —
  `[subbuzz] <b>Addic7ed.com</b>`, `[subbuzz] <b>opensubtitles.com</b>`, and a dozen more — are
  built as `$"[{Plugin.NAME}] <b>{NAME}</b>"` and appear only as `RemoteSubtitleInfo.ProviderName`
  on individual results. **Whitelisting them would be wrong**; the only name Jellyfin knows is
  `subbuzz`.
- ! **Provider-supplied names can contain HTML** — verified, ¬hypothetical: those subbuzz strings
  carry literal `<b>` tags. Anything rendering a provider or `ProviderName` **must** go through
  `escapeHtml`. This is the concrete case that rule exists for.
- **`Subscene` is ¬on the list.** It appears as an internal subbuzz source, ¬as a Jellyfin provider,
  and the site it targets is defunct. It was used as a placeholder in an early draft of this
  document's UI copy and that was an invention, ¬a finding.
- ! **The whitelist gates the *preflight and the row*, ¬the search loop.** Once the feature is
  armed, `AQ-Q8` walks **every** enabled provider in the admin's order, whitelisted or not. A local
  provider costs no allowance and may legitimately hold the file (`AQ-F6`), so excluding it would
  throw away free results. ! The whitelist answers *may this feature run at all*; it does ¬answer
  *whom may it ask*. Stated ∵ the two are easy to conflate and the code paths are far apart.
- ! **Our per-provider exclusions must be *unioned* w/ the admin's, never replace them.** `AQ-Q8`
  searches one provider by setting `DisabledSubtitleFetchers` to all the others — built from the
  admin's `DisabledSubtitleFetchers` **plus** our exclusions. Rebuilding that field from scratch
  would **re-enable a provider the admin disabled**, which is a config override w/ the user's
  account and quota on the line.
- ! **Unrecognised providers are logged by name, once per sweep.** Staleness (*Honest limits*) is
  survivable only if an admin can **see** that a provider was found and ¬recognised — otherwise the
  whitelist fails exactly like a wrong string: in silence. The provider row shows it; the log makes
  it greppable.

- ! **The empty-Languages case is surfaced here too.** *Wanted languages* establishes that
  acquisition is **inert w/ an empty Languages box** — silently. → the section states it and points
  at Scope. Two settings pages' worth of distance between a cause and its effect is how a user
  concludes the feature is broken.
- ! **Nothing goes under `Throttling`**, and the existing **Languages** box is ¬moved, ¬relabelled
  and ¬restyled — only its description gains the acquisition clause.
- ! Adding a section is a **config-page change**, and the standing rule is that the page is ¬touched
  w/out asking. This is recorded as approved for **this** section and these four controls only.

! **The status-panel accounting is unaffected by any of this.** The *one new UI element* the user
asked for is the **downloaded card**; settings are form controls, ¬panel figures, and the invariant
that every number on screen describes the library as it is now applies to the panel alone.

! **Two limits remain, and they bound different things.** `MaxDownloadsPerItem` bounds **one
title** — the number a user reaches for when a single bad title is eating their allowance. The
**provider's** limit bounds the account and is the only thing that ends a sweep early. There is no
longer any number in this plugin that bounds a run.

! **`AcquireWhenEmbeddedExists` is the most expensive switch on the page, and its danger comes from
the caps that were just removed.** `AQ-P1` found that items w/out a sidecar mostly *do* carry an
embedded track → turning this on does ¬widen the acquire population by a few percent, it can
**multiply** it, and there is no longer a run cap or a day cap of ours to catch that. The account's
own allowance is the only brake, and it will be reached. → three requirements, all binding:
- the toggle's description states plainly that it can multiply the number of downloads attempted;
- it is a **dependent field of the acquisition toggle**, so it cannot be armed by an admin who has
  ¬already opted in to acquisition;
- ! the **dry run's gap count is computed w/ this setting honoured**, so the number an admin sees
  before enabling acquisition is the number that setting will actually produce — a dry run that
  ignored it would under-report precisely the case that needs the warning.

! **Only `GateStamp()` changes, and languages need no work at all.** `LanguageAllowList` is
**already** in `GateStamp` (`ItemChangeGate`) and already absent from `OutcomeStamp` → editing the
wanted languages already reopens every item the gate closed, which is exactly what acquisition
needs. **Reuse inherits that for free**; a new `AcquireLanguages` would have had to earn it. → the
only additions are `AcquireMissingSubtitles`, `AcquireWhenEmbeddedExists` **and
`AcquireHearingImpaired`**, all three in the **gate** stamp, ∵ both change *which targets exist* — and the second one changes it for a very
large number of items, so an install that flips it must reopen everything the gate closed. `MaxDownloadsPerItem` belongs in **neither** — it is throttling, and `OutcomeStamp` excludes
throttling by design. ! That has a consequence worth stating: **raising `MaxDownloadsPerItem` does ¬reopen an item
that already exhausted the old limit.** The row is `Failed` and parked; **Retry failed subtitles**
is what releases it, and the setting's description must say so.

---

## Cost

The most expensive thing the plugin would do, per candidate: one network fetch + one full
`ffsubsync` run (whole-audio decode) + one verify pass (sampled audio decode). An item that tries
three candidates pays that three times.

- Acquire targets are ranked **last** in discovery order → a sweep does all its cheap text work
  before the first download.
- `SyncQueue` is the concurrency gate and stays the only one; acquire work is admitted through it
  like everything else, so `AdaptiveConcurrency` still governs the machine.
- ! The `AudioSample` is planned from the **subtitle's** cue span, so it is ¬trivially reusable
  across candidates. Whether re-decoding per candidate is acceptable, or the window plan can be
  pinned to the first candidate's, is a measurement — ¬a guess — and belongs w/ `AQ-P1`.

---

## Decisions taken — settled w/ the user

! These are decided. They are the design, ¬a proposal, and are ¬to be reopened by an implementer.

| # | decision | basis |
|---|---|---|
| `AQ-Q1` | On `Inconclusive`, **honour `RequireAudioConfirmation`** — on → discard the candidate and stop; off → the existing unconfirmed path decides, exactly as it does for a sidecar that already exists | user decision. Adds no setting, and acquisition then behaves the way the whole plugin already behaves on an unmeasurable title. ! It also means the *governing rule* holds unchanged: w/ confirmation on, nothing unverified is ever written. ! **`AQ-P4` has since made this decision load-bearing**: 16 of 18 wrong-title pairs were stopped by *this rule alone* — the check abstained rather than detecting them — and on the off branch a wrong-show subtitle cleared the score gate |
| `AQ-Q2` | **Extract the verdict→decision logic into one shared helper**; the sync path and the acquire path both call it | user decision. Two copies guarantee drift on the exact gates that enforce the governing rule; an in-place loop risks the path 1789 existing targets depend on. ! The sync path must be re-proved against `orchestratorcheck` **before** the acquire path is written |
| `AQ-Q3` | **`Clear database` drops the ledger**, and the existing confirm dialog gains one clause saying the next sweep re-buys every rejected candidate | user decision. A ledger surviving the clear button is a second store w/ its own lifecycle. ! The dialog currently promises *"No files will change"*, which stays true — the cost is quota, ¬files, and the wording must say that and ¬imply data loss |
| `AQ-Q4` | **Reuse `SubtitleProvenance.Created`**; no `Downloaded` value. The quota cost goes in the rollback confirm text | user decision. Rollback's verb is identical — delete, no backup, no original — and a fourth enum value would change only a warning string while adding a persisted state to migrate. → the design document's stale `Downloaded` comment is **deleted**, ¬implemented (`AQ-F5`) |
| `AQ-Q5` | **All three approved**: the `Acquire` row on the Pipeline table, the provider status line, and the clear-database clause — alongside the *downloaded* card | user decision. Without the first two, a user whose downloads do nothing cannot see that no provider is installed. ! The second half of this row originally also covered *the library already downloads its own*; that rests on an unverified inference and is now `AQ-P6` |

| `AQ-Q6` | **Add `AcquireWhenEmbeddedExists`**, default **off**, as a dependent field under the acquisition toggle — on, an embedded track no longer fills a wanted slot | user decision. The default direction is forced by `AQ-P1`: most sidecar-less items carry an embedded track, so *on* would aim the feature at most of the library. ! W/ the run/day caps gone (*Quota*), this toggle is the only remaining way for a user to make one sweep enormous, and the dry-run gap count must honour it |

| `AQ-Q7` | **Add `AcquireHearingImpaired`**, default **off** — off, only the plain language track is acquired, refused pre-fetch on the provider flag **and** post-fetch on the bytes via the existing `SdhDetector`; on, SDH is **acceptable but ¬preferred**, the list is ¬re-ranked, and the first candidate that syncs and verifies wins | user decision. ! The *on* branch adds no ranking rule of its own — it only stops filtering, so an SDH file can win over a plain one further down the list. ! ¬wired to `RemoveHearingImpairedTags` — they answer different questions, and inferring one from the other would override an explicit setting |

| `AQ-Q8` | **Fall through to the next provider** when the current provider's candidates are exhausted and per-item budget remains — implemented as **one search per provider**, in the order `AQ-Q10` fixes, w/ `DisabledSubtitleFetchers` excluding the others | user decision. ! `SearchAllProviders = false` alone consults **only the first provider that answers**, so w/out this a second provider is dead weight. Searching lazily (¬`SearchAllProviders = true`) keeps the common case — first provider succeeds — at exactly one search, and spends extra calls only on items that need them. ! `Inconclusive` still stops everything (`AQ-R4`); fall-through is for `Misaligned` and exhaustion. ! **Amended by `AQ-Q10`:** *the admin's order* is no longer the whole rule — anything named in *Additional download providers* is asked first, in the order it was typed |

| `AQ-Q9` | **Whitelist downloader providers by name.** A shipped list of known downloaders; the preflight gates on *≥ 1 known downloader*, ¬*≥ 1 provider*; matching is exact + case-insensitive on the trimmed `Name` | user decision, taken after the brittleness objection was raised and overruled. ! The whitelist **must be admin-extensible** — a shipped-only list means a newly released or renamed downloader is dead until this plugin ships again, which is the objection's real teeth. → *Additional download providers*, a comma-separated field in the `Download` section, merged w/ the shipped list. ! The plugin still ¬needs to know a provider is *good*, only that it is a *downloader*. ! Entries are quoted **from each plugin's source**, ¬its listing name — the official plugin reports `Open Subtitles` w/ a space, and the spaceless guess matches nothing. ! **Amended by `AQ-Q10`:** the same field is also the priority chain, so listing an already-whitelisted name is meaningful rather than redundant |

| `AQ-Q10` | **`Additional download providers` does two jobs, and the UI says so.** It extends the whitelist (`AQ-Q9`) **and** fixes the ask order (`AQ-Q8`): every name in it is asked first, in the order typed, then every other enabled downloader in the server's own order. A name that resolves to no installed provider is reported **in red under the field**; so is one that resolves but is disabled for the library | user decision. Listing `SubBuzz, Open Subtitles` where both are already whitelisted is ¬a no-op — it says *ask SubBuzz first*, which is the only way an admin can express a preference the server has no field for. ! Without the red line, a typo or a renamed plugin is **silent**: the name matches nothing, the order silently reverts, and the user sees a working page. ! The check runs **as the box is typed**, against a `GET AutoSubSync/Providers` list, ¬only on save. ! Provider names come from third-party plugins → the hint escapes them before rendering |

**Total config-page delta: a new `Download` section w/ four controls and a provider dependency row;
one new status card; one conditional stage row; one clause of dialog text.** ! Nothing else on that page changes without asking again.

---

## Pre-implementation checks — before any code is written

! Each of these could invalidate the design. **All five have now been run.** Four are answered
against the shipped assemblies or the real engine; `AQ-P1` is **deferred w/ its reason measured** —
it turned out to need the feature's own dry run, ∵ the sample it would otherwise use is the wrong
population. ! `AQ-P2`, `AQ-P4` and `AQ-P5` each **changed** something in the design above; the
changes are folded in, ¬appended.

- **`AQ-P1` — DEFERRED to the feature's own dry run, and the reason was measured.** The question
  stands: does the check abstain on the acquire population far more than the ~5% the sidecar
  population shows? What changed is *how* it can be answered.
  - The obvious sample — *titles with no subtitle sidecar* — **is the wrong population.** Probed a
    season whose folder holds thirteen videos and exactly **one** `.srt`: every episode sampled
    carries an **embedded `subrip` track**. → they have subtitles, the slot is filled, and the gap
    test would offer none of them.
  - ! **"No sidecar" ≠ "no subtitle", and the difference is most of the population.** Any estimate
    of how much acquisition would do, taken from counting sidecars, is wrong by that margin.
  - → identifying the real population means probing **every video's stream list** across the
    library. That is a library-wide media sweep, which the working rules forbid against this share,
    and it is **exactly what the plugin's own gap test does during a scan**.
  - → **the instrument already exists in this design**: the dry run reports the count of empty
    wanted slots at **zero** network cost (*Dry run*). Run it once before enabling acquisition, and
    it answers both *how many items would be searched* and *which titles to sample for abstention*.
  - ! Until that number exists, **no claim about this feature's yield is supportable** — including
    the ≈5% carried in *The three verdicts*, which is measured on titles that have sidecars and is
    used in this document only as a lower bound.
- **`AQ-P2` — ANSWERED, and it reverses the first draft's recommendation.** Read from
  `MediaBrowser.Providers/Subtitles/SubtitleManager.cs` on `release-10.11.z`, w/ the defaults
  confirmed by reflection against the pinned assembly.
  - The `Video` overload fills `ContentType`, `IndexNumber`, `Language`, `MediaPath`, `Name`,
    `ParentIndexNumber`, `ProductionYear`, `ProviderIds`, `RuntimeTicks`, `IsPerfectMatch`,
    `IsAutomated` — and for an `Episode`, **`SeriesName` and `IndexNumberEnd`**.
  - ! It **never touches `LibraryOptions`**, so `DisabledSubtitleFetchers` and `SubtitleFetcherOrder`
    stay at their defaults — both `string[0]`. The request overload filters and orders providers
    **from those two fields**. → **a fetcher the admin disabled is searched anyway, and their
    preferred order is ignored.**
  - ! `SearchAllProviders` defaults to **`true`** → every installed provider is searched **in
    parallel**, ¬in order until one answers. With it `false` the manager walks providers in the
    admin's order and returns the first non-empty list — fewer requests **and** the configured
    behaviour.
  - `TwoLetterISOLanguageName` is filled by the request overload itself from `Language` → we do ¬
    set it.
  - ! **A provider that throws during search is swallowed** — logged, and an **empty array** is
    returned in its place. → *"no candidate was offered"* and *"the provider was down"* are
    **indistinguishable at search time**. The `SetAside` outcome must ¬be read as *nothing exists*,
    and the ledger must ¬record an item as permanently barren on one empty search.
  - ! `SearchSubtitles` returns empty for any `video.VideoType != VideoType.VideoFile` → **ISO and
    disc-folder rips can never be acquired for**, silently. Worth a distinct skip reason.
  - → **decided: build the `SubtitleSearchRequest` by hand**, mirroring that field list including the
    `Episode` block, and additionally set `DisabledSubtitleFetchers` + `SubtitleFetcherOrder` from
    `ILibraryManager.GetLibraryOptions(video)` and `SearchAllProviders = false`.
  - ! **`AQ-Q8` later changed how this request is *used*.** `SearchAllProviders = false` returns the
    **first non-empty list and stops** → on its own it means a server w/ three providers only ever
    consults **one** of them per item. The fall-through in *The loop* is what fixes that, by issuing
    **one search per provider**, w/ `DisabledSubtitleFetchers` set to every provider **except** the
    one being asked. ! The field list above is unchanged; only the number of calls is.
- **`AQ-P3` — ANSWERED, and no allowance had to be spent.** Read from the OpenSubtitles plugin's
  `OpenSubtitleDownloader.cs`, w/ the type located by reflection.
  - Quota exhaustion throws **`MediaBrowser.Common.Extensions.RateLimitExceededException`**, message
    *"OpenSubtitles download limit reached"*. ! It lives in **`MediaBrowser.Common`**, which the
    plugin already resolves transitively → **it is catchable by type**, and the stop-the-sweep rule
    does ¬have to match on message text.
  - The provider tracks `RemainingDownloads` and a `ResetTime`, and **checks the counter before
    issuing the request** → once the allowance is gone the exception is raised locally and costs no
    HTTP call. A sweep that keeps going after the first one is cheap in bandwidth and still wrong.
  - `ISubtitleManager.GetRemoteSubtitles` **catches nothing** — it splits the ID, resolves the
    provider, and returns `provider.GetSubtitles(...)` → every exception reaches the plugin intact.
  - The other exceptions to expect, distinct from quota: `AuthenticationException` (bad or expired
    credentials — a **configuration** fault, ¬a transient one, so retrying the sweep cannot help),
    `HttpRequestException` (transient), `FormatException` / `ArgumentException` (a malformed ID,
    i.e. our bug).
  - → three behaviours, ¬one: **rate limit → stop the whole sweep** · **authentication → stop and
    say the credentials are the problem** · **transient → fail this candidate, continue.**
- **`AQ-P4` — ANSWERED. The gate holds — 0 accepted in 18. ! But ¬by the mechanism the thesis
  claimed.** 18 deliberately mismatched pairs — **10 wrong-show**, **8 wrong-episode** — assembled
  from the five calibration titles and run end to end through the real engine and the real check.

  | post-sync verdict | wrong-show (n=10) | wrong-episode (n=8) |
  |---|---|---|
  | `Aligned` — **accepted** | **0** | **0** |
  | `Misaligned` — refused by detection | 1 | 1 |
  | `Inconclusive` — refused by abstention | 9 | 7 |

  → the pass condition is met. **Nothing mismatched was accepted, and the feature survives.**

  - ! **16 of 18 were refused by abstention, ¬detection.** The check does ¬recognise a wrong title;
    it fails to find any alignment worth reporting on it. → the only thing standing between a
    wrong-title download and the library is `AQ-Q1`'s *unconfirmed → discard*. That rule is now
    **load-bearing**, and softening it later re-opens this hole. It is ¬a conservatism to be tuned
    away once the feature "settles".
  - ! **On the confirmation-off path, one wrong-show pair would have been written.** Engine score
    per shown second ran **-27.2 … 42.3, median 9.5**; `MinimumEngineScore` is **40**; one
    wrong-show pair reached **42.3** — a subtitle from an entirely different programme, cleared by
    the score gate. **1 in 18 on this sample.** → *Honest limits*, and `AQ-R2` measured a third
    time, now against acquisition specifically.
  - ! **Z1 reproduced.** 12 of 18 came out on the PAL-family ratios **1.042 / 1.043 / 0.959**; the
    other six sat within 0.1% of unity. Offsets ran **-48.5s … +59.0s**. The engine returns a
    plausible-looking rate and offset from another show's subtitle **every single time** → *"the
    sync succeeded"* carries no information about whether the subtitle belongs to the video.
  - The denominator caveat: per-shown-second is computed against the **source** file's displayed
    seconds, ∵ the harness deletes its output. The applied rate moves that by ≤4.3% → the 42.3 pair
    sits somewhere in **40.5 … 44.1**. It clears 40 across that whole band — but ¬by much, and the
    converse holds too: a pair measured just under could truly sit just over.
  - ! `Inconclusive` on a mismatched pair is arguably the instrument's **correct** answer — there is
    no true alignment to find. This is ¬a fault in the check, and ¬something to fix. It is a
    statement about what the check may be **asked** to do: it can certify a match; it cannot refute
    one.
  - Method: the pairs were driven through `check-stretch-outcome.ps1`, which already runs this shape
    end to end. ! The generator + tally are scratchpad-only and die there; the standing harness this
    feature owes is listed under *Harness debt*.
- **`AQ-P6` — ANSWERED: Jellyfin downloads nothing of its own.** ! Raised ∵ the user disputed a
  claim this document made, and the claim turned out to rest on an **inference**. The answer is that
  the server ships **no subtitle provider at all** — `ISubtitleManager` is a dispatcher over whatever
  provider plugins the admin installed, and `SubtitleDownloadLanguages` can only aim those same
  plugins at the same gap. → the *"refuse to race Jellyfin"* idea is **deleted**, ¬deferred: a
  refusal built on it would protect against nothing and would disable the feature on exactly the
  libraries an admin had configured for subtitles.
  - ! **The empirical half — does a refresh act on that field — was never measured and is now
    moot.** Whether it fires or not, both paths end at the same provider plugins, and the gap test
    reads the resulting sidecar like any other. Do ¬reopen this as a measurement; reopen it only if
    Jellyfin ever ships a downloader in core.
  - ! What this does **¬** answer is the **Bazarr** race, which is a separate tool w/ its own
    account. That stays where `AQ-F4` left it: narrowed by the nothing-at-all scope, ¬removed.
- **`AQ-P5` — ANSWERED for OpenSubtitles; the guard stays anyway.** From the same source read.
  - `Format` is **hard-coded `"srt"`** on every search result, and the download ID is
    `srt-{language}-{fileId}[-sdh][-forced]`. → for this provider the *"a format the engine cannot
    read"* filter costs nothing and drops nothing.
  - The stream is a **`MemoryStream` of UTF-8 text** — already decompressed, **never an archive**.
    The gzip worry does not apply here.
  - `IsForced` / `IsHearingImpaired` on the response are parsed back out of the **ID the plugin
    itself built at search time** → they agree w/ the `RemoteSubtitleInfo` by construction, and
    cannot disagree.
  - ! **The filters and the `srt` check are still written**, ∵ every one of these facts is a
    statement about **one provider**. A second provider — installed by the admin, ¬by us — is under
    no obligation to match, and the whole point of going through `ISubtitleManager` is that we do ¬
    control which providers are present. Validate the bytes in hand; never trust the advertisement.
  - **And one design simplification**: the OpenSubtitles provider **already sorts its results** by
    hash match, download count, rating and trusted uploader before returning them. → our ranking
    should **preserve provider order** and confine itself to *filters* + promoting `IsHashMatch`;
    re-sorting the list on `DownloadCount` fights a sort the provider made w/ more information than
    we have.

---

## Implementation checklist — ¬to be started before the pre-implementation checks pass

**A. Plan amendments** — `RM-SCOPE` rewritten as superseded, naming this document; the Phase 10 spec
un-withdrawn or re-specced; the stale `Downloaded` provenance comment deleted per `AQ-Q4`; the
dry-run section's *"would download 412 subtitles"* promise corrected to the gap count.

**B. Config** — the **five** settings; `Normalize()` clamps (! `MaxDownloadsPerItem` clamped
**≥ 0**, w/ `0` meaning **unlimited**, ¬disabled) and trims/dedupes `AdditionalDownloadProviders`;
`GateStamp()` extended w/ the toggle, **the embedded opt-out and the SDH toggle** (! languages are
already there, `OutcomeStamp` is ¬touched, and ! `AdditionalDownloadProviders` goes in **neither** —
it changes *whether the feature may run*, ¬what a run would decide, and stamping it would reopen
every item in the library the first time an admin adds a name); a **new `Download` section between
`Scope` and `Output`** holding all five, the four dependents greyed while the master toggle is off.
! ¬a new language list, ¬a Throttling entry, ¬anything added to `Scope` beyond one appended
sentence on the **Languages** description.

**C. Discovery** — the gap test over raw `MediaStream`s (! ¬the filtered target list, and !
compared on **`LanguageKey` alone**, ¬the `(language, forced, SDH)` slot key); acquire targets keyed
`acq:<language>`, ranked last; the **preflight** (known-downloader count, once per sweep).
! **¬the `AQ-F3` library-options refusal** — that is `AQ-P6` and unverified; building it now ships a
refusal that may protect against nothing.

**D. `SubtitleAcquirer`** — per-provider search + fall-through (`AQ-Q8`), filter, rank, fetch to
scratch, the ledger, the single per-item cap, per-provider quota retirement, the
stop-the-sweep rule. ! Every fetch inside the plugin's existing scratch discipline, deleted in the
`finally` that already exists.

**E. The shared decision helper, then the loop** — in that order, per `AQ-Q2`. ! The helper lands
first, w/ the **sync path green** under `orchestratorcheck` before any acquire code exists. Then the
loop, w/ the two inverted terminal branches under *The loop* and the three-cap floor.

**F. Store** — the ledger field on `SyncRecord`; ! **no daily counter** (*Quota*); a `storecheck` fixture
proving a record written before this version still loads.

**G. UI** — the `Download` section (four controls, `dependentOff` throughout, provider dependency
row, the empty-Languages pointer), the *downloaded* card, the conditional `Acquire` stage row, the
clear-database clause, the rollback clause. **Nothing else on that page.**

**H. Harness debt — ¬optional.** ! **Paid.** `acquirecheck` is the new harness; the rest are
additions to harnesses that already existed, and the standing control is `verifycheck --mismatch`.
- `orchestratorcheck` — the shared decision helper, both callers, and the two inverted branches.
- `stalecheck` — an acquire record through its whole life: offered w/ no file, kept and placed, its
  output deleted by the user, the item leaving scope. ! The re-acquire loop in `AQ-F2` is exactly the
  class of defect this harness exists to catch.
- a new acquire harness covering rank order, every filter, **the per-item cap and the quota stop**,
  the empty-Languages inert case, the `eng, en` duplicate case, and the ledger
- ! **SDH, off:** a candidate advertised SDH is dropped **w/out** consuming the per-item budget; a
  candidate advertised clean that the detector finds SDH **is** discarded **and does** consume it;
  an item offered nothing but SDH ends *exhausted*, ¬*failed*
- ! **SDH, on:** the ranking is **byte-identical** to the same list w/ the setting off and the SDH
  entries removed — i.e. turning it on **inserts** candidates and re-orders nothing. That assertion
  is what stops a future 'prefer plain' tweak from landing unnoticed
- ! **the preflight, twice** — w/ no provider installed the sweep yields **zero** acquire targets
  and **zero** records, ¬a library's worth of failures; and a library w/ `SubtitleDownloadLanguages`
  set yields zero **while a sibling library still yields targets**
- ! **the whitelist strings are asserted verbatim** — a fixture holding `Open Subtitles`,
  `Addic7ed/Gestdown Subtitles` and `subbuzz` matches, and `OpenSubtitles`, `open  subtitles` and
  `[subbuzz] <b>Addic7ed.com</b>` do **¬**. ! This test exists ∵ a wrong string disables the feature
  **silently**, which no other test in this list would catch
- ! **provider fall-through** — w/ two stub providers, all of provider A's candidates `Misaligned`
  and a good one in provider B: the item ends **kept**, having searched **both**. And the negative:
  an **`Inconclusive`** on provider A ends the item **w/out ever searching B**
- ! **the budget spans providers** — `MaxDownloadsPerItem = 2` w/ three providers makes **two**
  fetches in total, ¬two per provider
- ! **`MaxDownloadsPerItem = 0`** — unlimited, ¬disabled: the loop runs past 3 and stops only on
  list exhaustion or a terminal verdict
- ! **the gap test against embedded tracks, both ways** — an item w/ an embedded wanted-language
  track yields **no** target w/ `AcquireWhenEmbeddedExists` off and **one** w/ it on
- ! **one per language** — an item carrying a **forced** track, an **SDH** track, or a subtitle
  **this plugin wrote on an earlier run** yields **no** target for that language. A comparison that
  reaches for the `(language, forced, SDH)` slot key passes the embedded test above and **fails
  all three of these**, which is exactly how it would reach a release unnoticed — over a **stub provider**, so it needs no network and spends no
  allowance. ! It must prove a filtered-out candidate does ¬consume the per-item budget.
- `rollbackcheck` — a kept download deleted, and a record whose candidates were all refused keeping
  its row.
- ! **a standing wrong-title control.** `AQ-P4` was run from the scratchpad and its generator died
  there. The measurement it makes — *does a mismatched subtitle ever come out `Aligned`* — is the
  single result this whole feature rests on, and it must be re-runnable against a changed engine,
  changed thresholds, or a changed check. → a harness that builds the mismatched matrix from the
  local calibration set and tallies the verdicts. ! Its fixtures are library files → it reads them
  from the untracked local config like the other calibration tools, and **commits none of them**.
  - → built as **`verifycheck --mismatch`**, ¬a new project: the calibration set, the vendored
    ffmpeg and the linked `SyncVerifier` are all already there, and a second copy of that wiring is
    a second thing to drift. It reads `calibrate.local.json`, pairs every video w/ every **other**
    title's subtitle, prints the matrix, and **exits non-zero on a single `Aligned`**.
  - ! **It is ¬in `verify.ps1`.** Five titles is twenty pairs of real audio reads over a network
    library — it is a control a maintainer runs deliberately, on a change to the check, the engine
    or a threshold. `--cases <path>` runs a subset.

**I. Documentation** — `ARCHITECTURE.md` gains the acquire path and the gap test; `CLAUDE.md`'s
dry-run and security invariants gain the network clause; `README.md` gains one line per setting.

**J. Before any commit** — `.\agentic\tools\verify.ps1` from the repo root, zero warnings.

---

## Honest limits

- **The gate is only as good as the check.** On a title the check cannot measure, this feature has
  no way to tell a good match from a bad one, and `IDEA-VAD` established that such titles are a real
  and characterised slice of any library. `AQ-Q1` is how that is handled, ¬how it is solved.
- **The check refuses a wrong title by abstaining, ¬by recognising it.** `AQ-P4`: 16 of 18. → the
  safety of this feature rests on the *policy* that an unconfirmed candidate is discarded, ¬on any
  ability to detect a mismatch. Stated plainly ∵ it is easy to read *"0 accepted in 18"* as the
  check being good at this. It is ¬. It is good at declining to guess.
- ! **W/ `RequireAudioConfirmation` off, this feature can write a subtitle from a different
  programme.** Measured, ¬theorised: `AQ-P4` had one wrong-show pair score **42.3** per shown second
  against `MinimumEngineScore = 40`. On that path the engine score is the last gate, and Z3 already
  said the engine score is the engine agreeing w/ itself. **1 in 18 on an 18-pair sample** is a rate
  worth neither trusting nor dismissing — the direction is what matters. → the README line for the
  acquisition toggle must ¬describe the feature's safety without naming this dependency.
- **`IDEA-VAD`'s `O7` is the binding limit here too**: 11 of 16 measurably-correct files were still
  abstained on. Every one of those, in this feature, is an allowance spent for no verdict.
- ! **The downloader whitelist goes stale, by construction** (`AQ-Q9`). Nothing in Jellyfin's API
  distinguishes a downloader from a local file scanner (`AQ-F6`), so the plugin matches provider
  **names** against a list it ships. A newly released downloader, or one that renames itself, is
  invisible until the admin adds it to *Additional download providers* or this plugin ships an
  updated list. → **the failure is visible and recoverable** — the provider row names what it saw
  and the escape hatch is on the same page — but it is a failure, and it will happen.
- **Provider match quality is inherited, ¬improved.** The plugin can only judge what it is handed. If
  the provider offers nothing but wrong-release files, the loop's honest outcome is *exhausted*, and
  the user has paid for that answer.
- **n=1 library.** Every field figure in this document comes from one real library. The acquire
  population — items w/ no subtitle at all — has ¬been counted even once.
- **This makes the plugin a downloader.** `RM-SCOPE`'s support-burden argument is untouched by
  everything above: bad matches will arrive as sync bug reports.

---

## Rejected alternatives

| # | rejected | ∵ |
|---|---|---|
| `AQ-R1` | `ISubtitleManager.DownloadSubtitles` | writes an unmarked sidecar into the library → discovered next scan as an ordinary subtitle → downloaded *and* duplicated. `AQ-F1` |
| `AQ-R2` | The engine's own score as the acceptance gate | Z3, refused twice, and now measured a third time. A high score is the engine agreeing w/ itself — 49.5 a second while applying a bogus PAL stretch, and in `AQ-P4` **42.3 a second on a subtitle from a different programme**, over the 40 threshold |
| `AQ-R3` | "The sync succeeded" as evidence of a correct match | Z1 `[R]`, reproduced in `AQ-P4`: all 18 mismatched pairs produced a confident-looking rate + offset, 12 of them on a PAL-family ratio, built from another show's subtitle |
| `AQ-R4` | Trying the next candidate after an `Inconclusive` verdict | the abstention is a property of the video's audio → the next candidate buys a second abstention. `IDEAS.md` names this as the failure that *"leaves the item worse off and poorer"* |
| `AQ-R5` | A dry run that reports intended downloads | it would have to search, and a search is an outbound call under the admin's account. The gap count is free, honest, and answers the same question |
| `AQ-R6` | The plugin holding its own OpenSubtitles credentials | a second credential store on an elevated endpoint, for an account the OpenSubtitles plugin already owns |
| `AQ-R7` | Hard-coding the provider's daily limits | they are the provider's to change; a compiled-in number is wrong the day they move it |
| `AQ-R8` | A separate store for the attempt ledger | `AQ-F2` — the record's key never needed to be a path, and a second store is a second lifecycle to get wrong |
