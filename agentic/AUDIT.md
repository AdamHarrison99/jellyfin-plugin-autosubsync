# Audit History

Findings from pre-release audits, per the checklist in `CLAUDE.md`. Recorded so known false positives are ¬re-flagged and accepted risks stay traceable.

> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap · `w/` with · `≈` about.
> Status: **[F]** fixed + shipped · **[O]** open · **[A]** accepted, deliberate · **[R]** measured and rejected.
> Finding IDs are `<letter><n>`, one letter per pass, and a letter is never reused — `H1` means one thing forever. **Used: A B C D E F G H J K L M N P Q R S T U V W X Y Z AA AB AC AD AE AF AG. Take the next pass's from `AH`.** ! `I` and `O` are held back ∵ `I1`/`O1` read as `11`/`01`; when the pool runs out, go to `AA`, ¬to those.
> Newest first. ! Passes before the fifteenth are collapsed to one line per finding — the code and `ARCHITECTURE.md` carry what shipped. What survives here is *why* something is the way it is, and what must ¬be re-flagged.

---

## 2026-08-21 (forty-sixth pass) — the interim write, the retry window, and what a set-aside row still carries

Scope: **the full eleven-item checklist over the uncommitted delta** on top of 1.6.2.0 — the panel corrections raised from the field (mid-scan card movement, the download limit rendered as a failure, one sentence under two headings, the language named on the inconclusive message), the search cooldown, and the retry setting that replaced its constant. Version unchanged at 1.6.2.0, nothing committed.

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AG1 | `SubtitleAcquirer` fetch wall | Only the **pre-fetch** wall check counted `Walled`. A wall raised by the fetch itself — the ordinary shape, ∵ an allowance surfaces on a download and ¬on a search — left `Answered` true → the row parked for the whole retry window w/ the rest of that provider's list never seen, and fell through to *no subtitle could be used* about offers never fetched | High | **[F]** |
| AG2 | `AutoSubSyncController` stage summary | `IsAudioRefusal` requires `Status == Failed`. Moving `CapReached` to `SetAside` (AG's own delta) left its per-candidate `Verify` refusals **counted as tool failures** while the row sat on no card — both halves of what the panel invariant forbids, and the ordinary case at the default limit of 3 | High | **[F]** |
| AG3 | `SyncOrchestrator.LogSetAside` | `Reason` strips a one-word action prefix only, so `"Set aside: …"` survived it and the line read `Set aside X (Y): Set aside: …`. The line exists so a spent allowance is visible in the log at all | Low | **[F]** |
| AG4 | `SyncOrchestrator.Spent` | At `RetryDownloadsAfterDays == 0` the window is empty → the per-item limit is counted over nothing and an item may spend it again on **every scan**, bounded only by the ledger and by re-uploads earning new ids | Med | **[A]** |
| AG5 | `SyncOrchestrator.BudgetSpent` | Gated on `SetAside` alone → a `Failed` acquire row released by the retry window re-entered w/ a per-**run** allowance while a set-aside row got a per-**window** one. Invisible asymmetry between two rows that differ only in how their last run ended | Med | **[F]** |
| AG6 | `SyncOrchestrator.RetryDue` | Read `UpdatedUtc`, which `SyncStore.Upsert` restamps on **every** write. Safe in today's steady state, but a scope change or a dedupe pass would postpone the retry by another whole window w/ nothing on screen to explain it | Med | **[F]** |
| AG7 | `SubtitleAcquirer` already-tried fall-through | A lapsed window that re-offered only ledgered ids returned `AllFiltered` → the orchestrator **overwrote a refusal w/ a set-aside**, and the row left every card. The rejection counts drain toward zero over successive windows while nothing about the library changed | Med | **[F]** |
| AG8 | `SyncOrchestrator.FailCandidate` | The interim write also persisted the ledger. A process kill mid-loop now loses it, and under a per-window budget the ledger **is** the budget → a lost entry both re-spends the allowance and re-buys a file already paid for | Low | **[A]** |
| AG9 | `SyncStore.ReopenFailedIn` | Skipped anything ¬`Failed`, and both new gates require `SetAside` → the config page's retry button did nothing for a row waiting out its cooldown. Pre-existing for `BudgetSpent`, newly user-visible now the wait has a name on the page | Med | **[F]** |
| AG10 | `SubtitleAcquirer.Tried` | Matches on `SubtitleId` alone though the field is provider-scoped. Two providers sharing an id namespace cross-suppress, and a re-upload defeats it. Errs toward spending less → harmless in itself, but it is the one mechanism holding AG4 bounded | Low | **[A]** |
| AG11 | `configPage.html` `.statusPanel` | `container-type: inline-size` outlived the `@container` rules the delta replaced w/ an `auto-fit` grid | Info | **[F]** |

**AG2 is the pass's lesson, and it is the third time this shape has appeared.** `IsAudioRefusal` was written as *the* single place the rejected/failed line is drawn — and it draws that line on `Status == Failed`. Reclassifying a status **moved rows out from under a predicate that was never keyed on the thing it was reporting.** `CarriesAudioRefusal` now names what the stage table actually asks: ¬*did this row fail as a refusal*, but *does this row carry refusals the check made*. ! The general rule: **when a status changes meaning, grep every predicate that reads that status, ¬only the ones that read the field you edited.**

**AG4 is accepted on the maintainer's explicit instruction** that `0` means no cooldown. The alternative — counting the whole ledger at `0` — restores 1.6.2.0's lifetime cap and makes `0` mean *search again* only, which is ¬what was asked for. The cost is written on the setting's own description, where an admin choosing `0` will read it.

**AG8 is the accepted cost of the mid-scan-movement fix.** Both `ProcessAsync` catch blocks still store the row, so cancellation and unhandled exceptions are covered; exposure is a hard kill or crash inside the acquire loop, where the previous scan's row survives — lag, ¬a lie.

**Verified clean, w/ what was checked rather than assumed:** the **AE3 upgrade trap is cleared** — `git log -p --all` over `SyncOutcome`, `SyncDecision` and `SubtitleAcquirer` yields exactly three shipped no-verdict sentences and all three reach `IsInconclusiveRefusal`, two by the legacy array and the historical exhausted form by prefix, which `NoVerdictExhausted(null)` reproduces byte-for-byte → the harness tests the shipped string · no other `"Rejected:"` message in the tree begins w/ that prefix, so nothing is captured by accident · **`OutcomeStamp` at defaults is byte-identical to 1.6.2.0** (`confirmed`), so the `RequireConclusiveDownloads` term invalidates no stored record and reopens exactly the rows of a user who had already turned the gate off · `SearchedRecently`'s `SetAside` scoping holds — the discovery-suppression sites cannot reach an `AcquireKey` row (`SuppressCoveredEmbedded` skips `IsExternal`, the acquire candidate is built `IsExternal: true`; `SuppressOcrCoveredByText` requires `RequiresOcr`) · `SearchStamp` covers every setting `Filtered()` reads · `AcquireAttempts` survives `ReopenFailedIn` and `Remeasure`, so the anti-repeat filter holds across a manual retry and a measurement bump · `AttemptedUtc`/`SearchedUtc` round-trip as `DateTimeKind.Utc` · skipping the interim store cannot leak a candidate failure into a kept row ∵ `KeepAsync` resets every field `MarkFailed` writes · `SyncDecision`'s `NoVerdictRefusal` reaches a downloaded candidate but never the store, which is now load-bearing on the `Fail`/`FailCandidate` split · no new endpoint, process spawn, client-supplied path or media-tree write · the new log lines carry item name, target key and a canned sentence, ¬subtitle content · `SearchedRecently` returns ahead of the `DryRunMode` branch but performs no I/O and no network call, and correctly suppresses a *would be searched* claim for a row it would ¬have searched · comment linter clean over 183 files · build 0 warnings, 0 errors.

---

## 2026-08-21 (forty-fifth pass) — the setting the description already described, and the per-source wall

Scope: **the full eleven-item checklist over the uncommitted delta**, which the forty-fourth pass's delta is now folded into — the inconclusive-download setting rebuilt to the behaviour its own description promises, renamed to `RequireConclusiveDownloads`, and retirement narrowed from the provider to the internal source inside an aggregator. Version unchanged at 1.6.1.0, nothing committed.

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AF1 | `SubtitleSourceKey` | A label token confirmed against **any** id containing an underscore invents a source for a provider that has none — a label that happens to prefix its own id is enough. A wall then narrows to the invention instead of stopping the provider, and a **spent account goes on being asked** for every offer behind it | Med | **[F]** |
| AF2 | `SubtitleAcquirer` fall-through | A list left entirely behind a walled source fell through to `AllFiltered` and reported *no subtitle offered for this language could be used*. Those candidates were usable; the allowance is what stopped them, and the card blamed the files | Med | **[F]** |
| AF3 | `acquirecheck` fixture | The aggregator-id fixture carried a **plugin guid read out of the field store** rather than a synthetic one. Benign in class — it identifies a third-party plugin, ¬the user — but it is field data reproduced verbatim in a published file for no reason | Low | **[F]** |

**The setting was the finding, and the code was what had to move.** `RequireConclusiveDownloads` off now **keeps** the first candidate nothing refused; the build discarded it and stopped the item. → the description was the specification and the behaviour was the defect, ¬the other way round. ! **The insertion point is what makes it faithful.** The flag is passed into `SyncDecisionMaker.Decide` as `requireConfirmation`, ¬applied as an override on `decision.Accepted` afterwards, so an abstention falls through to the gates **below** the verdict — the unverified-shift ceiling, then `MinimumEngineScore` — and a download the engine never scored is still refused. Overriding the result instead would have kept files the *checks*, plural, had rejected.

! **Turning it off is a deliberate relaxation of the third download invariant** (→ `CLAUDE.md`: *nothing unconfirmed by the video's own audio reaches the library*). It holds under the default and the setting is what trades it for coverage on a library the check cannot measure. Recorded here so the invariant's exception is traceable rather than discovered later as a contradiction. `SyncOrchestrator.DownloadNeedsConfirmation` is the two settings `&&`ed → the download setting can never loosen the sync gate, which is what the description's *no effect unless* clause promises.

**AF1 is the audit finding the new code's costly direction, and it is worth naming the asymmetry.** A source key that is **missed** degrades to whole-provider retirement — today's behaviour, and safe. A source key that is **invented** under-retires a wall the plugin already knows about, and the cost is provider allowance spent on fetches that cannot succeed. → the parse fails toward the whole provider on every doubt: `Name` is excluded ∵ an aggregator sets it from the candidate's filename, the token must be **host-shaped** (dotted, no whitespace) ∵ every source these aggregators name is a host, and the id must confirm it. A dotless source one day would cost granularity, ¬correctness.

**AF2 is the panel invariant on a surface the delta had just created.** The wall is real, the reason shown was not, and *the UI may lag, it may never lie* does not distinguish a wrong count from a wrong cause. The branch is placed **last** in the fall-through so anything the check actually decided about a file it fetched outranks it — a refusal is the more useful reason, and a walled sibling does not get to overwrite it.

**AE5 moves to [A].** Its two halves resolved in opposite directions: the **granularity** half is built (per-source retirement, this pass), and the **scope** half — sweep-scoped, in memory, reset by `FullLibrarySyncTask` — is a decision rather than a defect. A run-scoped wall is what was wanted: a provider that reports a spent allowance is ignored for the rest of the run and asked again on the next one, w/ no clock of its own and nothing persisted. ¬re-flag it as an unfinished 24-hour timer.

**Verified clean over the delta** — security (no new input surface; one bool from an already-elevated endpoint, and `SubtitleSourceKey` does ordinal string work over data the ledger already stores) · races (`ProviderRetirement`'s second table is under the same `Lock`; the log-dedup read-then-write is non-atomic and can duplicate one warning line, which is what it always could) · filesystem, endpoints, process spawning, write scoping (**the delta adds no call in any of these classes**) · dry run (the `DryRunMode` return still precedes `RunAsync`; the per-offer check does no I/O and the skip *prevents* a fetch) · rollback (a walled skip buys nothing, so it adds no ledger row and nothing new to undo) · efficiency (`SubtitleSourceKey.For` runs per offer and allocates a few short strings — measured against a pipeline that reads video audio over a network share, ¬worth a fast path) · comments (linter clean over 183 files, then read by hand; five violations in the new code were fixed, incl. a comment whose trailing semicolon read as commented-out code) · personal data (five patterns in PowerShell over tracked files — 47 drive-letter and 4 UNC hits, all regex literals, documented Tesseract probe paths, or the synthetic `C:\m\Movie (2001)` fixtures; the four untracked files swept by hand, and AF3 was the one thing it caught).

! **`AcquireResult.Abstained` is deleted and the enum now has a gap at 6.** Checked rather than assumed: `AcquireResult` is never persisted — it reaches the store only through `StageFor`, as a `StageOutcome` — so renumbering was unnecessary and leaving the gap keeps every other member's value stable.

---

## 2026-08-21 (forty-fourth pass) — the panel split and the patience setting

Scope: **the full eleven-item checklist over the uncommitted delta** — the `Acquire` stage row rebuilt to count items, the inconclusive refusals split onto a card of their own, `TryAnotherWhenInconclusive` added, and the provider hint taught to report a downloader that is installed but switched off. Version unchanged at 1.6.1.0, nothing committed. Driven by field evidence: the download row read **1108 failed** where the store held **4** failed acquire stages.

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AE1 | `configPage.html` :893, :905 | The new control has **two** dependencies — downloads on, and audio confirmation on — and each was owned by a different function, so whichever ran last won. The load path also called the toggle **before** `#chkAcquire.checked` was assigned, so the control read a checkbox that had no value yet | Low | **[F]** |
| AE2 | `PluginConfiguration` :73 | `TryAnotherWhenInconclusive` is a **default-`true` bool added after the fact** — the one shape that inverts silently if the deserializer ever resets it, turning "keep trying" into "stop at the first non-answer" on every upgraded install. Nothing covered it | Med | **[F]** |
| AE3 | `SyncOutcome` :9 | Dropping the *rejected as inconclusive* clause from `NoVerdictRefusal` orphans **every row already in the store**: matching the current string alone moves each one onto the card that did not cause it, the moment the user upgrades | Med | **[F]** |
| AE4 | `configPage.html` :331 | The *Only sync when the audio checks are conclusive* description already told the user those refusals are *"listed as rejected as inconclusive"* — a category the panel **did not have**. The page had been promising the card for as long as the setting has existed | Low | **[F]** |
| AE5 | `ProviderRetirement` | A provider that answers with a spent allowance is retired **until the next full sweep calls `Reset()`**, ¬for a day. The window is therefore whatever the scan schedule happens to be, it is in memory alone so a server restart clears it, and the event-driven path never resets it at all | Med | **[A]** → AF |
| AE6 | `configPage.html` :861 | A shipped downloader **switched off in a library's fetcher settings vanished from *Current order* in silence.** The *installed but disabled* hint ran only over names the admin typed into the additional-providers box, and a shipped downloader is never typed → the field page read `Current order: subbuzz.` w/ Open Subtitles installed and said nothing, which reads as the plugin failing to detect it | Med | **[F]** |
| AE7 | `SubtitleAcquirer` :225 | **The card the delta added would have sat at zero.** *Rejected as inconclusive* fired on `NoVerdictRefusal` alone — the **single-candidate** message — but under the default the item buys the whole list and ends `Exhausted` carrying the every-candidate-was-refused wording. The setting the card exists to explain is exactly what routes the rows past it | Med | **[F]** |

**AE3 is the upgrade trap, and it is the second time this delta produced one.** A message string is not an identifier, and the moment the panel groups on one it becomes a schema — stored rows carry the wording that was current when they were written, and no migration touches them. `NoVerdictRefusalLegacy` keeps the old text matchable and is `private`, so nothing can start writing it again. ! The general rule: **grouping the panel on a user-facing string means every past wording of that string is now part of the contract.** Prefer a stored enum where a category is expected to last.

**On the panel invariant, deliberately re-read before adding the card.** *`Retired` is the only split permitted across the panel* governs **which rows a surface covers** — cards `!Stale && !Retired`, stage table `!Stale` — and this change moves no row between surfaces. `IsAudioRefusal` still decides rejected-versus-failed exactly once; `IsInconclusiveRefusal` is defined **in terms of** it rather than beside it, so there is no second place for the line to drift to. The three buckets partition cleanly: `Failed` excludes every audio refusal, `Rejected` excludes the inconclusive ones, `Inconclusive` takes them — no row is counted twice and none is dropped.

**AE7 is the delta auditing its own new surface, and the failure mode is the one the panel invariant names.** The card was not wrong about any row it drew — it was **empty of rows it existed to hold**, ∵ the grouping matched one wording of a message that has two. A number that cannot move is the same defect as a number that lies, and the default setting was what pushed every row onto the other wording. The message is now chosen on the tally (`Failures == 0 && Abstentions == Refusals`), and the tally is the last point at which an abstention is still distinguishable from a measured refusal — the fall-through charges them identically by design. → the second AE-pass finding to come from **grouping the panel on a user-facing string**; see AE3.

**AE6 is a silence, ¬a wrong number, and that is why nothing caught it.** Every check on the page validated names the admin had typed; a downloader Jellyfin knows about and the library has switched off is not typed anywhere, so it fell out of `Order(...)` and the page rendered a shorter list w/ no line to explain the gap. ! **`ISubtitleManager.GetSupportedProviders` filters on `SupportedMediaTypes` alone** — it does **¬** exclude disabled fetchers, so the disabled ones are already in hand at this layer; `Survey` marks them and the hint now reports them whether or not they were named. ! The hint wording says *a library*, ¬*this library*: `Survey` reads the fetcher settings of **one sample item** and the plugin may cover several libraries w/ different settings, so naming a specific one would be a claim the data cannot support.

**AE5 is recorded open rather than fixed**, ∵ it is pre-existing behaviour the class documents accurately (*"will not be asked again until the next one"*) and the fix is a feature, ¬a correction: a persisted per-provider wall with its own clock. ! **Resolved in the forty-fifth pass and moved to [A].** The clock was ¬wanted — run-scoped is the intended behaviour — and the granularity half was built instead.

! **The granularity claim first written against AE5 was wrong, and is corrected here rather than deleted.** It said subbuzz's internal sources are invisible at this layer and can only be retired by subbuzz itself. They are not invisible: subbuzz prefixes its **result ids** w/ the source that produced them — `<plugin-guid>_opensubtitles.com`, `…_subsource.net`, set where it rewrites `s.Id` — and stamps a matching `[<source>]` comment prefix, and this plugin **already stores that id** on every `AcquireAttempt`. A per-source wall is therefore reachable from data the ledger holds today. ! It remains a **feature and unbuilt** for a different reason: the id rides on a *result*, so the source is knowable only after the search has been paid for, and the wall arrives as a **thrown exception carrying no id at all** → attributing it to a source means inferring it from the fetch in flight. That inference is the design work AE5 needs. ¬re-assert that the data is unavailable. ! **Built in the forty-fifth pass** — the inference is the fetch in flight, which carries the candidate and therefore its id; see AF1 for the direction the parse has to fail in.

**Verified clean over the delta** — security (no new input surface; the setting is a bool from an already-elevated endpoint, and the grouping does an ordinal string compare) · races (no shared mutable state added; the acquire fall-through reads config and a per-call tally) · filesystem, endpoints, process spawning, write scoping (**the delta adds no call in any of these classes**) · dry run (the `DryRunMode` return still precedes `RunAsync`, so the setting can cause neither a search nor a fetch) · rollback (more ledger entries per item, no change to what is placed or removed) · comments (linter clean over 179 files, then read by hand) · personal data (five patterns in PowerShell over tracked files — 46 drive-letter and 4 UNC hits, all regex literals, the documented Tesseract probe paths, or the synthetic `C:\m\Movie (2001)` fixtures; no untracked files in the tree). ! **The provider hint was re-read specifically for it** — it renders a provider **name** from the installed list into the page, and a Jellyfin fetcher name is a plugin's name, never the admin's or the server's.

! **Efficiency, noted and accepted:** `Rejected` now evaluates `IsAudioRefusal` twice per record, and on a row whose `RefusedByAudio` is null the second pass re-walks `Stages`. Measured against the shape of the panel — which already makes a dozen passes over the same list — this is not worth restructuring for, but it is the reason to prefer a stored category if a third bucket is ever added.

---

## 2026-08-21 (forty-third pass) — the post-1.6.0.0 acquire-waste delta

Scope: **the full eleven-item checklist over the uncommitted delta** — one new source file (`Subtitles/SdhNaming.cs`) and four changed ones, built in response to field evidence that the acquire path was buying subtitles it always discarded. Of 703 ledgered downloads in the record store, **454 were discarded as hearing-impaired and 45 were kept (6.4%)**. ¬a release audit; the version stays at 1.6.0.0 and nothing is committed.

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AD1 | `agentic/tools/acquirecheck/Program.cs` | **Ten real media titles from the maintainer's library** were written into a harness that ships in the public repo, as the fixtures for the new name filter. Caught by eye, ¬by any of the five sweep patterns — a title is not a path, a host or an address | **High** | **[F]** |
| AD2 | `SyncOrchestrator` :118 | **The skip that lost the brake.** Converting the all-SDH cap outcome from `CapReached` to `HearingImpairedOnly` moved the row from `Failed` to `SetAside` → `IsExhausted` requires `Status == Failed`, so the row stopped being parked and bought `MaxDownloadsPerItem` **fresh** candidates on every scan, for ever. The ledger blocks only the ids already tried | **High** | **[F]** |
| AD3 | `configPage.html` :483 · `README.md` :72 · `ARCHITECTURE.md` | Three user-facing strings kept describing a rule that had been reverted at the maintainer's instruction — *"Remove hearing-impaired tags accepts them regardless"*, when `AcquireHearingImpaired` alone decides. The panel invariant is about counts, but a settings description that names a behaviour the build does not have is the same defect | Low | **[F]** |

**AD2 is the one that mattered, and it was introduced by the fix it audits.** The requested change was a *reporting* change — a budget spent entirely on discarded SDH is a skip, ¬an audio failure — and it was correct. What it silently took with it was the only thing bounding what that row could spend over its lifetime. ! **`IsExhausted` is the brake for `Failed` rows and there was no equivalent for `SetAside` ones**, ∵ every other set-aside acquire outcome is free: `NothingOffered` and `AllFiltered` buy nothing, and a `HearingImpairedOnly` reached by **falling through** the whole offer list has ledgered everything on it, so the second scan re-searches at zero cost. The **cap** case is the first that stops early with un-ledgered offers left behind. Field state at the time: **98 rows** in exactly this condition.

`SyncOrchestrator.BudgetSpent` closes it — a `SetAside` row whose `AcquireAttempts` count has reached `MaxDownloadsPerItem` returns **before the search**, ¬merely before the fetch, ∵ a search is a network request under the admin's account. ! **The release is raising the limit, ¬the *Retry failed subtitles* button**, which reopens `Failed` rows alone: the setting that stopped the row is the setting that restarts it, and the ledger keeps the second budget off ids already bought. `orchestratorcheck` gained six cases; the off-by-one mutation (`>` for `>=`) kills exactly one of them.

**On the SDH question itself, two analyses were retracted before they reached the code** and are recorded so they are ¬repeated. ① Discards were correlated to searches by **log adjacency** in a concurrent log, producing a table showing 29 of 30 downloads were for the wrong title. Adjacency is not correlation in an interleaved log; the sound method pairs both facts **inside one record**, which then showed 60 of 200. ② Every one of the 703 ledgered downloads decoded as `IsSdh: false`, read as proof the providers mislabel. It is a **tautology** — an advertised-SDH offer is dropped before any fetch, so a downloaded candidate has the flag false by construction. The surviving evidence is the filename: 50 of the 340 discards that recorded one literally say `SDH`, and of the candidates that got **past** the SDH check to the audio check, ¬one did.

**Verified clean over the delta** — ReDoS (all three `SdhNaming` regexes use bounded `{0,3}` quantifiers and 200 ms timeouts, matching `SdhDetector`'s convention; `info.Name` is provider-controlled, read in exactly one place, never logged and never written to disk) · efficiency (at most three regex evaluations per offer, short-circuited entirely when `AcquireHearingImpaired` is on) · races (`Tally` is a per-call local; compiled `Regex` matching is thread-safe; no shared mutable state added) · filesystem, endpoints, process spawning and write scoping (**the delta adds no call of any kind in these four classes**) · dry run (the `DryRunMode` return still precedes both the acquire branch and the new budget gate) · rollback (a set-aside row with no `OutputPath` has nothing to restore, and `rollbackcheck` already holds the case that its ledger survives) · comments (linter clean over 179 files, then read by hand) · personal data (five patterns in PowerShell over tracked files — 46 drive-letter and 4 UNC hits, every one a regex literal or the documented Tesseract/Jellyfin install path; the four untracked-by-design paths re-confirmed against `.gitignore`; the new untracked source file swept by hand).

! **The sweep found nothing and the violation was real.** AD1 is the standing argument for the checklist's own instruction to *read* what returns and to check by eye for the classes no grep catches. A list of titles disclosing a private collection is on that list, and it went in as test fixtures — the least-suspected file in the change.

---

## 2026-08-21 (forty-second pass) — the download feature, end to end

Scope: **the full eleven-item checklist over the acquisition delta** — nine new source files (`SubtitleAcquirer`, `ProviderRetirement`, `SyncDecision`, `DownloadProviders`, `ISubtitleSource`, `JellyfinSubtitleSource`, `AcquireAttempt`, plus the `acquirecheck` harness) and fifteen changed ones. ¬a release audit; the version is unchanged at 1.5.1.0 and no release has been cut. The feature is `agentic/plans/IDEA-ACQUIRE.md`, built at the descoped scope: a wanted language w/ **nothing at all** in it, kept only where the plugin's own audio check confirms the download.

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AC1 | `agentic/tools/check-comments.mjs` :129 | The **default (no-argument)** mode lints the lines `git diff` reports against `origin/HEAD`. An **untracked** file is in no diff → nine new source files were never scanned, one of which carried a banned rationale word. ! `verify.ps1` passes a path and scans the whole tree, so the **gate** was never blind — the trap is the quick form the working rules recommend | Low | **[F]** |
| AC2 | `JellyfinSubtitleSource.BuildRequest` :114 | `BuildRequest` resolved `GetLibraryOptions(video)` and then called `Enabled(video, config)`, which resolved it **again**, alongside a second `GetSupportedProviders`. `GetLibraryOptions` walks to the item's collection folder → one search per provider paid for it twice | Low | **[F]** |
| AC3 | `SubtitleAcquirer` :209, :257 | A provider whose **search threw** was tallied identically to one that answered nothing: the row read *"Set aside: no subtitle was offered for this language"* and the only trace was a `Debug` line. The panel stated something the plugin never established | Med | **[F]** |
| AC4 | `ProviderRetirement.RetirementReason` :88 | The chain walk followed `InnerException` alone. An `AggregateException` holds several, and a rate-limit or auth failure sitting past the first was missed → the exhausted provider was asked **once per item for the rest of the sweep**, which is the exact failure the class exists to prevent | Low | **[F]** |
| AC5 | `SubtitleAcquirer` :145 | Nothing set `SubtitleTarget.IsHearingImpaired` on an acquire target → w/ `AcquireHearingImpaired` **on** and `RemoveHearingImpairedTags` **off**, an accepted SDH download was written **w/out the `sdh` token**, named as if it were plain dialogue | Low | **[F]** |
| AC6 | `RecordReconciler` :41 | A download the plugin placed **fills the very language that offered it** → discovery stops offering that target the moment the work succeeds, and the row was marked `Stale` on the next scan. The *downloaded* card emptied itself one scan after every purchase, w/ the file sitting in the library | Med | **[F]** |
| AC7 | `RollbackService` :58 | A rollback dropped the row of a target whose candidates were **all refused** — the only record of downloads already spent and rejected. The next sweep would buy every one of them again against the user's account | Low | **[F]** |
| AC8 | `SyncOrchestrator` :474, :732 | `SyncDecisionMaker.Decide` took the engine confidence **by value**, so the produced file was parsed before `RequireAudioConfirmation` was consulted — and discarded unread on the branch that refuses. Shipped code, found while extracting the shared helper (`AQ-Q2`) | Low | **[F]** |

**AC6 is the one that mattered, and it is the invariant's own case.** *The UI may lag. It may never lie.* Every other `Created` row stays live because its target is still offered — the source file it was made from is still there. An acquire row is the first whose target exists **because the work has not happened yet**, so success is what removes it. The fix tests the file, ¬the offer: `Downloaded(record)` holds a row live while its own `OutputPath` is on disk. When the user deletes the download the language is empty again, discovery offers the gap a second time, and the row returns by the ordinary route — which is also how the re-buy happens, exactly once. ! `stalecheck` now walks a download from purchase to deletion and its library leaving scope; the mutation that drops the clause fails the placed-download case.

**AC3 and AC7 are the same shape as AC6 and were found the same way** — by writing the harness cases the plan owed, ¬by reading the checklist. That is worth recording: three of the eight findings came out of `stalecheck`, `rollbackcheck` and `acquirecheck` rather than out of inspection, in a feature whose central component (`SubtitleAcquirer`) is the first new one in a year that a harness **can** construct.

**AC1 has a second half worth keeping.** The violation it hid was in a file the whole-tree gate would have caught before any commit, so nothing could have shipped. What it demonstrates is narrower and still real: **a check whose scope is a diff cannot see a file git has never heard of**, and the working rules recommend that form for a quick pass. Untracked files of a scanned extension are now added as whole-file scans; proved w/ a probe file that the default mode failed and then passed once removed.

### The eleven-item checklist

① **Security** — the one new externally-controlled string that reaches the filesystem is the provider's `Format`, which becomes a scratch **extension**. It is gated by `SyncEngine.Supports`, a four-string whitelist (`.srt .ass .ssa .vtt`) → a traversal attempt cannot survive it, and the scratch path is composed server-side from a GUID. No new shell string, no new spawn, no new deserialization. ! **`ISubtitleManager.DownloadSubtitles` is called nowhere** — grepped; it writes an unmarked sidecar into the media folder and would place bytes the check has never seen. `GetRemoteSubtitles` is the only fetch. ② **Efficiency** — AC2 above; otherwise no `async void`, no `.Wait()`, no `GetAwaiter().GetResult()` on any shipped path, and the gap test costs **zero** extra I/O ∵ `GetMediaStreams` was already called. ③ **Races** — `ProviderRetirement` guards all five members w/ a `Lock`; `JellyfinSubtitleSource`'s warn-once set is guarded; `SubtitleAcquirer` holds no mutable state of its own, and every singleton matches the registrations around it. ④ **Filesystem** — the fetched candidate is written to a tracked scratch path and reaches the library only through `SubtitlePlacer`. ⑤ **Endpoints** — `GET Providers` is the fifth, inheriting the class-level `[Authorize(Policy = Policies.RequiresElevation)]`, taking no input and returning provider names alone. ⑥ **Process spawning** — no new spawn site; a confirmed candidate runs the same `RunEngineAsync` as every other target. ⑦ **Write scoping** — an acquire placement is **structurally** `Created`: `SubtitlePlacer.Place` reaches `Overwrite` only when `Origin == External`, and an acquire target never is → no vault interaction is possible and rollback's delete branch is the only verb. ⑧ **Dry run** — the `DryRunMode` return at `SyncOrchestrator` :121 sits **ahead** of the acquire branch at :268, so no search and no fetch is reachable; the preflight below it touches `GetItemById`, `GetSupportedProviders` and `GetLibraryOptions`, all local. ⑨ **Rollback** — AC7 above; the delete path is unchanged and still requires the marker suffix. ⑩ **Comments** — AC1 above; linter clean over 178 files and the new ones read by hand. ⑪ **Personal data** — the five patterns over tracked files: 43 drive-letter, 4 UNC, 0 for the rest, every hit in the known-benign classes (regex literals, Tesseract/Jellyfin probe paths, synthetic `C:\m\Movie (2001)` fixtures). The nine **untracked** new files were swept by hand for the same reason AC1 exists — 2 hits, both the synthetic fixture path. `acquirecheck`'s SRT fixtures are written phrases, ¬third-party subtitle text; `git add -An` over the new harness offers exactly `Program.cs` and `acquirecheck.csproj`.

**Checked rather than assumed:** `SubtitleSearchRequest` is built by hand ∵ the `Video` overload ignores `DisabledSubtitleFetchers` and `SubtitleFetcherOrder` — verified by reading the overload, ¬by trusting the write-up · our per-provider exclusions are **unioned** w/ the admin's rather than replacing them · search results are ¬filtered on `ProviderName`, which would drop every `subbuzz` answer ∵ it reports its internal source there · `Survey`'s `GetItemList(Limit = 1, Recursive = true)` is a SQLite query, ¬a filesystem walk, so it does not violate the slow-share rule · `AdditionalDownloadProviders` is a `string[]`, ¬a `List<T>`, so the `XmlSerializer` append trap is avoided · `Normalize` clamps `MaxDownloadsPerItem` to a floor of zero and de-duplicates the provider list case-insensitively while preserving order · the config page parses (`node --check` on the script body; tag balance walked).

**Considered and accepted, ¬defects:** the ledger is **never pruned**, so a title run w/ `MaxDownloadsPerItem = 0` against a generous provider could accumulate a long attempt list — bounded in practice by the account allowance, which stops the sweep long before the store notices · `SearchAsync` reads `Plugin.Instance?.Configuration` rather than the config the acquirer already holds, so a save mid-sweep could change the exclusion list between planning and searching; the effect is one search against a newer setting, which is what the user just asked for · a provider exception logged w/ `LogWarning(ex, …)` carries whatever that plugin put in its message, which could in principle include a credentialled URL — Jellyfin's own subtitle manager logs the same exceptions, and suppressing ours would hide the only signal a broken provider gives · placing a download over the plugin's **own** earlier output for the same language is reachable only while Jellyfin's metadata lags behind the file, and `IsStillCurrent`'s `AcquiredOutputSurvives` is what normally prevents reaching the placer at all.

! **The personal-data recipe in `CLAUDE.md` is PowerShell, and transcribing it into bash silently returns zero.** `git grep -E '\\'` errors or matches nothing depending on the form; `[\\]` is the working bash spelling. A sweep that reads as clean ∵ its regex never matched is worse than no sweep — **run it in PowerShell as written**, and treat an all-zero result over this tree as a failed transcription, ¬a pass.

! **The structural risk moved, ¬closed.** `SyncOrchestrator` is still unconstructable from any harness, and the acquire loop's two inverted terminal branches and the stop-the-item rule live in it — `orchestratorcheck` reaches `StageFor` and the gate methods by inspection-by-linking, and `acquirecheck` reaches everything **below** the judge callback. The judge itself, `RunAcquireAsync` and `KeepAsync` are covered by reading alone.

---

## 2026-08-19 (forty-first pass) — pre-release audit for 1.5.1.0

Scope: **the full ten-item checklist**, run as the release audit the fortieth pass explicitly was ¬one. It stands on that pass — same day, all 65 source files — and re-runs every item against the delta since: `74d4e50` (config page wording, the closing-tag repair, `StillOurOutput`'s comment + `private` → `internal`) and `b54458c` (the AGPL-3.0 relicence, which the fortieth pass excluded). **¬a behaviour change between the two passes.**

| # | Where | Defect | Sev | Status |
| --- | --- | --- | --- | --- |
| AB1 | `README.md` :85–88 | The audio-check section says the second pass runs "when that first reading cannot decide". Since the detector lever it **also** runs on an *aligned* reading that measured no coarse drift — the sentence is true but has stopped being exhaustive, and it is the only user-facing description of when the plugin spends a second decode | Low | **[F]** |

**AB1 was resolved by deletion, ¬by extension** (`c1333d2`). The second-pass paragraph is gone rather than reworded → nothing has to be re-edited the next time the trigger conditions move, which is the more durable shape. ! It leaves **"voice detection" introduced nowhere**: the term now survives only inside two setting labels (`README.md` :59, :89) w/ no text saying it is a second pass over the same audio. Raised, deliberately left — the shorter section was the author's choice.

### The ten-item checklist

① **Security** — zero shell strings: no `UseShellExecute = true`, no `.Arguments =`, no `cmd.exe` or `/bin/sh` anywhere; 44 `ArgumentList` uses and every spawn site sets `UseShellExecute = false`. Deserialization is three `System.Text.Json` calls into concrete types (`AssyResult`, `List<SyncRecord>` ×2), no `TypeNameHandling`, no `BinaryFormatter`. ② **Efficiency** — no `async void`, no `.Wait()`, no `GetAwaiter().GetResult()`. ③ **Races** — `SyncStore` takes `_lock` on all twelve mutating entry points; the constructor and `Dispose` are the two public members without it, and `Dispose` reaches the store only through `Flush()`, which locks. ! The deduplicator's missing target lease stays **accepted at the thirty-sixth pass**. ④ **Filesystem** — 25 destructive call sites, every one a scratch path, a payload directory, the store, the vault, or a media path gated by ⑦ and ⑨. ⑤ **Endpoints** — five, all under the class-level `[Authorize(Policy = Policies.RequiresElevation)]` at :18 w/ no method-level override; the only input is a route-bound `Guid`. ⑥ **Process spawning** — three spawn sites, each w/ a linked timeout and `Kill(entireProcessTree: true)`; the other three `ProcessStartInfo` builders hand off to `FfmpegProcess`. ⑦ **Write scoping** — `SubtitlePlacer.Overwrite` :59 takes the vault copy and :63 returns null on failure, **ahead of** `TryMove` at :66. Re-read, still gating. ⑧ **Dry run** — `SyncOrchestrator` returns at :121; both new U1 exits sit at :312 and :406, downstream. ⑨ **Rollback** — `Delete` refuses at :170 unless `IsPluginOutput` holds, before `File.Delete` at :181. ⑩ **Comments** — linter clean over 69 files; the changed comments read by hand.

**Checked rather than assumed:** the config page parses — tag balance verified by walking the document w/ script bodies elided: no unclosed element, no crossed close, file ends `</html>
`. ! This is the check that would have caught the truncation `74d4e50` repaired, and **nothing in `verify.ps1` performs it** → the page is still only as sound as the last person to read it. · `private` → `internal` on `StillOurOutput` widens nothing outside the plugin assembly; there is no `InternalsVisibleTo` and `orchestratorcheck` links the **source**, ¬the DLL. · Hard rule 1 re-run over every shipped `.cs`, `.html`, `.json`, `.md`, `.yaml` and `.csproj`: no pointer to `agentic/` or any unpublished document, and no `agentic/` shorthand (`¬` `∵` `w/` `→`) left in a shipped comment — which is what the one- and two-line diffs across eleven files since 1.5.0.0 were. · The detector lever's new arm fires on `Plan.Count == 4` alone (`Count / 2 >= 2` ∧ `Count < DriftWindows`, and plans are 1, 4 or ≥6) → the extra decode is bounded to mid-length titles, window-scoped rather than whole-file, run through `AssyCliRunner`'s timeout and kill-tree, and taken **once** — the record closes and `IsStillCurrent` holds it closed. · The lever can only *add* a coarse reading to an aligned verdict, never overturn one, so the binding measurement constraint holds: an acceptance still requires a real reading inside the bound, which `verifycheck` proves.

**The fortieth pass's closing risk is now partly retired.** `orchestratorcheck` links the whole plugin as source and reaches `StillOurOutput`, `IsStillCurrent`, `IsExhausted` and the retroactivity hooks — 13 cases, all four guards mutation-proved. ! It reaches the orchestrator's **decisions**, ¬its pipeline: the two skip exits, the stretch guard and `ProcessAsync` itself are still covered by inspection alone, ∵ the class takes fifteen dependencies and cannot be constructed. **The gap is smaller, ¬closed.**

---

## 2026-08-19 (fortieth pass) — the whole codebase, after the panel fix and the detector lever

Scope: **the full ten-item checklist over all 65 source files (10,375 lines)**, run at the user's instruction after U1, U2 and the voice-detection lever landed. ¬a release audit; no release has been cut and the version is unchanged at 1.5.0.0. The licence was changed to AGPL-3.0 in the same working tree and is ¬part of this audit.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| AA1 | `SyncOrchestrator.StillOurOutput` :1140 · `ARCHITECTURE.md` | The comment and the documentation both claimed the fingerprint half proves *the file on disk is this row's own placement, unchanged since*. `RefreshSourceFingerprint` runs **only** on a `Retimed` placement → for a `Created` sidecar `SourceSha256` is the **source's** hash, and the test proves only that the **target** is unchanged | **[F]** |

**AA1 is a documentation defect, ¬a behaviour one, and the distinction is the finding.** Editing a plugin-created side-by-side sidecar leaves the row `Synced` — but `IsStillCurrent` compares that same field, so editing it never reopened the record under any version either. Nothing regressed; the justification written beside the code was simply wider than the code. → both the comment and `ARCHITECTURE.md` now say what is actually proved, and the `Retimed`/`Created` asymmetry is recorded where the next reader meets it. ! Fixing the **code** instead would mean fingerprinting the output for `Created` placements, which is the field `IsStillCurrent` compares against `target.SubtitlePath` → idempotency breaks and every side-by-side target re-syncs on every scan. The asymmetry is load-bearing.

### The ten-item checklist, over all 65 files

① **Security** — no shell string anywhere: zero occurrences of `UseShellExecute = true`, `.Arguments =`, `cmd.exe` or a shell invocation; every child process builds through `ArgumentList`. Deserialization is `System.Text.Json` into three concrete types (`AssyResult`, `List<SyncRecord>`) w/ no `TypeNameHandling` and no polymorphic binder. No endpoint takes a path. ② **Efficiency** — no `async void`, no `.Wait()`, no `.Result` on a `Task`, no `GetAwaiter().GetResult()` on any shipped path; the `.Result` hits are a property of that name on `AssyInvocation`. The three `Task.Run` sites are deliberate background dispatch and each wraps its body in `try`/`catch`. ③ **Races** — `SyncStore` takes `_lock` on all twelve mutating entry points; `SubtitlePlacer` serialises placement on `_gate`. ! The deduplicator's missing target lease is **accepted at the thirty-sixth pass** and ¬re-flagged. ④ **Filesystem** — 27 destructive call sites reviewed; every one is a scratch path, a payload directory, the plugin's own store, the vault, or a media path gated by ⑦ and ⑨. ⑤ **Endpoints** — five, all under a class-level `[Authorize(Policy = Policies.RequiresElevation)]`; the only input is a route-bound `Guid` resolved server-side through `GetItemById`. ⑥ **Process spawning** — three spawn sites (`AssyCliRunner`, `SeConvRunner`, `FfmpegProcess`), each w/ a linked timeout token and `Kill(entireProcessTree: true)`; fan-out is bounded by `SyncQueue`. ⑦ **Write scoping** — `SubtitlePlacer.Overwrite` returns null when `_vault.Store` returns null, **before** `TryMove` → the vault copy gates the destructive step rather than preceding it, re-confirmed by reading. ⑧ **Dry run** — `SyncOrchestrator` :121 returns ahead of the pipeline and `SubtitleDeduplicator` gates at :65 and :88; every write added since the last audit sits downstream of :121. ⑨ **Rollback** — `Delete` requires the record **and** `SubtitleNaming.IsPluginOutput`; the renamed copy is removed before the vault restores over the old name. ⑩ **Comments** — linter clean over 69 files, and read by hand, which is what produced AA1.

**Checked rather than assumed:** `RefreshSourceFingerprint` has exactly one call site and it is inside the `Retimed` branch · the five endpoints inherit the class-level policy w/ no method-level override loosening it · `IsWithin(scratchDir, …)` still contains the engine's declared output path before it is trusted · the U1 exits are both downstream of the dry-run return · `verify.ps1` green: build 0 warnings 0 errors, comment lint clean over 69 files, 15/15 harnesses, payload lock and generated manifest agree.

! **Unchanged and still the largest gap: `SyncOrchestrator` is unlinkable from every harness.** U1, U2 and AA1 all live in it. Four of the last five findings have landed in the one file no harness can reach, and no pass has yet proposed a way to reach it. **This is the outstanding structural risk in the plugin and it is ¬shrinking.**

---

## 2026-08-19 (thirty-ninth pass) — the synced library that reported itself skipped

Scope: the two "nothing to do" exits in `SyncOrchestrator` and the retroactivity hooks behind them, plus the voice-detection lever recorded at the end. Run off a user question — *why did the synced count go down after a full sync on 1.5.0.0?* — which turned out to have two answers, one of them a live cost defect. ¬a release audit. Two shipped files.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| U1 | `SyncOrchestrator` :309, :404 | Both exits that close a target w/out writing stamped `Skipped` regardless of who wrote the file being read. A correctly synced subtitle is exactly one the pre-check leaves alone and the engine cannot improve → the first scan after the `check2` bump migrated the plugin's whole synced library onto the *skipped* card | **[F]** |
| U2 | `MinimumWouldNowSync` :1133 | `(SkippedMovementMs ?? AppliedOffsetMs)` read a **successful** sync's offset as a **skipped** movement on any row demoted by U1. Every such row failed `IsStillCurrent`, re-decoded four windows off the share, landed in the same state, and did it again on the next scan — for ever | **[F]** |

**U1 is the status panel invariant, met from the other side.** The cards describe *the library as it is now*; as it is now those subtitles are synced, by this plugin, w/ a vault copy behind each. Reporting them as *skipped* is ¬false of the **run** — the run did skip them — which is exactly how it survived review: `Skipped: already aligned` is a true sentence about a scan and a wrong one about a library. The same shape as `FAILED` disagreeing w/ *failed* (K10). → `StillOurOutput` decides it: a non-null `BackupPath` or an explicit `Created` provenance, **and** `FingerprintMatches`. ! `Provenance == Retimed` is ¬usable as evidence — `Retimed` is `0`, the default on a row never placed, so that test is true of every record ever created.

**U2 is what made U1 expensive rather than merely wrong.** The fallback exists for records predating `SkippedMovementMs`; the field it falls back to is written by a different exit and means a different thing. ! The hook can ¬fire legitimately at all: `MinimumMovementMs` is a `const`, so nothing can lower it. Kept ∵ the const may become a setting; the fallback is ¬. Measured on the user's own library: every row U1 demoted was paying a full four-window ffmpeg pass over an SMB share on every scan, permanently, for no state change.

! **Fixing U2 alone would have sealed U1 in.** Once a demoted row reads `Skipped` w/ a matching stamp and fingerprint, `IsStillCurrent` short-circuits it for ever — and U2's churn was the *only* thing still reopening those rows. Fix the churn, fix the exits, ship both, and nothing would ever have re-run to restore a single record. Retroactivity needed its own lever, chosen deliberately: `CheckRevision` → `check3`.

**The bump's second effect was put to the user rather than absorbed.** `check3` reopens every `Failed` row too, ¬only the demoted ones → the coarse-drift release recorded at the thirty-eighth pass as shipping *w/ no retroactivity lever* now has one, and the first full scan retries the ≈356 stored audio refusals without *Retry failed subtitles* being pressed. That reverses a decision the user made earlier the same day, so it was raised as a choice against a no-bump alternative (a `MeasurementVersion` migration promoting the rows at load). **The user chose to keep the bump**, on the grounds that a re-scan re-verifies each file rather than restoring a status from stored evidence alone.

### The checklist, over the two shipped files

① **Security** — no endpoint, path, deserialization or process change; `StillOurOutput` is a pure predicate over fields already read. ② **Efficiency** — U2 is a strict removal of work; U1 adds one `FingerprintMatches` per skip exit, which `IsStillCurrent` already paid earlier in the same pipeline for the same target. ! The `check3` bump is a full library pass, once, and is the point. ③ **Races** — both fixes are per-record inside the target's own lease; nothing new is shared. ④ **Filesystem** — no read, write or delete site added. A row that stays `Synced` keeps the `OutputPath` and `BackupPath` it already had, so no vault copy changes hands. ⑤ **Endpoints** — none added. ⑥ **Process spawning** — untouched. ⑦ **Write scoping** — untouched; neither exit writes, and both still `TryDelete` the discarded produced file before returning. ⑧ **Dry run** — `DryRunMode` returns at :121, ahead of both exits. ⑨ **Rollback** — strictly safer: the fix keeps **more** rows carrying a live `BackupPath` on the cards, and `RollbackService.GetAll` is unfiltered either way. ! A row demoted by U1 was still restorable — the pointer survived the demotion, which is the only reason this was a reporting defect and ¬a data-loss one. ⑩ **Comments** — linter clean over 69 files, and read by hand.

**Checked rather than assumed:** the incoming `record.Status` is intact at both exits — the only assignments ahead of them are in `catch` blocks and the `Unsupported` branch · `SafeUpsert` re-stamps `SettingsStamp` and `MeasurementVersion` on the confirmed path, so a restored row does ¬re-run next scan · `ToleranceWouldNowSync` and `NothingToDo` both require `Status == Skipped`, so a stale `AlignedAtMs` on a confirmed row is inert — it is cleared regardless, matching the precedent already at :374 · the `RequiresOcr` sub-branch still places its converted file, and `ours` is computed from the incoming record **before** that placement · no harness or script pins the revision string, checked by grep for `check2` across `agentic/tools/` · build 0 warnings 0 errors, comment lint clean, 15/15 harnesses, payload lock and generated manifest agree.

! **Neither fix is covered by a harness.** `SyncOrchestrator` is unlinkable from any of them (recorded at the thirty-sixth pass), and both fixes live in it. They rest on inspection and on the argument above, as `Adopt` did at E1 and `StampStage` did at Q1. **This is the third finding in four passes to land in the one file no harness can reach**, and it is now the largest untested surface in the plugin.

### Also this pass — the detector as a coarse reading, measured before it was written

Two levers were tested against the thirty-eighth pass's release condition, on 187 real titles decoded once and scored against a build of `HEAD` and the changed one. **Six windows forced onto a short plan was measured and rejected:** +2 releases at 0 ms and **2 false accepts** at ±800, ∵ three windows a side is a 0.60 baseline and the 500 ms drift bound then tolerates 833 ms end to end. Its best-tuned variant (bound 350) reaches zero leaks w/ a **50 ms** margin against the coarse path's 200 — ¬a place to put a constant under *must ¬write a badly synced sub, period*. Recorded so it is ¬re-proposed.

**The second lever shipped:** `ScoreAsync` now consults the detector on an `Aligned` reading whose coarse fit returned nothing. **20 → 23** releases over 141 four-window titles, **0** false accepts at every injected level, smallest injected |coarse| **425 ms** against the 300 bound. ! The shipped figure was **20**, ¬the 19 reported at the thirty-eighth pass — that sweep scored through static `Score`, which never reaches the second pass, so it understated shipped recall by one. The correction is the measurement's, ¬a behaviour change.

! **The four trigger guards are mutation-proven and one mutation found a real gap.** Dropping `first.CoarseDriftMs is null`, `Plan.Count < DriftWindows` and `Plan.Count / 2 >= 2` each fails exactly its own case. Dropping the **`Aligned`** requirement failed **nothing** — the pre-existing *a refused subtitle is never handed to the detector* uses a 16-window plan, which the coarse trigger cannot reach, so the case that matters most was untested. Under the mutation a `Misaligned` title was handed to the detector and came back **`Aligned`**. → *a refused four-window title is never handed to the detector* was added, and the mutation now fails exactly it. ⓐ The `Verdict: Aligned` test on the *second* result is accepted as defensive: `Score` computes no coarse fit on a non-`Aligned` verdict (T1), so nothing can distinguish it while that holds.

---

## 2026-08-19 (thirty-eighth pass) — the coarse drift release, at the stretch guard

Scope: the whole `IDEA-SHORT-DRIFT` change set — `CoarseDriftWithinMs`, `CoarseDriftMs` on `VerificationResult`, the `Score` split, `SyncVerifier.ReleasedByCoarseDrift`, and the release path added to `SyncOrchestrator`'s stretch guard. Two shipped files. ¬a release audit; no release has been cut, and the user asked for the pass at the point the work finished. The change converts a class of **refusals into writes**, so ⑦ is where the attention went.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| T1 | `SyncVerifier.Score` :262 | The coarse fit was computed for **every** short-plan title, ahead of the verdict — so `Inconclusive` and `Misaligned` titles paid two extra half-width sweeps for a value nothing can read, and the pre-sync call paid it on every short title in the library. `Drift` runs two `Fit` sweeps over the same cue list → nearly as expensive as the whole-track fit | **[F]** |

**T1 is a cost defect, ¬a correctness one, and it was measured rather than reasoned about.** 935 `Score` calls over 187 real titles: **37.0 s** on the shipping code, **64–76 s** w/ the eager computation, **39.8 s** w/ it moved. → the eager form roughly **doubled** the cost of scoring a short title; the fix leaves ≈8%. On the sweep population 111 of 141 four-window titles reach a verdict that can never read the value. The fit now runs at the one site that carries it — inside the returned `VerificationResult`, gated on `Aligned` **and** `Plan.Count < DriftWindows`. ! That also makes *carried, never judged* structural rather than asserted: the value is constructed after every branch that could act on it, so it cannot reach one. Proven not to change behaviour: every release decision across all 935 rows identical to the eager form, and the 21 readings no longer taken are all on non-`Aligned` verdicts.

### The measurement, before and after, over 187 real titles

The change is required to move **nothing** that the check already decides. That was tested by holding the audio constant: every title decoded once through the shipping `SampleAsync`, the onsets and cue starts dumped, then scored twice — once by a build of `SyncVerifier.cs` taken from `HEAD`, once by the changed one. A difference is then the code and nothing else.

- **935 rows (187 titles × 5 injected rate errors) identical on every judged field** — verdict, fitted shift, judged drift, hits, floor, onsets, peak. ! Populations: 141 four-window plans, 46 of six or more, drawn from Futurama, Kids Next Door, Simpsons, Drake & Josh, Nathan For You, Mr. Inbetween, Brooklyn Nine-Nine, Community, Chappelle's Show, Mad Men, Mythbusters.
- **`calibrate.ps1` byte-identical** on the fixed five, in both the as-shipped and the +1500 ms displaced run, measured from the vaulted fixtures. Simpsons S01E10 — the four-window title that reads a large coarse drift — is unchanged at `Inconclusive`, which is the live proof that the reading is carried and ¬judged.
- **Structural invariants, 0 violations in 935 rows:** no coarse reading on a plan of six or more · no judged drift on a plan under six · no release on a non-`Aligned` verdict · no release past the bound.
- **Safety.** 30 of the 141 four-window titles are called `Aligned` as they stand; **19 release** (10 fail closed w/ no measurable reading, 1 refused w/ a reading past the bound). Under an injected end-to-end rate error of +800, −800, +1500 or +2500 ms: **0 releases at every level.** The plan's own sweep left one survivor in 40 at +800; this population leaves none.
- **The bound is at the knee, reproduced independently.** Releases on correct files at ≤200/250/300/350/400/500 = 15/16/**19**/19/19/19, and 500 admits **one** false accept at −800 that 300 refuses. 300 is full recall w/ zero leaks — the same answer the investigation reached on a different, smaller set.

### The ten-item checklist, over the two shipped files

① **Security** — no endpoint, no client-supplied path, no deserialization, no process spawn, no new path handling. The addition is an `int?` on a record struct and a pure predicate over it; `ReleasedByCoarseDrift` is reachable only from the guard. Class-level `[Authorize]` untouched. ② **Efficiency** — T1, above; no extra audio decode at any point, ∵ the fit runs over onsets already read. ③ **Races** — `Score` and the predicate are static and pure, `VerificationResult` is a readonly record struct, `SyncVerifier` gains no state. ④ **Filesystem** — no read, write or delete site added; a released result reaches the existing `TransformAsync` → `SubtitlePlacer` path. ⑤ **Endpoints** — none added. ⑥ **Process spawning** — untouched. ⑦ **Write scoping** — the change turns refusals into writes, so the count of writes rises; the **mechanism** does not move. Every one goes through `SubtitlePlacer.Place`, which still pays vault → gate → move → record, and the vault copy still *gates* the overwrite rather than merely preceding it. No fourth destructive path. ⑧ **Dry run** — `DryRunMode` returns at `SyncOrchestrator` :121, ahead of the pipeline, so the guard is unreachable; and it adds no filesystem call to reach. ⑨ **Rollback** — a released row is an ordinary `Synced` row carrying `BackupPath` and `Provenance` from placement → `RollbackService` restores it exactly as any other. Where it was previously a `Failed` row w/ no output, it is now a row w/ one; nothing is stranded. ⑩ **Comments** — linter clean over 69 files, and read by hand.

**Accepted, deliberate.** ⓐ **Retroactivity ships switched off.** `CheckRevision` was ¬bumped — the rows the guard already refused stay `Failed` and `IsExhausted` keeps parking them until *Retry failed subtitles* reopens them. A bump re-scans the whole library to reach one bucket; the button reopens the refusals alone, and the user chooses when. ! The investigation's own audit recorded `CheckRevision` as *the only lever* for this, which is wrong on the narrow question: `SyncRecord.CurrentMeasurementVersion` reopens `Failed` rows carrying a `RejectedOffsetMs` at **load**, and the config-page button reopens every `Failed` row. Neither was needed. ⓑ **A release can rest on webrtcvad onsets** where the silence pass reached `Inconclusive` and the second pass settled the title. The gates it passes are the sweep's, ¬the detector's; measured w/ the second pass live, every VAD-settled `Aligned` under an injected stretch was still refused. ⓒ The pre-sync `Score` still pays the fit on a short title it calls `Aligned`, and discards it — but such a target skips the engine entirely, so the pipeline saves far more than it spends.

**Checked rather than assumed:** both routes to a verdict reach `Score`, so `VerifyAsync` (which samples internally) carries the reading exactly as `ScoreAsync` does — the guard cannot behave differently depending on which branch ran · `VerifyAsync`'s early `Nothing(0)` carries no reading → fail-closed · `halves` is now set **only** by the judged path, so nothing new reads the out-parameter · releasing is a fall-through, so the `Inconclusive` shift backstop, `RequireAudioConfirmation`, the engine-score gate, the transform and placement are all still reached · `RequireAudioConfirmation` gates only `Inconclusive` and the release fires only on `Aligned`, so no unconfirmed title becomes a write · a four-window plan always reads all four windows, `SampleAsync` returning null below `min(MinimumWindows, count)` → the coarse reading is only ever 2 + 2 · `MaximumRateDrift = 0.30` admits the 4.3% PAL conversions this recovers, so the bound upstream is not the limit · a stale `RejectedOffsetMs` surviving onto a now-`Synced` row is inert — `IsAudioRefusal`, `IsExhausted` and `ToleranceWouldNowAccept` all require `Status == Failed` · `vadcheck` still compiles against the appended record member, checked by building it ∵ `verify.ps1` does not · the two refusal messages and `RejectedOffsetMs` are unchanged for every title still refused, so no panel bucket text moves · build 0 warnings 0 errors, 15/15 harnesses, payload lock and generated manifest agree.

**The nine new `verifycheck` cases are mutation-proven, ¬asserted.** Each mutation was **run**, and each fails exactly its own case: gating the reading on windows *read* rather than *planned* fails only *a six-window plan with four windows read* · letting the reading reach the drift-`Misaligned` branch fails the two cases that assert a verdict is unchanged · widening the bound to `DriftWithinMs` fails only *a rate error the drift bound would have admitted*, at a coarse 375 ms on a title the whole-track fit calls `Aligned` — which is the constant's whole justification, executed · dropping the `Aligned` requirement from the predicate fails only the predicate case.

! **One pre-existing inaccuracy left alone, deliberately:** the `tooShort` refusal message is chosen on `verdict.Windows < DriftWindows`, i.e. windows *read*, so a six-window title w/ two unreadable windows already reports *"too short"* when it is not. Fixing it inside this change would move rows between the two refusal messages and confound the before/after count. ¬a finding of this pass.

---

## 2026-08-18 (thirty-seventh pass) — the voice-detection second pass, before payload 2.0

Scope: the whole `IDEA-VAD` change set — the centred bound, the webrtcvad second pass and the seam that reaches it, `CheckRevision`, the split stretch refusals, and the payload's own new code. **This is the audit for payload 2.0**, run before publishing it and at the user's instruction; it is ¬a plugin release audit, ∵ no plugin release has been cut. ! The payload now contains **code written here** — an entry wrapper and a detector subcommand — which is new attack and resource surface inside the frozen binary and had never been audited.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| R1 | `SyncRecord.cs` :60 | The offset measurement changed — stored offsets went **signed** and the bound became **centred** — and `CurrentMeasurementVersion` stayed at 1, against its own contract. Every stored rejection would keep being reported under a rule the shipping check can no longer reproduce, until a full scan re-processed it | **[F]** |
| R2 | `assy-entry/assy_vad.py` | The `vad` subcommand read a whole window into memory before scoring it. A window is planned over the **cue span**, so a short track late in a long film plans one spanning the hours before it — ≈1.9 MB of PCM per minute of window, in as many payload processes as there are concurrent syncs | **[F]** |
| R3 | `README.md` :59, :92 · `ARCHITECTURE.md` | Both named the setting *"Only sync when the audio check is conclusive"*; the config page reads *"Only sync when the audio checks and voice detection are conclusive"*. Documentation naming a control that is not on screen | **[F]** |

**R1 is the field's own instruction, unfollowed.** `CheckRevision` (D9) does make the change retroactive — every stored record's `OutcomeStamp` differs → `IsStillCurrent` and `IsExhausted` both fail → it re-runs. What it does ¬do is repair the **panel** before that scan happens: the rows still carry `RejectedOffsetMs` and a reason string measured by the 500 ms raw rule. `MeasurementVersion` is the mechanism that clears those at **load**, and the measurement did change under it. → bumped to 2. `SyncStore.Remeasure` then reopens exactly the refusals and leaves `Stale`/`Retired` rows alone, which is the bar `ReopenFailed` already sets. ! The cost is real and intended: the *rejected by audio check* card empties on the first load after the upgrade and those rows read as pending until the next full scan.

**R2 is a resource defect, ¬a correctness one, and the fix had to prove it changed nothing.** The reader now keeps ≈32 s of audio in flight and accumulates only the per-frame flags; ffmpeg's stderr goes to a **temporary file rather than a pipe**, ∵ nothing drains a pipe while stdout is being read and a full one deadlocks the decode. Measured on the rebuilt payload: peak working set **43 MB** on a 90 s window and **44 MB** on a 20-minute one — flat, where the buffered reader added the window's own PCM on top. ! The output is **byte-identical** to the buffered build on both a four-window plan and a single 20-minute window, and the shipping seam reproduces the recorded field reading exactly (The Simpsons S01E10: 94 onsets, `Misaligned +775 ms`, peak 1.35×). A rewrite of the detector's input path that moved a single onset would have invalidated every measurement in `IDEA-VAD`.

**R3 is the status-panel invariant reaching the documentation.** The label was changed on the page after the plan's D10 wording was approved; the README and `ARCHITECTURE.md` were not moved with it. The page is the authority — a user reads the docs to find a checkbox by name — so the docs followed the page, ¬the reverse.

### ! The centred bound cannot reopen a record, and this was checked rather than assumed

`ToleranceWouldNowAccept` now judges `RejectedOffsetMs` with `SyncVerifier.IsAligned`, a **centred** test — and that field holds three different kinds of number: a fitted **position**, a **drift** difference, and a **stretch** difference. A centred rule applied to a difference is exactly what D11's first pre-implementation check forbids, and the failure mode would be a churn loop: the hook releases the record, the run reproduces the same refusal, every scan, for ever. It is unreachable, by construction — the reopen window is `v` in `[−30, 370]`, and every refusal stores a value outside it:

| refusal | stored | refused when | reopens |
| --- | --- | --- | --- |
| out of alignment | `best` | outside `[−30, 370]` | never — the refusal *is* that test |
| drifting | `spread` | magnitude past 500 | never — disjoint from the window |
| rescaled, drift unmeasured | `stretch` | magnitude past 500 | never — disjoint |
| moved too far, no verdict | `shift` | magnitude past 60000 | never — disjoint |

The same holds for rows written by earlier versions, which stored **magnitudes** past 500. → the hook returns false for every value any version can have stored, and nothing reopens through it. ! This is a property of the constants, ¬of the code: a future `DriftWithinMs` below `AlignedWithinMs + TypicalLeadMs` breaks it, and the reopen would be silent.

### The ten-item checklist, over the change set

① **Security** — no endpoint, no client-supplied path, no new deserialization of anything but the payload's own stdout, which is read through `JsonDocument` inside a `try` and yields only integers; a malformed or hostile reading returns null and the first verdict stands. The `vad` argv is built with `ArgumentList` and the payload spawns ffmpeg with a list, never a shell string. `--ffmpeg` is `IMediaEncoder.EncoderPath`, server-derived; there is still **no setting that names an executable**. ② **Efficiency** — R2; and the second pass decodes the same windows a second time, accepted below. ③ **Races** — `SyncVerifier` is a singleton whose only new state is an immutable dependency; `ScoreAsync` shares nothing. ④ **Filesystem** — the second pass writes nothing and reads only the video. ⑤ **Endpoints** — none added; the class-level `[Authorize]` is untouched. ⑥ **Process spawning** — `VadAsync` reuses `AssyCliRunner.RunAsync`, so it inherits the allowlisted environment, the one-thread BLAS pins, the per-sync timeout and `Kill(entireProcessTree: true)`; a timeout returns "no reading" and an external cancel still rethrows. The whole pipeline holds a `SyncQueue` permit, so the fan-out is bounded by `MaxConcurrentSyncs` — which is what makes R2's per-process figure the one that matters. ⑦ **Write scoping** — untouched; no new write or delete site anywhere in the delta. ⑧ **Dry run** — the second pass is **post-sync**, and `DryRunMode` returns ahead of the pipeline; unreachable, checked at the call site. ⑨ **Rollback** — untouched. R1's reopen sets rows to `Pending` and clears no `BackupPath`, so no vault copy can be stranded. ⑩ **Comments** — linter clean over 69 files, and read by hand.

**Accepted, deliberate.** ⓐ The second pass pays a **second decode of the same windows**, and only on titles the first pass could not measure. Sampling is already 11–25% of a feature; the alternative is caching PCM the plugin has no place to put. ⓑ A partial read is scored: if the detector returns onsets from fewer windows than were planned, the fit runs on what came back, exactly as the silence pass already does with `used`. The gates — `MinimumHits`, the share floor, `PeakRatio`, `RivalRatio` — are the sweep's, ¬the detector's, and apply unchanged. ⓒ `LOCAL = ("vad",)` in the entry wrapper would shadow an upstream subcommand of that name; upstream has none, and the pass-through is smoke-tested on every build.

**Payload releases, and the one thing that must never happen to them.** The published payload was retitled **Payload 1.0** w/ its tag, asset names and URLs untouched, ∵ every plugin version already released has `payload-v6.4` compiled into its DLL. **Payload 2.0** is published under `payload-v2.0` and both assets were downloaded back and hashed against `PayloadManifest.g.cs` before this was written. ! **Neither release may ever be deleted, and no asset any released plugin pins may be renamed.** The 2.0 archives *were* renamed after publishing — `assy-cli-2.0-<rid>.zip` → `assy-cli-6.4-<rid>.zip`, so the filename names the tool inside rather than the payload revision — which was safe only ∵ no plugin release pins them yet; both were re-downloaded and re-hashed against `PayloadManifest.g.cs` afterwards. A withdrawn payload release, or a renamed asset under a shipped plugin, is a plugin that installs and can never sync anything — the same failure `-ReleaseMode`'s `sourceUrl` check exists to catch for the plugin's own zips, one layer down. ! And a payload rebuilt without moving `version` reaches **no** existing server: `PayloadStore` keys the cache on it and never re-hashes an installed payload.

**Checked rather than assumed:** the entry wrapper hands every unclaimed argv to `cli.main()` unmodified, proven by the build's own pass-through smoke test and again by an accidental no-argument run printing upstream's help · the payload's ffmpeg arguments are identical to `vadcheck/vad-onsets.py`'s, so the reference the constants were measured against still describes what ships · `PlanWindows` never emits a zero-length window, so the payload's read-to-EOF branch is unreachable from the plugin · the two new stretch messages still match every bucket matcher in `vadcheck/`, which keys on *rescaled the subtitle across the runtime* · `AssyVadOnsets` refuses a reading without `ok: true`, a non-empty onset array and `windowsRead > 0` · no dependency cycle in the new DI edge (`SyncVerifier` → `ISpeechOnsetSource` → `IAssyCliRunner`) · the five-title calibration set is unchanged on every verdict, shift, hit count and floor after the rebuild · build 0 warnings 0 errors, 15/15 harnesses, payload lock and generated manifest agree.

---

## 2026-08-16 (thirty-sixth pass) — a removal that came back, before 1.4.3.0

Scope: the three commits since 1.4.2.0 — the `Rejected` column + as-of stamp removal, the `SetAside` status, the deduplication age tiebreak — plus the two fixes below. **This is the pre-release audit for 1.4.3.0.** The `SetAside` delta was audited at the thirty-fifth pass and the panel work at the thirty-fourth; what had ¬been audited is the column removal and the tiebreak. Run off a user question — *does the source gone counter count up when a subtitle is deduplicated?* — which it does ¬, on a path that reaches the **synced** card instead.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| Q1 | `RecordReconciler.Reconcile` :43 | The retired branch un-retires on `offered`, which is a **`TargetKey` or path** match. `EmbeddedKey` names a stream inside the video → an extracted sidecar removed as a duplicate has its target offered on **every** scan → the row rejoins the cards as `Synced` w/ no file behind it, permanently. K14's own note says this is unreachable ∵ neither key nor path can match; that holds for `ExternalKey`, which is a path, and ¬for `EmbeddedKey` | **[F]** |
| Q2 | `SubtitleDeduplicator.Group` :140 | A retired row's `OutputPath` is gone ∵ this deduplicator deleted it → `ToCandidate` returns null → **poisons the slot**, silently disabling deduplication for that language on every later scan. K12 one case over, and reachable w/out Q1 | **[F]** |
| Q3 | `AUDIT.md` :249 (D2 note) | Records that the *source gone* card *"was removed after 1.3.0.0"* and that the cards therefore *"no longer sum to `Total`"*. The card was **never removed** and that was ¬why the sum broke | **[F]** |

**Q1 is the K14 fix meeting a key that is not a path.** `Reconcile` reads *the target is offered* as *the file is back*, which is sound only where the key names the file. Two key shapes exist and they differ exactly here: `ExternalKey` is a relative path, `EmbeddedKey` is `emb:<stream>:<codec>` and describes a stream in the container. → the retired branch now tests **`OutputPath` against the offered paths alone**; `offered` still serves the rest of the method unchanged. A restored duplicate is discovered as an external target naming that path, so K14's case passes through the narrower test — proven, ¬assumed, by the K14 case still passing.

! **The embedded case is the common removal, ¬a corner.** `ChooseKeeper` sorts `IsPluginFile` last → the plugin's own extraction is the copy that loses, and every one of those is an embedded target. Reachable w/ `DeduplicateSubtitles` **and** `ProcessEmbeddedWhenExternalExists` on; w/ the latter off the track is set aside and never produces an output to deduplicate.

**The fix needed a second half, or it would have traded one defect for another.** Once the row stays retired, a genuine re-run — `IsStillCurrent` fails on a settings change — writes a live file onto a row no card counts. → `StampStage` clears `Retired` before stamping. ! It sits **after** the `Pending`/`DryRun` early return, ∵ both are provisional: without that, turning dry run on reopens every removal onto the *pending dry run* card. Ordering is what makes it safe — all three call sites run the orchestrator over every target, **then** deduplicate, **then** reconcile, so a removal always lands behind the outcome that cleared the flag.

**Q2 is reachable on its own and outlives Q1's fix.** `Group`'s poison rule is for a candidate whose state cannot be established; a file this deduplicator deleted is the opposite of that, and the record says so. → skip a retired row the way a suppressed one is already skipped. ! The two guards are ¬interchangeable: a suppressed target has no record to read, a retired one has a record naming what happened to its file.

**Q3 is the drift the read-the-docs rule exists to catch, in the audit history itself.** `git log -S"source gone"` returns the single commit that **added** the card, and `configPage.html` renders it today. The claim would have sent a later pass looking for an unreported number, or re-adding a card that is on screen. The sum did break, later and for a different reason — `SetAside`, recorded correctly at the L pass and in `ARCHITECTURE.md`. Struck in place rather than deleted, ∵ the history is what the file is for.

**The ten-item checklist, over the change set.** ① **Security** — no endpoint, client path, deserialization or process change; `SetAside = 6` is the L3 downgrade hazard, accepted there and ¬re-raised. ② **Efficiency** — `CreatedUtc` reads the `FileInfo` already stat'd for `Length`, so the tiebreak costs **no** extra I/O against the SMB share; Q1's fix is a `HashSet` lookup that replaces a `HashSet` lookup; Q2's guard **saves** a `FileInfo` + `SubtitleProfile.Read` per retired row per scan. ③ **Races** — see the accepted item below. ④ **Filesystem** — untouched; ! neither fix adds a `File.Exists`, deliberately, ∵ `Reconcile` runs per item against a slow share and discovery's own path list already carries the evidence. ⑤ **Endpoints** — none added; class-level `[Authorize]` unchanged. ⑥ **Process spawning** — untouched. ⑦ **Write scoping** — untouched; the vault still gates every removal. ⑧ **Dry run** — no media-filesystem call added; the `Retired` clear is behind `StampStage`'s `DryRun` return, checked by reverting the guard. ⑨ **Rollback** — the fix keeps **more** rows retired, `Reconcile` never drops one, and `RollbackService.GetAll` is unfiltered → strictly safer than before; no vault copy can be stranded by it. ⑩ **Comments** — linter clean over 67 files, and read by hand.

**Accepted, ¬introduced here.** `SubtitleDeduplicator` takes no target lease where `ProcessAsync` does, so a full scan deduplicating item X concurrently w/ the event handler processing item X can write a pre-removal clone back over `Remove`'s result — a whole-record lost update that already clobbered `BackupPath`, `Provenance` and the `Deduplicate` stage. `Retired` joins that set and is the least of it. ! **Reopening this means fixing the lost update, ¬the flag** — narrowing it to one field would leave the vault pointer exposed, which is the half that matters.

**Checked rather than assumed:** `LastRecordUpdateUtc` has **no** occurrence left in either half, and `stage.Rejected` none in the page — K10's *delete both halves or neither* holds for both removals · the cards still partition every status but `SetAside`, so the new `Total` comment is accurate · `CreationTimeUtc`'s Linux fallback was already recorded at the tiebreak's own documentation and is ¬a new finding · `ChooseKeeper` is a stable `OrderBy` chain, so a uniformly unavailable creation time falls through to size, the previous behaviour · the K14 harness case passes under the narrowed test, ∵ a restored file is discovered as an external target naming its path · build 0 warnings 0 errors, 15/15 harnesses.

**Both fixes are mutation-proven, ¬asserted.** `stalecheck` gained *an embedded row whose sidecar was removed stays retired* and `dedupecheck` *a retired row does not switch the slot off*; reverting each fix was **run**, and each fails exactly its own case and nothing else. `SyncOrchestrator` remains unlinkable from any harness → the `StampStage` half is covered by inspection and by the ordering argument above, as `Adopt` was at E1.

---

## 2026-08-16 (thirty-fifth pass) — `SetAside`, and what a status costs

Scope: the `SyncStatus.SetAside` delta on top of the thirty-fourth pass — six files, ~14 lines. Run ∵ the user read *"A text subtitle in this language already serves this track"* under the *Unsupported tracks* heading and rejected the categorisation: the plugin **can** process that track and chose ¬to. Unreleased; nothing here has shipped, and the K pass it sits on has ¬shipped either.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| L1 | `SyncOrchestrator.StampStage` :187 · `SyncStore.OutcomeFor` :443 | **Two copies of the status → stage-outcome map**, and the new status had to be added to both. Each has a `_ => Failed` default → a status present in one Skipped arm and missing from the other files a track that never ran a step under `FAILED` on its stage row | **[F]** |
| L2 | `SubtitleDiscoveryService.SuppressCoveredEmbedded` | A covered **embedded text** track is now reportable **only** in the *Synchronization* row's `SKIPPED`, mixed w/ already-in-sync. The OCR case gets *Convert*`/SKIPPED`, which nothing else fills; this one has no such column | **[A]** |
| L3 | `SyncStore.Load` / `SerializerOptions` | A `"SetAside"` in `records.json` is **unreadable by any earlier build**: `JsonStringEnumConverter` throws on an unknown name → `LoadBackup`, whose file is also new → **empty store** → every `BackupPath` pointer lost, and the first `Save` overwrites both copies | **[A]** |

**The change itself.** `SubtitleTarget.SetAside` is set by both suppressors beside the `UnsupportedReason` they already wrote; `ProcessAsync` reads it to pick `SyncStatus.SetAside` over `SyncStatus.Unsupported`. The card and the *Unsupported tracks* list both filter `Unsupported` alone → the rows fall out of each by construction, w/ no second filter to keep in step. ! **`UnsupportedReason` stays the single "this target will ¬run" marker** — `Group`'s K12 guard, the `covered` filter and `TextCovers` all keep testing it and none needed touching. A flag on the target rather than a match on the message, ∵ the message is a display string and K-pass `RetireRemovedDuplicates` is the shape that needs a shared constant to survive.

**L1 is the finding this pass exists for, and the new status is what exposed it.** Both maps agreed before and after; what is wrong is that agreeing was a matter of remembering. → one `SyncOutcome.StageFor(SyncStatus)`, called by `StampStage` and by `Migrate`. ! Neither caller can reach it w/ `Pending` or `DryRun` — `StampStage` returns early on both (K6) and `Migrate` `continue`s on both — so the default arm stays `Failed` and means *a real failure*, ¬*anything unlisted*. `storecheck` gained a case over all five reachable values; **reverting the `SetAside` arm was run and fails it** (`a set-aside track failed`), ¬assumed.

**L2 is the cost of the no-card decision, accepted at the user's explicit choice** of *no card at all* over a *set aside* card. The OCR half is clean: `Convert/SKIPPED` is written by exactly two paths — this suppression and the non-transient *converter not installed for `<platform>`*, and the second is on the *unsupported* card w/ a reason line, so the row is explainable. The embedded half is ¬: `Sync/SKIPPED` already holds already-in-sync and vanished-source skips, and a suppressed embedded track is now indistinguishable inside it. ! **This is the K6 shape one step short of a defect** — the rows *were* skipped, so nothing on screen is false; what is lost is the ability to say how many. Raised, ¬fixed, ∵ every fix is a card or a column and the user ruled both out. ¬re-flag as a defect; re-open it only if the *Synchronization* row's `SKIPPED` is ever read as a single population.

**L3 is a downgrade hazard, ¬a bug in this change, and it is accepted — ! ¬to be re-raised.** `SyncStatus` round-trips through `JsonStringEnumConverter`, which **throws** on a name it does not know, and one such record fails the whole `List<SyncRecord>`, ¬just its own row. `Load`'s `catch (JsonException)` falls to `LoadBackup`; on a downgrade that file was written by the same new build → also throws → empty store, and `Save`'s copy-then-write then destroys the good data file behind it. Reachable only by installing an older plugin version from the catalogue.

**Downgrade is ¬a supported path, at the user's decision**, so the mitigation — a converter mapping an unknown name onto a safe default — was declined rather than deferred. ! **The consequence is a standing property of the store, ¬of this status**: it holds for **every** future enum value, in `SyncStatus`, `SubtitleStageKind`, `StageOutcome` and `SubtitleProvenance` alike, and a later pass finding it again is finding this. What would reopen it is downgrade becoming supported, ¬another enum addition.

**The ten-item checklist, over the change set.** ① **Security** — no endpoint, path, deserialization or process change; the two reasons are compile-time constants reaching the page through the existing `escapeHtml`, and neither is rendered any more in any case. ② **Efficiency** — one `bool` per suppressed candidate; `GetStatus` walks the same records the same number of times. ③ **Races** — `SetAside` is written during discovery on one thread per item and read in `ProcessAsync` under the target lease, before any queue work. ④ **Filesystem** — untouched. ⑤ **Endpoints** — none added; class-level `[Authorize]` unchanged. ⑥ **Process spawning** — untouched. ⑦ **Write scoping** — untouched. ⑧ **Dry run** — the `SetAside` branch sits where the unsupported branch already did, **ahead** of the `DryRunMode` check, and still does nothing but `SafeUpsert`; K's finding ⑧ re-confirmed unchanged. ⑨ **Rollback** — a `SetAside` row writes nothing and backs nothing up, so it carries neither `BackupPath` nor `OutputPath` and `RecordReconciler` may remove it outright, exactly as an `Unsupported` row could; `RollbackService` reads status nowhere. ⑩ **Comments** — linter clean over 67 files, and read by hand; two comments were re-anchored, one off `Waiting` (its claim that the cards sum to `Total` is now false) onto `Total` itself.

**Checked rather than assumed:** the covered-embedded and covered-OCR suppressions are the **only** two writers of `SetAside`, so the status can ¬appear from anywhere else · `SubtitleDeduplicator.ToCandidate`'s `Synced or Skipped` gate excludes it exactly as it excluded `Unsupported` → no slot is poisoned and K12 is untouched · `IsExhausted`/`IsStillCurrent`/`ReopenFailed`/`Remeasure` all gate on `Failed` or `Skipped` and none can see the new value · `GetByStatus` has **no production caller** — interface plus three harness fakes — so nothing queries the store by status · `SubtitleTarget` is never persisted, so the flag adds no schema · the config page names no new field and `statCard` is untouched · BOM + CRLF byte-match `HEAD` on all seven changed files, measured through bash per J7 · build 0 warnings 0 errors, `check-comments` clean over 67 files, 15/15 harnesses.

! **No load-time migration, deliberately.** A row left `Unsupported` by the old code restamps on its next pass ∵ the suppression check is the first thing `ProcessAsync` does, ahead of `IsExhausted` (K13) → the old count is **lag**, which the panel invariant permits, ¬staleness. `RetireRemovedDuplicates` needed a backwards pass ∵ its rows were `Stale` and discovery would never offer them again; these are offered every scan.

---

## 2026-08-16 (thirty-fourth pass) — the work the panel could not report

Scope: the whole status panel, audited for honesty at the user's request off a field screenshot whose *OCR* row read `0 0 0 0` on a library that has image subtitles. `GetStatus`, `SummarizeStages`, `configPage.html`'s cards + stage table, and every writer feeding them — `SyncOrchestrator.StampStage`, `SubtitleDeduplicator`, `RecordReconciler`, `SubtitleDiscoveryService`. Evidenced against three days of server log (`log_20260814-16`). Unreleased; nothing here has shipped.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| K1 | `SubtitleDeduplicator.Remove` :234 | Sets `Stale = true` **before** stamping `Deduplicate/Succeeded`, and `GetStatus` filters `!Stale` → **every** removal is excluded from the panel by construction. Log: **546** removals in 3 days against a `DONE` column that cannot count one | **[F]** |
| K2 | `SubtitleDeduplicator.Canonicalize` :282 | The only `Deduplicate/Succeeded` that survives K1 is the **survivor rename** → `DONE 188` is renames, under a row named *Duplicate removal*. Two operations, one column, and the named one invisible | **[F]** |
| K3 | `SubtitleDiscoveryService` :106, :138 | `DropCoveredEmbedded`/`DropOcrCoveredByText` `RemoveAll` the candidate → no target, no record, counted **nowhere**. ! Turning OCR **on** *removes* tracks from the panel: w/ it off the same track is a visible `Unsupported` row | **[F]** |
| K4 | `SyncOrchestrator` :249 | `ConvertAsync` is entered only when `RequiresOcr`, and no text-track path stamps `Convert/Skipped` → that row's `SKIPPED` is a structurally permanent zero, against *never render a number that cannot move* | **[F]** |
| K5 | `AutoSubSyncController.SummarizeStages` :254 | The `Rejected` split reads record-level `IsAudioRefusal` on **every** row. A stage outlives its run → an old non-`Verify` failure on a since-refused record lands under *rejected by audio check* on a row the check never touched. J8 one level down | **[F]** |
| K6 | `SyncOrchestrator.StampStage` :183 | `DryRun → Skipped` on the default `Sync` kind → while dry run is on, untried subtitles fill the *Synchronization* row's `SKIPPED` beside genuine already-in-sync. Dry run is **on by default** | **[F]** |
| K7 | `configPage.html` :632 | `ms < 1000` tested signed → a −1.2 s median renders `-1200ms` where +1.2 s renders `1.2s` | **[F]** |
| K8 | `AutoSubSyncController.MedianAppliedOffset` | Median over **signed** offsets → an early subtitle cancels a late one, and a library that moved every track can report ≈0, reading as nothing having moved | **[F]** |
| K9 | `AutoSubSyncController.Reasons` :114 | `.Take(8)` truncates silently → the list can total less than the card above it. Same shape as D1/E3, twice fixed | **[F]** |
| K10 | `AutoSubSyncController` :104 | `LastRecordUpdateUtc` computed + serialized, **no reader** in the page → the panel carries no as-of stamp under an invariant reading *the UI may lag* | **[F]**, then **[W]** — the stamp was rendered, then the user withdrew it as noise; field + reader both removed post-1.4.2.0 |
| K11 | `configPage.html` :645 | *not yet run* = `Pending`, which also holds rows parked after a transient failure — those have run | **[F]** |
| K12 | `SubtitleDeduplicator.Group` :134 | A target w/ an `UnsupportedReason` returns `null` from `ToCandidate` → **poisons its slot** → deduplication silently off for that language. Pre-existing for any image track w/ OCR off; K3's fix would have made it the common case | **[F]** |
| K13 | `SyncOrchestrator.ProcessAsync` :97 | `IsExhausted` ran **ahead** of the unsupported check → a suppressed track whose row was left `Failed` on an unchanged fingerprint short-circuits and never restamps. K3's fix would have missed Sandakan, the row that prompted the pass | **[F]** |
| K14 | `RecordReconciler.Reconcile` | A retired row was skipped unconditionally → a duplicate restored **by hand** (¬by rollback, which deletes the row) is discovered, processed, and stays off the cards forever. Introduced by this pass's own `Retired` split | **[F]** |

**K1 + K3 are one root cause: `Stale` was doing two jobs.** It meant both *gone from the library* — correct to drop from the cards, which describe the library as it is now — and *the plugin itself closed this row*, which is work that really happened and does ¬stop having happened. Both erasures are **self-inflicted**: dedupe deletes the file and sets the flag; OCR writes a text sidecar and the next discovery drops the bitmap that sidecar now covers. → new `SyncRecord.Retired`, set by `Remove` in place of `Stale`, skipped by `Reconcile` and by both reopen paths. `GetStatus` now reads **two populations one filter apart** — cards `!Stale && !Retired`, stage table `!Stale`.

! **`Retired` is the only split permitted between those two lists.** Any other reintroduces the D1/E3/J1 failure — a tile disagreeing w/ the list under it. `IsAudioRefusal` still decides rejected-versus-failed **once**, for both.

**K3's fix is a suppression, ¬a drop.** The covered candidate now survives discovery carrying an `UnsupportedReason`, so `ProcessAsync`'s existing unsupported path gives it a record, the existing *unsupported* card, and a line in the existing *Unsupported tracks* list — and `SafeUpsert` routes its stage to `Convert` when `RequiresOcr`, which lands it in the OCR row's `SKIPPED` column and closes K4 in the same move. ! **No new card, column or row was added** — at the user's explicit instruction, and it is the better design: the counters already existed and were being starved.

! **K12 is the trap in that fix and must ¬be re-broken.** Letting suppressed targets reach `SubtitleDeduplicator.Group` poisons the slot ∵ they can never have an output to compare. `Group` now skips any target carrying an `UnsupportedReason` before it computes the slot.

**The log is what settled it, and it overturned a conclusion drawn from source.** I argued from `SummarizeStages` + card arithmetic that **no record had ever entered `ConvertAsync`** — the reasoning was sound about the *pending* case (a parked OCR track stamps `Convert/Failed`, and that column was 0) and the conclusion was still wrong. `OCR read "Sandakan No. 8" … in 370124ms` appears **twice**, Aug 15 and Aug 16, ≈6 min each. The row was OCR'd, refused by the audio check, and then dropped by `DropOcrCoveredByText` at 10:58 on the 16th — `Reconcile` marked it `Stale` and took the `Convert/Succeeded` stage with it. ! **A card reading zero proves a population is empty *now*, never that it was always empty.** The store is a current-state view; only the log carries history. Ask the log before concluding a step never ran.

**The split is applied backwards, at the user's instruction.** `SyncStore.RetireRemovedDuplicates` runs on load and flips every removal the old code marked `Stale` → the 546 return rather than the row restarting from zero. ! It keys on the stage **message**, ¬the kind — `Canonicalize` stamped the same `Deduplicate/Succeeded` on the survivor it renamed, and retiring that row would count a rename as a removal. Constant shared w/ `Remove` so the two cannot drift; idempotent ∵ it clears `Stale`.

! **`IsExhausted` ran ahead of the unsupported check, and that broke retroactivity.** A suppressed track whose row was left `Failed` on an unchanged fingerprint — Sandakan exactly — short-circuits before `ProcessAsync` can restamp it, so K3's fix would never have reached the rows that prompted the pass. The unsupported check now runs **first**: it describes the target as discovery offers it *now*, where exhaustion describes a run that is over. Found only ∵ the user asked whether any of this applies retroactively.

! **`Reconcile` logs nothing when it marks a row stale** — only `Drop` and `MarkOutOfScope` log, and `Stale` appears **0** times in 3 days of server log. The erasure that produced this whole pass leaves no trace. Left as it is for now; the panel now shows the outcome, which is the thing that was actually missing.

**K9 was capped, ¬uncapped, at the user's decision.** `ReasonLimit = 100` on both lists — high enough never to cut a real one (the field store's 331 refusals produce **4** rows), retained as a safety valve for exactly the J3 case where a message stops collapsing into its group and renders one row per subtitle. `UnsupportedReasons` was uncapped and now shares the bound, so the two lists behave alike.

**The ten-item checklist, over the change set.** ① **Security** — no new endpoint, no new path handling, no deserialization change; the suppression reasons are compile-time constants, ¬user data, and reach the page through the existing `escapeHtml`. The as-of line is written w/ `textContent`, ¬`innerHTML`. ② **Efficiency** — `GetStatus` now takes **one** `GetAll` and filters it twice where it took one and filtered once; ¬a second clone. ③ **Races** — `RetireRemovedDuplicates` runs inside `Load`, under the same lock as `Migrate`/`Remeasure`, and sets `_dirty` the way `Remeasure` does. ④ **Filesystem** — untouched; `Canonicalize`'s `File.Move` and `Remove`'s vault-then-delete order are byte-identical, and only the stage stamp moved. ⑤ **Endpoints** — none added; the class-level `[Authorize]` still covers everything. ⑥ **Process spawning** — untouched. ⑦ **Write scoping** — untouched. ⑧ **Dry run** — ! the unsupported branch now sits **ahead** of the `DryRunMode` check; confirmed it performs **no** filesystem work, only a `SafeUpsert`, which dry run explicitly permits. `StampStage` skipping `DryRun` only *reduces* what is written. ⑨ **Rollback** — `RollbackService` reads neither flag, so a retired row is still restorable, and it `RemoveMany`s what it undoes → a rolled-back removal leaves no retired row behind. A retired row always carries a `BackupPath` ∵ the backup gates the delete → `Reconcile`'s drop path could never have stranded one. ⑩ **Comments** — linter clean over 67 files, and read by hand.

! **Every reader of `Stale` was enumerated before the migration was written**, ∵ it flips 546 rows from `Stale` to `Retired` and an unguarded reader would change behaviour for all of them at once. Six sites, all guarded: `ReopenFailedIn`, `Remeasure`, the migration itself, `OnCards`/`OnStageTable`, `Reconcile`, `MarkOutOfScope`. `RollbackService` and `FullLibrarySyncTask` read neither — the first by design, the second ∵ it queues off discovery.

**K13 and K14 are this pass auditing its own fix, and both came from the user asking whether the change applies retroactively.** K13 would have left the three titles that prompted the audit unchanged on the panel. K14 is a hole the `Retired` split *introduced*: `Reconcile` skipped a retired row unconditionally, so nothing could ever bring one back. It now un-retires a row whose file is offered again — the same `offered` test the rest of the method already uses, and unreachable for a genuine removal ∵ neither its `TargetKey` nor its `OutputPath` can match while the file is gone.

**Checked rather than assumed:** the cards sum to `Total` on the field screenshot exactly (`210+1160+331+0+0+0+88+0 = 1789`) and every `SyncStatus` value maps to exactly one card, so that half of the panel was sound before this pass and is untouched by it · `RollbackService` reads neither flag → a retired row is still restorable, and `Remove`'s vault-then-delete order is unchanged · `Canonicalize` keeps its `Upsert` via a new `Save` helper, ∵ dropping the call w/ the stage would strand `RenamedFromPath`/`OutputPath` and rollback would lose where to put the backup · `SyncStore.Migrate` and `OutcomeFor` no longer synthesize a `Sync` stage for a dry-run row, matching K6 · no new endpoint, process spawn, client-supplied path or media-filesystem write · dry run untouched except to stop it stamping a stage it never earned · build 0 warnings, 0 errors.

**Gate green end to end**: build 0 warnings 0 errors · `check-comments` clean over 67 files · all 15 harnesses pass · payload manifest matches the lock. `stalecheck` gained **7** cases and `dedupecheck` a `reportable` assertion driving the real deduplicator, so K1, K13 and K14 each have a harness that fails if the fix is reverted.

---

## 2026-08-16 (thirty-third pass) — what the panel calls a failure

Scope: the wording + categorisation delta on top of 1.4.0.0 — the stage table's `Rejected` column, the reason-line prefix strip, the two miscategorised messages, and the two paths storing raw child stderr as a record message. Unreleased; nothing here has shipped. Driven by the 1.4.0.0 field store, read idle at 1924 records, and by the manual repair of the G1 residue in that store.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| J1 | `AutoSubSyncController.SummarizeStages` | The stage table's `FAILED` column counted **every** `Failed` stage, refusals included — 50 on `Sync` + 369 on `Verify` = 419, the whole *rejected* card, under a heading saying failed. Same population as the cards, opposite split | **[F]** |
| J2 | `SyncOrchestrator` :357, :689 | The rate guard and the OCR-readability gate wrote `Rejected:` messages but stamp a **non-`Verify`** stage → `IsAudioRefusal` is false → they render under *failure reasons* saying "Rejected" | **[F]** |
| J3 | `SyncOrchestrator.RunEngineAsync`, `SeConvRunner` ×2 | Raw child stderr (and one raw `ex.Message`) returned as the stored record message. The panel groups reasons **by message** → an unbounded per-title string is one reason row per title | **[F]** |
| J4 | `ARCHITECTURE.md` :163, :197 | Documented that stderr as what the record carries — the doc described the defect | **[F]** |
| J5 | `ARCHITECTURE.md` :623 | `MinimumEngineScore` documented as **20** w/ the midpoint derivation; the code has been **40** since the Z pass moved it. Pre-existing drift, ¬introduced here | **[F]** |
| J6 | `README.md` :79 | "Files the plugin writes carry an `autosubsync` marker" is false for the **default** write mode — `SubtitlePlacer.Overwrite` writes to `originalPath` w/ no marker; only side-by-side builds a marked name | **[F]** by the user |
| J7 | 5 changed files | BOM + CRLF drift against `HEAD`, introduced by my own edits and then by a mis-measurement of them | **[F]** |
| J8 | `SyncOrchestrator.Adopt` :1017 | An adopted refusal takes `SafeUpsert`'s default `Sync` kind → 50 audio-check rejections counted on the **Synchronization** row. Found after 1.4.1.0 shipped | **[F]** |

**J8 — `Adopt` files an audio-check refusal on the `Sync` row. [F], found after 1.4.1.0 shipped.** The `Rejected` column J1 added was correct on the *Audio check* row and wrong on *Synchronization*, which claimed **50** rejections. Every `Rejected:` call site passes `SubtitleStageKind.Verify`, so the source reads as though this cannot happen — but `Adopt` runs no check of its own and took `SafeUpsert`'s **default** kind. It now picks the kind from `SyncOutcome.IsAudioRefusal` off the record it just filled in. → Synchronization goes to 0 rejected, 0 failed; the 50 join the Audio check row.

! **The reason breakdown is what proves it, and it is the check to repeat.** The identical refusal text appears under *both* stage kinds — `stretched across the runtime` 37 Sync / 235 Verify, `offset drifting` 10 / 23, `out of alignment` 3 / 19, `no verdict` 0 / 92. One reason cannot be two pipeline steps; a reason split across kinds means the **stamp** is wrong, ¬the verdict.

! **Two false trails, recorded so they are not walked again.** ① *"These are legacy rows from before the `Verify` kind existed"* — killed by `UpdatedUtc`: both populations were written in the same 27 minutes, by the same build. ② The whole question is answered by `SettledTwin` in `ARCHITECTURE.md`, and was reached by reading source instead. ! A *what does this mean* question is a design question → read `ARCHITECTURE.md` **first**; the code says what happens, ¬what it is for. Doc drift found in the same pass (J5) is ¬a reason to distrust the narrative — a constant goes stale silently, the prose does not.

**J1 is the G-pass labelling problem, promoted to a defect at the user's decision.** The thirty-second pass recorded "the `FAILED` column on the `Synchronization` row contains zero synchronization failures" as deliberate and ¬to be re-flagged. It was re-raised from the panel itself and fixed rather than accepted: `Rejected` is now its own column, and `FAILED` reads **0** on every row across the whole field store. ! That earlier "do ¬re-flag" line is now **spent** — the split it described is gone.

! **The fix needs the record, ¬the stage.** `IsAudioRefusal` reads `SyncRecord`, and `SummarizeStages` had only a flat stage list → the lookup carries `(Record, Stage)` pairs. A stage outcome alone cannot tell a refusal from a failure and never will; anything that regroups this table must keep the pairing.

**J2 and J3 are the same class: the message is load-bearing.** The panel groups reason lines by exact message string, so a message is a *category key* and ¬free text. J2 put the right word on the wrong category; J3 put a per-title unique string where a category belongs. Both now say `Failed:` w/ a fixed sentence, and the stderr is logged at Warning instead — `_logger.LogWarning("The OCR tool wrote no cues: {Message}", …)` and the engine equivalent. **The prefix is stripped for display only**: `WithoutStatusPrefix` removes `Rejected:`/`Failed:`/`Skipped:`/`Unsupported:` and re-capitalizes, ∵ the heading already names the outcome — the stored message keeps it, and the log lines still read it.

**The field repair, and the trap in it.** 10 rows carried a stale `Verify` stage `Failed` from G1 — `Stale != true`, `Status: Skipped`, `RefusedByAudio: null`. Proven cosmetic first: the only behavioural reader of a stage outcome is `SyncOutcome.InferredRefusal`, unreachable for those rows on two independent grounds (`Status` is ¬`Failed`, and `RefusedByAudio` non-null would short-circuit it anyway). Patched in `records.json` w/ Jellyfin stopped, via a throwaway `System.Text.Json.Nodes` tool matching the store's serializer options behind an `--apply` gate; diff was 96,279 lines both sides w/ exactly 20 changed entries, no date or escaping drift. Verify failures 379 → 369, residue 0, cards unchanged, and the store re-parsed clean across a restart. Backup kept beside it.

! **The G1 residue (10, `Verify`) and the row the user was reading (50, `Sync`) are disjoint sets.** I merged them — "corrected" a count that was not the count being discussed — and set the expectation that patching one would clear the other. It could not, ∵ they are stamped by different call sites: `Fail()` defaults to `kind: Sync`. Anything reasoning about "the failures in the panel" must name **which stage kind**, always.

**J7 is a measurement trap, ¬a content one.** `git cat-file blob $oid > $tmp` in Windows PowerShell **adds a BOM and rewrites LF to CRLF** → every file measured that way reads as `HEAD=BOM` whether or not it is, and "restoring" BOMs off that reading corrupts files that never had one. Measure encoding through bash (`od -An -tx1 -N3`) or by writing the blob to a file first; `head -c 3` on a `cat-file` pipe also SIGPIPEs and returns garbage. All six changed files now byte-match `HEAD` on both counts.

**Clean, w/ what was checked rather than assumed:** no new endpoint, process spawn, client-supplied path or media-filesystem write — the whole delta is message text, one derived column, and a display-time string transform · dry run untouched · `storecheck` still compiles ∵ it consumes only `IsAudioRefusal`/`NothingToDo`, and `IsConfirmationRefusal` was withdrawn before it had a caller · `RequireAudioConfirmation` confirmed read at **exactly one** site, gating **exactly one** message (`SyncOutcome.NoVerdictRefusal`) → a note against the whole *rejected by audio check* category would be false for the rest of it, which is why the wording sits on the checkbox instead · `SubtitleOffsetProbe.Measure` returns `Math.Abs`, so a squish and a stretch are indistinguishable on the record → the refusal message names neither direction · comment linter clean over 146 files · build 0 warnings, 0 errors · no `agentic/` reference in published code.

---

## 2026-08-16 (thirty-second pass) — the status panel invariant, and the two inverted options

Scope: the delta that added `Services/RecordReconciler.cs` + `SyncRecord.Stale`, inverted `SkipEmbeddedWhenExternalExists`/`SkipOcrWhenTextExists` into `ProcessEmbeddedWhenExternalExists`/`RunOcrWhenTextExists`, and the `Verify` stamp on the below-minimum return. Read *The status panel invariant* in `CLAUDE.md` and `ARCHITECTURE.md`'s new `RecordReconciler` section first. **This became the pre-release audit for 1.4.0.0.** ! It covers the **delta only** — the two commits since 1.3.0.0, the second of which is a one-line README fix. Everything beneath it was audited at the thirty-first pass, which is what makes a delta audit sufficient here; a release carrying more than a delta gets the full checklist.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| G1 | `SyncOrchestrator` :363–380 | The below-minimum-movement return fires **before** the post-sync `Verify` stamp, leaving the **pre**-sync check's `Misaligned → Failed` standing. The panel counted 10 records as audio-check failures where nothing failed, nothing was written, and `RefusedByAudio` was null | **[F]** |
| G2 | `PluginConfiguration` :43 | `bool? LegacySkipEmbeddedWhenExternalExists` is stamped `xsi:nil` by `XmlSerializer` once adopted → the retired element is **written back into every saved config forever**, reading as a setting that is still there | **[F]** |
| G3 | `FullLibrarySyncTask` :149 | `MarkOutOfScope` was handed the **pre-scan snapshot**. An item added to an enabled library mid-scan is synced by the event handler, and its live records were then marked stale ∵ the snapshot predates it | **[F]** |
| G4 | `RecordReconciler.MarkOutOfScope` | An **empty** scope marked the entire store stale → an unmounted share or a library still loading blanks the whole panel until the next scan | **[F]** |
| G5 | `AutoSubSyncController` :270 | `Reconcile` was inserted between the `! Closes the item against the refresh` comment and the `_gate.Commit` it describes → the comment named the wrong line | **[F]** |

**G1 is what the field store was measured to find, and it is the invariant's own failure mode.** The store, idle at 1924 records: **418 records `Failed`, every one `RefusedByAudio: true`** — the engine did not fail once across the whole library. Those 418 split disjointly as 50 stamped on `Sync` (raised through `Fail(...)`, which defaults to `kind: Sync`) and 368 on `Verify`. The remaining 10 of the `Verify` row's 378 were G1. ! **The `FAILED` column on the `Synchronization` row contains zero synchronization failures** — that is a labelling problem, deliberately left alone at the user's decision, and is ¬to be re-flagged as a defect. G1 is the separate case where the number itself was wrong.

**G2 is the trap in every `XmlSerializer` rename.** `IsNullable = false` is rejected outright for a `Nullable<T>`; `ShouldSerializeLegacySkipEmbeddedWhenExternalExists() => false` is what suppresses it, ∵ the field is **read-only by construction** — it exists to carry one element forward and is never a value the plugin wants to persist. `configcheck` now asserts the round-tripped file does not contain the string at all, which is the assertion that failed before the fix.

**G3 and G4 are the same mistake twice: treating one scan's view as the whole truth.** The fix re-resolves scope at the end rather than reusing the snapshot, and the empty-scope guard lives in `RecordReconciler` so every caller inherits it. ! `MarkOutOfScope` still runs only on a sweep that reached the end — a cancelled scan has visited an arbitrary prefix, and the `catch` rethrows before reaching it.

**Clean, w/ what was checked rather than assumed:** `RollbackService.GetAll()` is **unfiltered** → rollback still sees every stale row, which is the whole reason they are retained · `Drop` fires only on rows w/ `BackupPath is null && OutputPath is null`, so its `_vault.Discard` can never destroy a live backup — the gate is structural, ¬incidental · no new process spawn, no new endpoint, no client-supplied path, no new write to a media path; `Discard` is vault-scoped and the vault is under the plugin data directory · dry run is unaffected ∵ nothing here writes to the media filesystem, and `CLAUDE.md` already states the record store is written in dry run · `Prune`'s two-signal rule is untouched · `ReopenFailed` skips stale rows, so the retry button cannot re-inflate the count it exists to clear · the `GateStamp` takes the embedded flag **negated**, so an upgraded install's stored stamps stay byte-identical and the rename alone reopens nothing · `Reconcile` matches on `OutputPath` as well as `TargetKey`, ∵ `Canonicalize` renames a survivor without moving its key — `stalecheck` fails that case under a key-only match.

**Accepted by design, ¬to be re-raised:**

- **The effective default for covered embedded tracks changed**, at the user's explicit request: 1.3.0.0 shipped *process them*, this ships *skip them*. An install that enabled embedded processing and **never saved** its configuration therefore changes behaviour on upgrade and has its gate stamps invalidated once. Reachable only w/ `ProcessEmbeddedSubtitles` on, which is off by default. This is a **minor** bump, ¬a patch.
- **A stale row w/ an `OutputPath` whose file the user later deleted is retained forever.** Rollback finds nothing and skips it. The store grows; the panel does not lie. Removing it would need a `File.Exists` per stale row against a slow SMB share on every scan, which buys tidiness at the cost of the thing the share is bad at.

---

## 2026-08-16 (thirty-first pass) — `TesseractLanguage`

Scope: the new `Cli/TesseractLanguage.cs`, its call site in `SeConvRunner`, and the `IsUnreadable` gate removed from `OcrAsync` when Chinese stopped being refused. Read the `LanguageCodes` and `SeConvRunner` sections first, and E7 above.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| F1 | `TesseractLanguage.ChineseScript` :44–65 | **Chinese is the only script subtag read.** `sr-Latn` normalizes to `srp`, which is tessdata's **Cyrillic** model → a Latin Serbian track is read w/ the wrong alphabet and returns plausible-looking nonsense. `az-Cyrl` and `uz-Cyrl` are the mirror case | **[F]** |
| F2 | `TesseractLanguage.Untagged` :14–16 | `und` used to fail loudly (`und.traineddata` missing); it now gets no flag and is read as **English**. A non-Latin `und` bitmap turns a clean failure into a silent bad read | **[A]** |

**F1 was the same defect as E7, one language over.** E7's own record named `srp_latn` in the list of script-split tessdata names and then handled only `chi_sim`/`chi_tra`. ! Serbian Latin is the common case of the three — subtitle releases are tagged `sr-Latn` routinely, where `az-Cyrl`/`uz-Cyrl` are rare.

**Fixed:** `ChineseScript` became `ScriptedName` over a `(code, script) → model` table holding all five — `(zho, hans)`, `(zho, hant)`, `(srp, latn)`, `(aze, cyrl)`, `(uzb, cyrl)`. ! The unsuffixed model is **Cyrillic for Serbian and Latin for the other two**, so the table cannot be generated from a rule and a sixth entry has to be looked up, ¬inferred. `langcheck` is 38 cases, incl. `sr-Cyrl` → `srp` and `aze` → `aze` staying on the unsuffixed name.

**F2 is a judgement, ¬a bug**, and the recommendation is to accept it. An untagged track has always been read as English by the same code path, so `und` now behaves as the thing it declares itself to be; refusing it instead would lose every Latin `und` bitmap, which is what `und` overwhelmingly is. ! What it does expose: `OcrReadability` cannot judge a CJK read at all — no spaced words → under `MinimumWords` → left unjudged — so nothing downstream catches an English model pointed at a Japanese bitmap. That is pre-existing and true of every untagged track.

**Clean, w/ what was checked rather than assumed:** the ISO placeholders all reach `Untagged` through `Normalize`'s three-letter passthrough (`und`, `mis`, `mul`, `zxx` are none of them bibliographic) · `Resolve` allocates a split per OCR call, which runs once per track against a job measured in minutes · `SyncOrchestrator` no longer names `TesseractLanguage` and the build stays at 0 warnings → no orphaned using · the language allowlist is untouched, ∵ `Matches` still goes through `Normalize` and `zh` still matches a `zh-Hant` track.

**Accepted by design, ¬to be re-raised:** an unlisted code — `cmn`, `mol`, a typo — still reaches Tesseract and fails there. That is the blacklist-¬-allowlist decision recorded at E7, and the failure is identical to what shipped before any of this.

---

## 2026-08-16 (thirtieth pass) — the twenty-ninth pass's own delta

Scope: everything the twenty-ninth pass changed, plus C1/C2 — `Models/SyncOutcome.cs`, `SyncRecord.RefusedByAudio`, `SyncStore.ReopenFailedIn`, `GetStatus`, `renderCounts`, `SeConvRunner`'s VobSub flag, `DropOcrCoveredByText`, `OcrReadability`. Read `ARCHITECTURE.md` *Models/* + *Data/* and this file's N3/N6/A4 first, per the rule the last pass added.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| E1 | `SyncOrchestrator.Adopt` :987–1006 | Copies `RejectedOffsetMs` but ¬`RefusedByAudio` → a **fresh** row is written `Failed` w/ a null flag. ! Directly contradicts what the last pass recorded at A4 — *"the flag decides every new row"* — and what `ARCHITECTURE.md` says the null means | **[F]** |
| E2 | `SyncStore.Remeasure` :359–386 | The second reopen path. Clears status, bound + message; leaves `RefusedByAudio` and `Stages` describing the erased run. N3's fix never reached it | **[F]** |
| E3 | `AutoSubSyncController` :94–100 → `configPage.html` :636 | The *failed* card excludes refusals; the **Failure reasons** list under it still groups every `Failed` record → the list totals `Failed + Rejected`. D1's confusion, one panel down | **[F]** |
| E4 | `SeConvRunner` :79, :85 | Two hardcoded `Insert(3, …)` into the argv, both depending on position 3 of a literal initialiser. Reordering it puts a flag between `--outputfilename` and its path — wrong only at runtime, only on VobSub, silently | **[F]** |
| E5 | `storecheck/Program.cs` :159–169 | The new reopen case borrows `records[2]` and wipes it. Runs last today → harmless; any case appended after it reads a fixture that is no longer the fixture | **[F]** |
| E6 | `Models/SyncOutcome.cs` :8–9, :18–19 | Comments state *why* the fallback exists and carry history (*"now says so itself"*). The linter passes both — checklist item 10 is the hand read it cannot do | **[F]** |
| E7 | `SeConvRunner` :83–86 | `--ocr-language` gets ISO 639-2/T, but seconv passes it **verbatim** to Tesseract's `-l` → Chinese asks for `zho.traineddata`, which no tessdata release contains. **Every Chinese image track fails**, whatever the admin installed | **[F]** |

### E7. `zho` is not a Tesseract language **[F]**

Measured, ¬inferred — the twenty-seventh pass left this open as *"unverified"*. A `supsample` PGS was OCR'd w/ `--ocr-language:zho` against a real seconv + Tesseract:

```text
Error opening data file C:\Program Files\Tesseract-OCR/tessdata/zho.traineddata
Failed loading language 'zho'
```

→ **no mapping layer exists**; `--ocr-language` is Tesseract's `-l`. seconv's own `list-ocr-engines` says *"Tesseract: ISO 639-2"*, and `chi_sim`/`chi_tra` appear in its binary — both misleading. Tessdata is ISO 639-2/T **except** where a script splits a language: `chi_sim` `chi_tra` `srp_latn` `uzb_cyrl` `aze_cyrl`, and `kmr` for Kurdish (`kur`).

Of those only two actually break: **Chinese** (`zho` exists nowhere) and **Kurdish** (`kur` → `kmr`). Serbian, Azerbaijani and Uzbek resolve under their unsuffixed names and silently get the Cyrillic model.

! **The OCR path threw away a distinction the naming path preserves.** `ForFilename` keeps `zh-Hans`/`zh-Hant` intact by design; `Normalize` collapsed both to `zho` before OCR ever saw them.

**Fixed as `Cli/TesseractLanguage.cs`.** `Resolve` reads the raw tag before normalizing → `zh-Hant` → `chi_tra`, `zh-Hans` → `chi_sim`; aliases the codes tessdata names differently (`nob`/`nno` → `nor`, `kur` → `kmr`, `tgl` → `fil`, `zho` → `chi_sim`); returns null for the ISO placeholders (`und`, `mis`, `mul`, `zxx`) so they get **no flag** rather than a model that cannot exist. 28 cases in `langcheck`, which links the real file.

- ! **A blacklist, ¬an allowlist.** Listing what tessdata *has* is ≈120 names over an unbounded input domain — a stale entry there **refuses work that would have succeeded**, where a short blacklist only ever leaves today's behaviour in place. The asymmetry is the whole argument.
- ! **A bare `zh` takes Simplified, ¬a refusal.** Considered and dropped: marking it `Unsupported` and skipping. Simplified is the commoner script by a wide margin → refusing every bare `zh` loses far more than guessing costs, and the guess is visible in the output where a skip is only visible in the panel.
- ! **Nothing is skipped for its language any more**, so the `IsUnreadable` gate that briefly existed in `OcrAsync` was removed rather than left with an empty set behind it.

### PGS colour isolation: measured, and it must stay **on** — **[R]**

The other half of C1's open list. Same seconv, same Tesseract, both `supsample` styles, `--no-pgs-isolate-colors` against the default:

| Style | Isolation **on** (default) | Isolation **off** |
| --- | --- | --- |
| Solid | `with a big hole blown through the middle of my life,` | `with 2 big hole dlown through the midele of my lire,` |
| Outline | `When I was lying there in the VA hospital,` | `When \| lying) in the VA hospital,` — *"was"* and *"there"* gone outright |

**Exactly inverted from VobSub**, where isolation on lost 78% of Sandakan's images. → the flag stays VobSub-only. ! A later reader adding the PGS twin *for symmetry* would degrade every Blu-Ray track the plugin reads; this is why `IsVobSub` gates it rather than a general "image subtitle" test.

**All six fixed the same day.** E1 copies the twin's flag verbatim rather than asserting `true` — an adopted row is exactly as old as its twin, and a legacy twin still carries the offset the inference reads. E2 clears the flag **and** the stages, matching `ReopenFailed`. E3 splits into `RefusalReasons` + `FailureReasons` through one `Reasons(...)` helper → the grouping cannot change for one and miss the other; the page renders *Refused by the audio check* above *Failure reasons*, and `reasonBlock` already returns nothing for an empty list. E4 appends the conditional flags and moves `--outputfilename`/path to the end **as a pair**. E5 builds its own record; E2 gained a case of its own → `storecheck` is **19**. E6 cut both comments to the trap alone.

! **`Adopt` is unreachable from any harness** — private, on a class nothing links. E1 is covered by the code path being one line beside the field it belongs w/, ¬by a test. Making `SyncOrchestrator` linkable is a larger change than the finding justifies.

**E1 is what the new read-the-docs rule is for.** Nothing in the code says a fresh row must carry the flag; the claim lives in this file and in `ARCHITECTURE.md`, and only reading them makes `Adopt` a finding. ! It **miscounts nothing today** — `WroteNothing` gates adoption of a `Failed` twin to `RejectedOffsetMs is not null`, so the legacy inference reaches the right answer. The defect is that a new row depends on the legacy path the docs say never decides one; removing that path once legacy rows age out would flip every adopted refusal to *failed*.

**E2 is bounded the same way**: `IsAudioRefusal` gates on `Status == Failed` and `Remeasure` leaves the record `Pending` → invisible. Clearing `Stages` here is safe where the blanket clear rejected at **[A]** above was not — a remeasured record is `Pending` and is therefore guaranteed to run again and re-stamp.

**Clean, w/ what was checked rather than assumed:** the four `Status = Failed` write sites are :146, :1099, :1145 and `Adopt` — the first three set the flag, which is what makes E1 the whole of it · the cards still partition all six `SyncStatus` values → they sum to `Total` (! no longer true after 1.3.0.0 — see the D2 note below) · `ReopenFailedIn` is called only under `_lock`, and `storecheck` passes its own list · `SyncOutcome` is pure, adds no input, path, process or write, and the controller keeps its class-level `RequiresElevation` · a stale `RefusedByAudio = true` surviving onto a `Synced` record is unreachable through the status gate · `DropOcrCoveredByText` removes from the in-memory candidate list only — no container is opened, let alone written.

**¬re-raised:** N6 (the count walk is now nine w/ E3's fix, still accepted), N1, N2, A4, A8, A10, D4, D5, W1–W3.

---

## 2026-08-16 (twenty-ninth pass) — the status panel counted refusals as failures

Scope: `AutoSubSyncController.GetStatus`, `configPage.html`'s cards, and the record fields they read. Run ∵ a field screenshot showed **191 failed** against **1 × Failed** in the reason list — the tile and the list it sits above disagreed by 190.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| D1 | `AutoSubSyncController` :75–76 | The failed/refused split was `RejectedOffsetMs is null`, but the audio check's **no-verdict** path calls `Fail(..., null, Verify)` → 190 audio refusals counted as tool failures. The panel's own comment says a refusal is ¬a tool failure | **[F]** |
| D2 | `AutoSubSyncController` :77 | `Skipped` was one card labelled *already in sync*, and the status also covers "moved less than the 100 ms minimum" and "the subtitle file is no longer on disk" | **[F]** ↓ |
| D3 | `AutoSubSyncController` :71–79 | `SyncStatus.Pending` is in `Total` and on no card → the cards sum to `Total` only while no payload is being fetched | **[F]** |
| D4 | `SyncOrchestrator` :227 | The vanished-source branch cleared `AppliedOffsetMs` and `SkippedMovementMs` but left `AlignedAtMs`, a measurement of a file that is gone | **[F]** |

The field numbers reconcile exactly and are what proved D1: refused 353 = 318 stretch + 21 misaligned + 14 drifting, failed 191 = 190 no-verdict + 1 OCR timeout.

! ~~**D2's *source gone* card was removed after 1.3.0.0, at the user's instruction.** The split itself stands — `NothingToDo` still keeps a vanished source out of *already in sync*, which was the defect — but the vanished ones are now counted and ¬shown, so **the cards no longer sum to `Total`**.~~ **False, corrected at the Q pass → Q3.** The card was never removed: `git log -S"source gone"` returns the **one** commit that added it, and `configPage.html` renders `statCard(status.SourceMissing, 'source gone')` today. The cards summed to `Total` until `SetAside` shipped, which is the only status on no card → `ARCHITECTURE.md`, *`Status` and what it deliberately does not report*. `SourceMissing` is on the `/Status` payload **and** on screen. ! Three cards were added off this pass's findings without asking; adding to the config page now requires an explicit ask → `AGENT-HANDOFF.md`.

**D1's fix is a written fact, ¬an inference.** `SyncRecord.RefusedByAudio` is set on **every** failure as `kind == SubtitleStageKind.Verify` — the Verify stage *is* the audio check, and all six refusing `Fail` sites pass it. ! Written on every failure rather than only the refusals, ∵ a flag set only on refusal would outlive the run that set it. `bool?`: null means a row written before the field, and those alone fall back to reading the stages.

! **Stage outcomes outlive the run that wrote them.** `ProcessAsync` loads the stored record and `RecordStage` overwrites one kind, so a target refused at Verify in one run and failed at Convert in another carries both. That is why the pipeline table's 67 + 486 exceeds the 544 failed records, and why inferring the split from stages is only good enough for legacy rows. **Considered and rejected: clearing `Stages` at the start of a run** — both short-circuit paths return without stamping, so the 1,720 targets that skip on an unchanged fingerprint would be wiped and never re-stamped, collapsing the table to the ~460 that ran that night. The table is honest about *what*, ¬about *when*. → **[A]**

### D5. The docs described a status panel that no longer existed **[F]**

Found by reading `ARCHITECTURE.md` **before** proposing a fix, ¬after. `Status and what it deliberately does not report` recorded that the config page *"deliberately **omits `Sync`**… Two numbers for one thing on one panel, disagreeing by design, is worse than one."* `renderStages` maps every stage the API returns and `stageLabels` names `Sync` → the row had been back for some time and the reversal was never written down. **The confusion that decision existed to prevent is exactly what was reported from the field**: the `Sync` row's 67 against the cards' 191/353.

Resolved by **keeping the row** and recording why the earlier judgement was overturned: the cards count records by final status, the row counts the last outcome of a step, and a per-step failure is visible nowhere else. Two further stale claims in the same paragraph corrected — "summed elapsed" (it is a mean, as `:779` already said) and "one row per step that has actually run" (it walks a fixed description and greys out idle rows). Design document step 29 said "total elapsed" and repeated the omission; corrected to point at `ARCHITECTURE.md` rather than restate it.

! **Read the documentation on a component before concluding about a defect in it.** Three of this pass's five findings were already anticipated somewhere in `agentic/`, and the one above was a documented decision silently reverted — invisible to anyone reasoning from the code alone.

**Clean, w/ what was checked rather than assumed:** `Adopt` not copying `AlignedAtMs` is harmless ∵ `WroteNothing` gates adoption to `Skipped && SkippedMovementMs is not null` → an aligned skip is never adopted and every adopted skip still satisfies `NothingToDo` · `Stages` cannot be null at the `.Any()` ∵ `SyncStore` :321 does `??= new()` on load · `statCard` escapes and coerces (`escapeHtml(value || 0)`) → a cached page against a new DLL shows `0`, ¬`undefined` · the cards remain a partition of both `Failed` and `Skipped` · no new input, path, process or filesystem write, and the controller keeps its class-level `RequiresElevation`.

**Harness: `Models/SyncOutcome.cs`.** The two predicates were extracted out of the controller ∵ nothing could link it — ASP.NET. `storecheck` already links `Models\*.cs` and ships the v1 fixture, so it took no new project. Seven cases, incl. ! a **stale `Verify` stage cannot outvote the flag**, which is the regression the flag exists to prevent. `ReopenFailed` gained a static `ReopenFailedIn(records)` for the same reason `Migrate` has one.

**Three findings already recorded that this pass touched.** Re-noted rather than left to drift:

- **N3 [F]** (twenty-third pass) — `ReopenFailed` leaving stale `Stages` was found and fixed there w/ `Stages?.Clear()`. Clearing `RefusedByAudio` beside it is **that fix extended to a new field**, ¬a new discovery. ! Any field describing a completed run has to be cleared here.
- **N6 [A]** — `GetAll` deep-clones every record per poll and was "walked six times for counts". Now **seven** (`Waiting`), and `IsAudioRefusal` reaches into `Stages` inside two of them. Still accepted on the same grounds — the page polls only while open — but the recorded cost was stale and is now current.
- **A4 [A]** — "nothing gates work on `Stages`" still holds: `SyncOutcome` reads them for display, ¬to gate work. ! A4 also records that an **adopted** record gets no stage of its own and `Migrate` synthesizes a `Sync` one → a legacy row that adopted a no-verdict refusal stays counted as a failure until it runs again. Legacy only; the flag decides every new row.

**Verified:** build 0/0 · `check-comments` clean over 64 files · `verify.ps1` green · 14/14 harnesses.

---

## 2026-08-16 (twenty-eighth pass) — the whole codebase, before 1.3.0.0

Scope: all 61 source files plus `configPage.html`, checklist items 1–10. Run ∵ the twenty-seventh pass predates the whole VobSub subsystem and the Y1/Y2 verifier change.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| B1 | `SyncOrchestrator.SettledTwin` / `CaptureFingerprint` | `SourceSha256` was the hash of the **shared `.sub` payload**, identical for every stream of one index → `SettledTwin` read that as "identical subtitle text" and `Adopt`ed another stream's outcome. One refusal was inherited by all 23 remaining languages w/out OCR, engine or audio check, w/ a fabricated `RejectedOffsetMs` copied across. ! Sandakan's `en` target is refused w/ a bound → **both `zh` streams would have been adopted and never OCR'd**; the Z4 work would have delivered nothing for the title that motivated it | **[F]** |
| B2 | `FingerprintMatches` :1016 · `CaptureFingerprint` :882 | `TryComputeFull` is a whole-file SHA-256 and is reached from `IsExhausted`, `IsStillCurrent` and `CaptureFingerprint` ≈4× per target. Per-stream targets made that **24 × 4 ≈ 11.5 GB read and hashed off the SMB share per scan of one film**, on top of the staging copy | **[F]** |
| B3 | `SyncOrchestrator` :297 | `record.OutputPath ??= target.SubtitlePath` ran for *any* external target incl. one needing OCR → an already-aligned bitmap track dropped its OCR text w/ the scratch and pointed the record at the binary. `SubtitleDeduplicator.ToCandidate` then read no profile from it and **poisoned the whole language slot** | **[F]** |

**B1 + B2 share one fix.** `FileFingerprint.TryComputeSource(path, streamIndex)`: a partial hash suffixed w/ `#<stream>` where the target names one, the full hash where it does not. The suffix separates the streams; the partial read removes the repeated full reads. ! A null hash must stay null — suffixing one yields a fingerprint that matches everything, and `vobsubcheck` holds that case. Both call sites go through the one helper ∵ a fingerprint written one way and compared another never matches.

! **Existing VobSub records re-run once.** Their stored full hash cannot match the new shape → `IsExhausted` and `IsStillCurrent` both return false. Wanted: those rows hold outcomes B1 fabricated.

**B3 fixed by finishing the work, ¬by dropping the pointer.** An aligned OCR target now runs the same tail as a synced one — optional hearing-impaired strip, then `Place` → a marker-named `Created` sidecar. `SubtitlePlacer` already refuses overwrite when `RequiresOcr`, so the `.sub` cannot be written over. ! This writes a file where none was written before, for every already-aligned image track. That is the documented feature (*"Converts image-based subtitles to text"*) working; previously the user got nothing whenever the bitmap happened to be in sync.

**Clean, w/ what was checked rather than assumed:** no shell strings anywhere — every child via `ArgumentList` · zip/tar slip blocked by `ResolveInside`'s prefix check, rooted entries resolving outside and throwing, tar links skipped, hash verified **before** extraction · API carries a class-level `RequiresElevation` and takes only a `Guid` · every `innerHTML` interpolation in `configPage.html` passes `escapeHtml` over `& < > " '`, which matters ∵ `FailureReasons` carries engine stderr and stderr carries media filenames · `LibraryScopeResolver.IsUnder` boundary-checks w/ a per-OS case rule · the vault copy **gates** both `Overwrite` and `Remove` rather than merely preceding them · `RollbackService.Delete` needs marker *and* record · the store writes temp-then-move behind one lock and hands out clones · child processes get a linked-CTS timeout, `Kill(entireProcessTree: true)` and an allowlisted environment · the dry-run gate precedes all filesystem work and VobSub staging is under `TempDirectory`, reachable only from `ConvertAsync` downstream of it.

! `SyncQueue._inFlight` increments only **after** the semaphore, so `RollbackAll`'s and `RetryFailed`'s `InFlight > 0` guards are genuinely TOCTOU — recorded as **N1 [A]**, ¬re-raised. Also left alone as already recorded: N2, A8, A10, D4/N6, D5, W1–W3.

**Verified:** build 0/0 · `check-comments` clean over 63 files · `verify.ps1` green · `vobsubcheck` 20/20.

### C1. Nothing judges whether the OCR text is readable **[F]**

Found by finally executing the OCR link. ! **Tesseract was installed all along** — `C:\Program Files\Tesseract-OCR`, the plugin's *first* probe path, just ¬on `PATH`. Every earlier "unverified ∵ tesseract is not on the harness PATH" note was my own environment, ¬the server's. `check-ocr.ps1` now closes it.

Gravity stream 0, staged by the real stager and read by the pinned seconv:

| | cues | words | mean word | ≤2 chars | ALL-CAPS |
| --- | --- | --- | --- | --- | --- |
| the release's own `.eng.srt` | 744 | 5,185 | **4.55** | 17% | 0.2% |
| our OCR of the same stream | 624 | 15,283 | **2.65** | **53.5%** | **19.2%** |

Ground truth at 00:01:04 is *"Please verify that the P-one ATA removal"*. We produce *"ms os tp one eye ft re ere i ee if tage ffi fe fa an…"*. ! Identical w/ and w/out `--fix-common-errors` (624 cues both ways, 26 `*` placeholders raw) → the pass is ¬inventing this, tesseract is reading noise. The split is ¬the cause: the header, palette and `custom colors: OFF, tridx: 1000` line are byte-identical to the original, and `--time-codes-only` returns the correct 1,003.

**Why it matters, and why Z4 unmasked it.** The **timings are right** — they come from the index, ¬the OCR — so cue 1 lands 385 ms off the reference. Every gate the plugin has judges *timing*: the rate bound, the audio check, the engine score. **All of them pass garbage text with good timings.** `SubtitleContent.HasCues` only asks whether cues exist. Before Z4 this was invisible ∵ Gravity timed out at 20 min and wrote nothing; now a stream completes in **2.8 min** and 24 of them could land in the library.

! **One sample.** VobSub OCR quality was never measured — *Step 22d/22e* used `supsample`, which builds **PGS**. Whether this is this file, this BDSup2Sub++ conversion, or VobSub generally is unknown and is the next thing to measure, ¬to assume.

! **Only `eng` and `osd` tessdata are installed here**, so 23 of Gravity's 24 streams cannot be read at all on this server. Separately: `SeConvRunner` :69 passes ISO 639-2/T, but tessdata uses `chi_sim`/`chi_tra` for Chinese, ¬`zho` → Sandakan's two `zh` streams would ask for a model that cannot exist under that name. Unverified, ¬yet raised as its own finding.

**Root cause: seconv's VobSub colour isolation is ON by default** and binarises off the wrong colour. The "one sample" caveat above is answered — a second, structurally unrelated source reproduces it and is fixed by the same flag:

| | cues | mean word | ≤2 chars |
| --- | --- | --- | --- |
| Gravity, 1920x1080 BDSup2Sub++ — isolation **on** | 624 | 2.65 | 53.5% |
| Gravity — isolation **off** | 962 | **4.97** | 14.4% |
| Sandakan, 720x480 DVD VobSub, different palette — isolation **on** | **187 of 846 images (78% lost)** | 2.97 | 49.2% |
| Sandakan — isolation **off** | 786 | **7.74** | 14.6% |
| reference sidecars | 744 / 846 | 4.55 / 4.34 | 17% / 19.9% |

`SeConvRunner.OcrAsync` now passes `--no-vobsub-isolate-colors`, keyed off `IsVobSub(inputPath, codec)` — `.idx`/`.sub` names a sidecar, `dvd_subtitle`/`dvdsub` names an extracted track. `ISeConvRunner.OcrAsync` carries `codec` for that second case. ! PGS is untouched; `--no-pgs-isolate-colors` exists but no PGS source has been measured to need it.

**A flag is ¬a gate.** `OcrReadability` reads the output back and refuses text nobody could read: mean word length **< 3.5** **and** short-word share **> 35%**, judged only above **200 words**. ! **And**ed, ¬or'd — the worst real reading is 3.93 against a 3.5 floor, close enough that a language of short words trips the mean bound alone; both noise reads fail *both* signals by a wide margin. The floor leaves a CJK track (no spaced Latin words) and a six-caption forced track **unjudged**, ∵ refusing them is the one unrecoverable error here. `ocrcheck` holds all of it against nine real subtitles — real 3.93–4.56, isolation-off 4.52 and 7.39, isolation-on 2.53 and 2.72.

! **The gate is the durable half.** The flag fixes the one cause found; the gate catches the next one. Every other gate the plugin has judges timing, and OCR timings come from the index → none of them can ever see this.

### C2. A bitmap is OCR'd even when readable text already serves its slot **[F]**

`SubtitleDiscoveryService` processed every candidate. Nothing compared a track needing OCR against the text tracks beside it, so a PGS or VobSub was OCR'd, synced and written out while a text track of the same language sat in the same item — minutes of OCR per track for a sidecar the user already had in better form. `SubtitleSourceRank`'s comment claimed "Which source serves a slot when several could. Lower wins," but the rank only set processing **order** at :88; nothing selected.

`DropOcrCoveredByText` now removes a `RequiresOcr` candidate when another candidate serves its slot w/ `RequiresOcr` false and no `UnsupportedReason`.

! **Slot, ¬language — this is the whole finding.** Keying on language alone drops signs-and-songs and hearing-impaired tracks, which carry the language of the full track and are ¬substitutes for it. `SubtitleSlot` is (language, forced, hearing-impaired) and is what makes the rule safe. `DropCoveredEmbedded` **does** key on language alone and its comment says so; that is opt-in behind `SkipEmbeddedWhenExternalExists` and is left as it is. An always-on rule cannot afford the same trap. ! An unlabelled track is also left alone — two of them need not name the same language.

**The slot alone was ¬enough, and two guards were added after auditing it.** Both are cases where the flags the slot reads are known to be wrong:

| Guard | The hole it closes |
| --- | --- |
| a bitmap w/ a **title** is never dropped | `ARCHITECTURE.md` already records that anime ships a full English track and an English signs track **both non-forced and both tagged `eng`** → the slot cannot separate them. A title is the only mark such a track carries. Costs the skip whenever a bitmap is titled "English"; conservative on purpose |
| a **VobSub stream sharing its index** w/ another stream of the same language is never dropped | One Jellyfin `MediaStream` covers a whole `.idx`, so `IsForced`/`IsHearingImpaired`/`Title` are identical for every stream in it and only `Language` is per-stream (:309–311). A DVD index holding a full **and** a forced English stream gives both the same slot. The single-stream case still drops |

Always on, no new setting. It only fires when `ConvertImageSubtitles` is on ∵ nothing else sets `RequiresOcr`. `ExternalWriteMode` was considered as a gate and rejected: it decides where a synced output **lands**, ¬whether a track is processed, and the two are orthogonal.

! **`SkipEmbeddedWhenExternalExists` still runs first**, and it counts an external bitmap as covering an embedded text track. W/ both settings on, an external `.idx` beside an embedded SRT still drops the SRT and OCRs the bitmap. Left alone deliberately — the setting does what its name says, and changing which source wins is a preference, ¬a defect.

! **A dropped track writes no record**, so the config page shows nothing for it — only a Debug line. Same as `DropCoveredEmbedded`, and left consistent w/ it.

! **Accepted:** where the covering text track later fails to sync (audio check inconclusive, engine refusal), the user now gets nothing new for that slot where they previously got an OCR'd sidecar. They still hold the text track itself, unsynced, and after C1 an OCR'd sidecar is the less trustworthy of the two.

`namingcheck` carries nine cases, incl. the forced, hearing-impaired, titled and shared-index survivals.

---

## 2026-08-16 (twenty-seventh pass) — the field logs again: the largest refusal bucket was never the audio check

Scope: `log_20260815.log` **in full** (88,485 lines) and `log_20260816.log` (4,084). ! The twenty-fifth pass read `log_20260815.log` **from line 74,626 only** and drew its population from that tail → it measured the second-largest bucket and reported it as the largest. Reading the whole file inverts the ranking.

| refusal | 08-15 | 08-16 | total |
| --- | --- | --- | --- |
| **stretch guard** — "it stretches the subtitle by N ms … and the audio check never measured drift" | **222** | **35** | **257** |
| audio check inconclusive — "could not confirm it" | 145 | 35 | 180 |
| engine self-score too low (pre-1.2.6.0 wording) | 123 | — | 123 |
| misaligned | 56 | 4 | 60 |

! The stretch guard `SyncOrchestrator` :391 was ¬examined at the twenty-fifth pass at all. The ranking above is the durable result of this pass; the reading first drawn from it is withdrawn below.

| # | Where | Defect | Consequence | Status |
| --- | --- | --- | --- | --- |
| Z1 | `SyncOrchestrator` :391 | *Claimed:* the guard refuses standard framerate conversions ∵ nothing tests the ratio against a real one — 258 of 258 refused titles landed on a textbook conversion | **Withdrawn. The premise was measured wrong twice over** — see below | **[R]** |
| Z2 | `SyncVerifier.Score` :196 | *Claimed:* drift is never measured below a ≈27-min cue span, so the stretch guard is unfalsifiable for episode-length titles | **Duplicate of N5**, twenty-third pass — same mechanism, already closed there. ! N5 attempt 1 (`MinutesPerWindow` 6 → 3) is recorded **[R]**: it broke the one measurable control. Attempt 3 shipped the conditional raise instead, deliberately declining to restore drift below 27 min and bounding the damage w/ the stretch guard. Do ¬re-raise | **[R]** |

### Z1 withdrawn — two independent errors, both mine

**First error: the 258/258 clustering measures ffsubsync's search space, ¬its accuracy.** ffsubsync does ¬estimate a rate freely; it picks from a **fixed candidate list of standard framerate ratios**. So every output it has ever produced lands on a named conversion, including outputs it produced from nothing. Demonstrated w/ `scorecheck`'s own mismatched-pair baseline — the pairing it exists to provide:

| pairing | score / displayed second | reported rate |
| --- | --- | --- |
| Futurama S06E05 video + **its own** subtitle | **92.5** | 1.001 |
| Futurama S06E05 video + S02E03 subtitle | 23.1 | 1.001 |
| Futurama S06E05 video + **a Mythbusters** subtitle | 11.9 | 1.001 |

! A subtitle from a different *show* returns a textbook pulldown ratio. "Lands on a named conversion" therefore carries **zero information about correctness**, and a gate keyed on it would admit the third row as readily as the first. The premise of the proposed fix was empty.

**Second error: the library was being written to and rolled back all day, so its state per title is unknown.** 08-14 → 08-16 hold **1,897 write events over 1,109 distinct sidecars** — 556 written more than once, 22 of them five times. The cause is ¬a defect: **five plugin versions were installed on 08-15** (1.2.2.0 at 09:10, 1.2.3.0 at 12:26, 1.2.4.0 at 14:41, …) and **five rollbacks** ran between them — `1051 + 246 + 161 + 16 = 1,474 restored`, against `2,747 "no backup was taken"`. Each build scanned a library the previous rollback had partly reverted, which is why the same title records the *identical* shift five times (`20,000 Leagues` 5598 ms / 305452 ms ×5; Gravity 860 ms ×2).

! The damage question was checked, ¬assumed: Gravity was written at 09:29 **and** 23:43 w/ the same 860 ms, and the file on disk now reads **`Aligned 200 ms, drift −25 ms, peak 1.49x, 70/48 hits`** → exactly one shift is present. Repeated syncing did ¬compound. But 1,474 restored against 2,747 unbacked leaves the library **mixed**, which is worse for measurement than either extreme ∵ nothing in the log says which state a given title is in.

**This is X4 exactly** — the fixture vault was built one pass earlier ∵ a field scan rewrote a measurement input, and I read live sidecars anyway.

**What the sample actually showed.** Six titles drawn at random and run end to end w/ `check-stretch-outcome.ps1`: one (Disney's Christmas Favourites) came back `Aligned 125 ms, peak 1.33x` after the engine's rate fix, four stayed `Inconclusive` in both directions, and one — Mythbusters s10e09 — was **`Aligned 400 ms, peak 1.31x` *before* the engine touched it**, whereupon the engine proposed a 4% rate change plus 11.6 s at a score of **−63,747**. The guard refused it. Under the standing preference, that single case is worth more than the four unmeasurable ones.

**What already works, and needed no change.** The engine-score gate separates the population the ratio cannot: 92.5 a second honest against 23.1 and 11.9 impossible, w/ `MinimumEngineScore = 40` sitting between them. It caught the same Mythbusters file in the field at 19:25 — "the engine scored its own alignment at **-29.2** a second". ! The stretch guard and the score gate are ¬redundant; they refused the same file for different reasons hours apart.

**Standing consequence.** The field logs **cannot** answer the stretch-guard coverage question at all, ∵ the library has been written to repeatedly since. Answering it needs a run against sidecars in known-original state → the fixture vault, ¬the log. ! Do ¬re-raise "the stretch guard refuses valid framerate conversions" off log evidence alone.

### Z4. A multi-language VobSub is OCR'd in full, and the timeout hides it **[F]**

`Gravity 2013 1080p BluRay multi-subs` fails `Convert` four times across 08-15/08-16 w/ "the OCR tool timed out". Measured from the log timestamps: **exactly 20.0 min** each time → it is hitting `PerSyncTimeoutMinutes` and being killed, ¬crashing.

! **Raising the timeout is the wrong fix and would replace a clean failure w/ a silent bad one.** The `.idx` carries **24 language tracks** (`en, zh×3, ko, ar, bg, hr, cs, et, el, he, hu, id, lv, lt, pl, pt, ro, ru, sr, sl, th, tr`) and `seconv` OCRs **all** of them — confirmed w/ `--time-codes-only`, which prints `Extracting time codes from 21123 VobSub image(s)`. Given more time it would ¬produce an English subtitle; it would produce **21,123 cues of 24 interleaved languages**.

| | images | at Sandakan's measured 6.8 images/s |
| --- | --- | --- |
| as invoked today | 21,123 | ≈52 min → killed at 20 |
| English track only | **1,003** | **≈2.5 min** |

**Neither selector the CLI offers narrows it.** `--ocr-language:eng` sets the tesseract *model*, ¬the track; `--track-number` is documented for MKV extraction. Both were run against this file and both still enumerate 21,123. ! `SeConvRunner` :69 does pass `--ocr-language` when `LanguageCodes.Normalize` succeeds, but this sidecar has no language in its name → it returned null and the flag was never sent. Sending it would ¬have helped.

**What works.** A VobSub `.idx` is a text index of `filepos` offsets into the `.sub`, grouped under `id:` lines. Keeping the header plus one `id:` block and pointing `seconv` at *that* yields **1,003 cues** — a 21× cut, inside the existing budget. Verified end to end except the OCR itself, ∵ tesseract is not on the harness PATH (the plugin supplies it via `ApplyEnvironment`).

! **"Filter to the wanted language" was the wrong shape and is corrected here.** The unit of work must be the **track**, ¬the file, ∵ a blank `LanguageAllowList` is the default and legitimately means *process every language*. Both filter rules already say so and need no change — `Matches` :81 returns true on an empty allowlist, and `PassesLanguageFilter` :304 short-circuits on `Normalize(language) is null` → an unknown language is processed. Per-track work units make those rules apply to the 24 tracks the way they already apply to 24 separate sidecars.

**The discovery layer cannot see the tracks at all.** Jellyfin surfaces this file as **one** subtitle stream — the log records the `.sub` "offered more than once" exactly *once* per scan, which is the `.sub`/`.idx` pair collapsing at `seen.Add(path)` :67, ¬24 languages collapsing. So the language filter never had 24 candidates to choose between: the 24 exist only inside the `.idx`, invisible upstream. **The plugin has to read the `.idx` itself to know they are there.**

→ shape of the fix, ¬yet built:

1. Parse the `.idx` at discovery, enumerate its `id:` blocks, and emit **one target per language track** rather than one per file. Each carries its own `TargetKey` → its own record, output, backup and status row, which is the model `(ItemId, TargetKey)` already uses for 24 separate sidecars.
2. Apply the existing language filter to those tracks. Blank allowlist → all; unknown → processed.
3. OCR each selected track from a scratch `.idx` holding the header plus that one `id:` block, beside the original `.sub`.
4. Timeout per track, ∵ 24 tracks are 24 independent deliverables — a hang on track 7 must ¬discard the nine already read.

! **Point 4 is where Y2 bites.** Per-track fairness multiplied by track count is 24 × 20 min ≈ 8 h on one queue slot, which is the Y2 defect in a new place. ¬the same case as Y2's windows, though — those sixteen produce *one* answer and a partial read is usable, so one budget was right there. Independent deliverables want the opposite. **Decided: each track is its own queue item**, ¬a nested budget.

**Decided: one staged `.sub` copy per source file, shared across its tracks.** ∵ `seconv` resolves the `.sub` by filename beside the `.idx` and offers no flag to point elsewhere, a scratch `.idx` needs a `.sub` next to it. Per-item staging would be 120 MB × 24 ≈ 2.9 GB per scan of one film; one copy is ≈1 s. ! The cache needs a lifetime owner that does ¬exist — scratch is per-invocation today — and it must survive the tracks being separate queue items. Last-track-wins deletion is a race; refcount or sweep on item completion.

! **Sandakan No. 8 delivers nothing today, so there is no regression risk in it.** Both its targets are refused: the pre-existing 846-cue `.eng.srt` (dated 2023, ¬plugin output) and the OCR result alike, each `Rejected: the audio check could not confirm it (16 windows, peak 0.00x)`. **The merged-language OCR output never reaches disk.** Correcting the earlier note in this entry — the OCR succeeds, the *sync* does not, and nothing is being written for this title at all.

**Its streams are `en`, `zh`, `zh` — no Japanese**, against the assumption that a Japanese film carries a Japanese stream. Under a blank allowlist it becomes three targets; under `["eng"]`, one. Both codes normalize, so the unknown-language bypass at `PassesLanguageFilter` :304 does ¬apply here. ! The two `zh` streams are the same-language collision case → `AssignVariants` and `namingcheck` start being exercised for real by a VobSub, ¬only by sidecars.

#### What shipped

`Subtitles/VobSubIndex.cs` reads the `id:` blocks and splits one out verbatim; `Subtitles/VobSubStaging.cs` stages the payload; `SubtitleDiscoveryService.BuildCandidates` emits one candidate per declared stream, keyed by `SubtitleTarget.ExternalStreamKey`; `SyncOrchestrator.ConvertAsync` hands `seconv` the split index instead of the pair; `FullLibrarySyncTask` sweeps at scan start. `VobSubStaging` is a singleton rooted at `IApplicationPaths.TempDirectory` → the lifetime owner point 4 said did ¬exist. Full design → `ARCHITECTURE.md`, *A multi-language VobSub is several tracks wearing one filename*.

Three things were found while wiring it, none of them visible from the log:

1. **The language gate ran before the index was read**, so a specific allowlist dropped the whole pair on Jellyfin's single reported language and found none of the 24. Fixed by gating per declared stream; a single-stream index keeps the old ordering *and* its old `ExternalKey`, so existing store records stay addressable.
2. **`VariantFor` collapsed the two `zh` streams onto one filename** — it prefers `Title`, and every stream of a pair carries the container's one title; with no title it falls through to `StreamIndex` (null for external) and then to the shared filename. Every path gave both streams the same name → the second overwrites the first, which is the exact hazard `AssignVariants` exists to prevent. `VobSubStream` now decides the variant ahead of the title. This is the first input that could reach it; `namingcheck` now carries the case.
3. **A raw NUL byte in `SeenKey`'s separator**, written as a literal instead of an escape → the source file became binary to `grep` and `git diff`, so the line was invisible in review. ! Caught by the user reading the code, ¬by the build, the linter, or any harness. Nothing in the toolchain looks for this.

**Verified:** build 0 warnings / 0 errors · `check-comments` clean over 63 files · `vobsubcheck` 17/17 · `namingcheck` all cases · `verify.ps1` green · real media end to end w/ the plugin's own staged filenames — `source.0.idx` → 1,003 cues, `source.19.idx` → 873, matching the parser's independent counts. ! The OCR step itself is still unverified here ∵ tesseract is not on the harness PATH.

#### Audit of the new code — A1–A6

Run against the checklist immediately after the above, ∵ the twenty-seventh pass predates every file in it.

| # | Where | Defect | Status |
| --- | --- | --- | --- |
| A1 | `VobSubStaging.Link` | Symlink-first fell through to a **full copy** on Windows without `SeCreateSymbolicLinkPrivilege` — the ordinary service-account case → 24 × 120 MB ≈ 2.9 GB, exactly what the shared payload existed to prevent, and silent ∵ the conversion still works | **[F]** hard link first via `CreateHardLinkW`; `vobsubcheck` writes a byte to the payload and reads it back through the pair |
| A2 | `SubtitleDiscoveryService.VariantFor` | Both of Sandakan's `zh` streams got the **same** variant by every path the method has — shared container `Title`, null `StreamIndex`, shared filename → the second output overwrites the first, the exact hazard `AssignVariants` exists to prevent | **[F]** `VobSubStream` decides the variant ahead of the title; three `namingcheck` cases |
| A3 | `SyncOrchestrator.ConvertAsync` → `VobSubStaging.Stage` | A synchronous 120 MB copy **off the SMB share** inside an async method, w/ no `CancellationToken` → a thread-pool thread blocked, and a cancelled scan finishing the copy anyway | **[F]** `StageAsync` w/ `CopyToAsync` and the token threaded through |
| A4 | `VobSubIndex.MaxLinesRead` | The cap `break`s and returns what it read. A truncated `id:` block still parses as a valid subtitle → a track silently missing its tail, ¬a refusal | **[F]** hitting the cap returns `[]` / `false` |
| A5 | `VobSubStaging.Sweep` | Time-based only, no refcount → a scan starting while another sync is six hours into the same file deletes the staging under it. Z4 point 4 named this and sweep-only shipped | **[F]** `Stage` stamps the folder mtime on every call. A stage is followed by one conversion inside one timeout, so six hours cannot elapse between the two |
| A6 | `SubtitleTarget.ExternalStreamKey` | A multi-stream file's pre-existing `ext:` record is orphaned by the new key. No data loss — the old row still points at its backup and rollback still works — but a merged-language output already on disk is left there and thereafter skipped as plugin output | **[A]** |

**A6 accepted, ¬fixed.** Migrating would mean guessing which of the new per-stream rows inherits a record written for the whole file, and there is no answer: the old output is 24 interleaved languages and belongs to none of them. Field exposure is nil — Gravity never produced an output (it timed out four times) and both of Sandakan's targets are refused. ! The orphan is *inert*, ¬leaked: rollback reaches it through its own row.

! **The NUL byte is what nothing caught.** Not the build, not `check-comments`, not any harness — only a person reading the diff. Worth remembering next time a file goes quiet under `grep`.

### Z3. Letting the engine score **accept** an `Inconclusive` title — refuted by V11's own table **[R]**

Raised here ∵ the engine's VAD is genuinely independent of our level detector and the score is currently consulted only to refuse — and w/ `RequireAudioConfirmation` on it is ¬consulted at all (`SyncOrchestrator` :449 returns first). The proposal was an acceptance bar above the refusal bar, ≈75.

**V11 already measured the case that kills it.** `Futurama S01E04` scores **49.5 a second — inside the honest 41.7–161.3 range — while applying a bogus 1.043 PAL stretch to an NTSC DVDRip.** It scores *higher than TNG S02E02's correct alignment at 41.7*, and a bar at 75 is straddled by Sahara's correct 75.9. There is no threshold that separates a right sync from a confidently wrong one ∵ the score measures **the engine agreeing w/ itself**, ¬the placement.

→ the V11 wording stands unchanged: **a floor, never a warrant.** ! The distinction to keep hold of — the score answers "did the engine find *an* alignment", the acceptance gate must answer "is this alignment *right*". Different questions; only the second one can admit a write. Do ¬raise a third time.

**Corroboration for Y1, from the field.** Five `Sync failed for X: "[N:N:N] INFO User config path: utils.py:N` lines in `log_20260815.log` — raw engine stderr reaching `Reason()` and being cut at the first interior `": "`, exactly the case the twenty-sixth pass closed. It was a live defect, ¬a hypothetical.

**Also seen, ¬yet chased.** 5 refusals carry `(0 windows)` → the sample failed outright and the stretch guard still fired on the engine's figure. 87 titles over 36 min *did* reach six windows and still returned null drift, so one of the two halves failed its own `Fit` — a distinct failure from Z2 and unmeasurable from the log alone ∵ the produced file is deleted at :395.

---

## 2026-08-16 (twenty-sixth pass) — full checklist against the delta since 1.2.6.0

Scope: the ten-point checklist in `CLAUDE.md` over the whole tree, weighted to the delta since `c75115d` — the wording batch (`6f2b1de`) and the X1/X2 verifier change. ! The twenty-fifth pass was a **field-log investigation**, ¬a checklist pass; only item 10 was actually exercised there. This is the pass that covers 1–9.

`verify.ps1` green: build 0/0, comment lint clean over 61 files, all twelve harnesses, manifest matches the lock, both payloads present on both RIDs.

| # | Where | Defect | Consequence | Status |
| --- | --- | --- | --- | --- |
| Y1 | `SyncOrchestrator.Reason` :1060 | `IndexOf(": ")` finds the **first** occurrence anywhere in the string, incl. across newlines | Correct for the plugin's own `Rejected:`/`Failed:` messages, which is what it was written for. But `Fail(record, attempt.Message)` :318 passes **raw engine stderr** → a colon partway through a dump makes the log line drop every line above it. Log legibility only; `record.Message` keeps the full text and the panel groups on the last line | **[F]** |
| Y2 | `SyncVerifier.SampleAsync` :123 | The `PerSyncTimeoutMinutes` budget is applied **per ffmpeg invocation**, and a sample is up to `MaximumWindows` invocations in a `foreach` | Worst case for one verify is **16 × 20 min = 320 min** at the default, holding a queue slot the whole time. ¬a fan-out — sequential, and the task's cancellation token is linked — but a stalled mount rather than a dead one is the realistic trigger, and this library is on **SMB** | **[F]** |

**Y1 fixed.** The strip is anchored: the prefix is taken only where the text before `": "` holds no space, CR or LF, so a single action word goes and anything else stays whole. The damaging case is the one this closes — `Traceback (most recent call last):\n … \nValueError: bad` previously logged only `bad` and now logs the dump intact. ! It is ¬exhaustive: a stderr line whose *first* token ends in a colon (`INFO: …`) still loses that one token. That is one word off the front of a message the record keeps in full, and matching an explicit action-word list instead buys nothing — `Reason` is reached only from `Fail` and `FailStage`, whose messages are always `Rejected:` or `Failed:`.

**Y2 fixed.** A linked `CancellationTokenSource` in `SampleAsync` carries `PerSyncTimeoutMinutes` **once** across the whole read. On expiry the loop breaks and the existing `used < Math.Min(MinimumWindows, windows.Count)` test decides whether the partial sample is usable → a stalled mount costs one timeout, ¬sixteen. ! The filter is `when (!cancellationToken.IsCancellationRequested)`, so a real user cancellation still propagates rather than being read as a timeout.

**Sizing, measured before choosing.** ≈1.2 s per window: Twin Peaks 16 windows in 18.9–27.8 s across two runs, Mad Men 7 in 5.9 s, TNG 7 in 4.9 s, Simpsons 4 in 2.0 s; field-log sync durations ran 1.6–31 s end to end. A 20-minute *sample* budget is ≈43× the slowest observed full read → the existing default is safe unchanged, and no new setting is needed.

! **A partial sample cannot manufacture a verdict.** The missing windows are the late ones, so `Drift`'s `late` half finds no onsets, its `Fit` returns null and drift is null — no false `Misaligned`. The whole-film `Fit` computes `reachable` from the full plan, which would inflate the floor, except X2 already caps the floor at `buckets.Count` → it self-corrects to what was actually read.

**Regression evidence.** `calibrate.ps1` reproduces every verdict, shift, hit count, floor and onset count exactly, w/ no `ran out of time` line; Twin Peaks exercises the 16-window path that Y2 is about. `verify.ps1` green: build 0/0, comment lint clean over 61 files, twelve harnesses, manifest and both payloads.

### Verified clean, so a later pass ¬redoes it

- **Process spawning (6).** Every one of the five spawn sites builds `ArgumentList`; **no `Arguments =` string exists in the tree**. All three runners — `AssyCliRunner`, `SeConvRunner`, `FfmpegProcess` — carry `PerSyncTimeoutMinutes` + `Kill(entireProcessTree: true)`. The verifier's ffmpeg inherits both ∵ `FfmpegProcess` reads `Plugin.Instance.Configuration` itself → Y2 is about the budget's *scope*, ¬its absence.
- **Write scoping (7).** Both destructive paths **gate** on the vault rather than merely preceding it: `SubtitlePlacer.Overwrite` :59 returns null when `Store` does, and `SubtitleDeduplicator.Remove` :204 does the same under its `duplicate` label. The engine-supplied output path is contained by `IsWithin(scratchDir, …)` :748, case-folded on Windows only.
- **Rollback (9).** `File.Delete` fires only behind **both** a matching `record.OutputPath` and `SubtitleNaming.IsPluginOutput(…, MarkerSuffix)` :170, and restore precedes delete.
- **API (5).** Class-level `[Authorize(Policy = Policies.RequiresElevation)]` → every endpoint inherits it, incl. the four added since. No endpoint takes a path; `SyncItem` takes a `Guid` and resolves server-side.
- **Deserialization (1).** `System.Text.Json` only — no `BinaryFormatter`, no `XmlSerializer` over untrusted input. The one `IXmlSerializer` is Jellyfin's own config plumbing.
- **Blocking async (2).** None. Every `.Result` in the tree is a property read on `AssyInvocation.Result`, ¬`Task.Result`; no `.Wait()`, no `GetAwaiter().GetResult()`.
- **One sample, two questions (2).** Confirmed still true after the X1 change: `Score(sample, placed)` reuses the pre-sync sample, and the `VerifyAsync` fallback only runs where `sample` is null — which is also the case where `Starts` returns null and it exits before decoding. No double decode on any path.
- **Races (3).** `SyncStore` takes its lock on every accessor. `DryRunMode` + rollback interaction is **already accepted** at the twenty-second pass — ¬re-flag.
- **The X1/X2 delta itself.** `ShiftFit` is a `readonly record struct` returned by value ≤3× per score; no allocation added. `Drift` reads `first.Strength`/`second.Strength` where it previously read two `out` params — same values, same `Math.Min`. The drift verdict carries the *whole* fit's hits/floor/onsets while its strength comes from the halves, which is ¬reported anywhere the two could be confused: only the `Inconclusive` log line prints hits/floor/onsets.

---

## 2026-08-15 (twenty-fifth pass) — the 1.2.6.0 field logs: 66 titles returned no verdict

Scope: the first full scan under 1.2.6.0 — 14 min, 10,027 items, **441 verify readings**. Zero plugin exceptions, zero ffmpeg failures, zero timeouts; `AdaptiveConcurrency` settled 2 → 4 → 5 as other server load fell away. The concern is ¬stability, it is **coverage**: 52 written against 124 refused, and the largest single refusal bucket is 66 × "the audio check could not confirm this result".

| peak strength | readings |
| --- | --- |
| ≥ 1.40 — usable | 349 |
| 0.01–1.39 | 26 |
| **exactly 0.00** | **66** |

New tooling: `check-inconclusive.ps1` — runs `verifycheck` over the titles a log says were unmeasurable and reports **which of `BestShift`'s three gates fired**. It reads the hit count, floor and onset supply straight out of the shipping verdict (X1) rather than recomputing them → cannot drift from what ships.

### X1. `Nothing()` discards the strength it just measured **[F]**

`Fit` takes `out var strength` and `BestShift` assigns it **before** its gate test → the value is real on the refusal path. `Score` then drops it: `whole is not { } best → Nothing(sample.Windows)`, and `Nothing` hardcodes `Strength = 0`. Every inconclusive verdict therefore reaches the log as `peak 0.00x` **whichever gate refused it**, and the three are not the same problem. This is why the field logs cannot attribute their own largest failure bucket, and why a harness was needed to answer a question the plugin already knew the answer to.

**Fixed.** `BestShift` became `BestFit` returning a `ShiftFit` — the winner plus the strength, hit count, floor and onset supply it was judged by; `Fit` and `Drift` thread it through and `Nothing(windows, fit)` carries it. `VerificationResult` gained `Hits`/`Floor`/`Onsets`, and the refusal log line reports all three. The 3-arg `BestShift` overload the harness cases use is unchanged. → the next scan attributes its own rejections on the real post-sync cues, w/ no media re-read and no harness.

### X2. `MinimumHitShare` is a share of **cues**, but hits are bounded by **onsets** **[F]**

`var floor = Math.Max(MinimumHits, (int)(reachable * MinimumHitShare));` — `reachable` counts cues the windows can reach. What a title can actually supply is onsets, and on a continuously-scored mix the two differ by an order of magnitude. Six titles profiled:

| title | win | cues in win | onsets | peak | floor | /mean | /rival | refused by |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Chappelle S02E01 | 4 | 127 | **42** | 20 @ **150 ms** | 31 | 2.30 ✓ | 1.43 ✓ | **floor** |
| Kids Next Door S01E01 | 4 | 105 | **27** | 11 @ −925 ms | 26 | 2.75 ✓ | 1.57 ✓ | **floor** |
| Midnight Diner S03E07 | 4 | 89 | **30** | 13 @ −50 ms | 22 | 3.61 ✓ | 1.86 ✓ | **floor** |
| MPFC S01E01 | 6 | 119 | 155 | 37 @ 600 ms | 29 | 1.55 ✓ | **1.23 ✗** | rival |
| Nathan For You S01E01 | 4 | 127 | 17 | 5 @ −3275 ms | 31 | 2.27 ✓ | **1.00 ✗** | rival + floor |
| TNG S02E02 | 7 | 122 | 305 | 63 @ 1400 ms | 30 | 1.58 ✓ | 1.29 ✓ | — passes |

**Two populations, ¬one.** MPFC and Nathan are the recorded V9/V10 case — 155 onsets and still a flat sweep, `/rival` at or below the gate. No threshold rescues them and none should be tried; that is settled.

The first three are a different failure. Both gates that separate signal from noise pass, and pass **well** — 1.86× against a 1.25 bar. Chappelle hit **48% of every onset the audio offered** and it reads as 16% of cues against a 25% bar. The denominator measures something the title cannot supply, and its answer (150 ms) is right. These are refused for having a laugh bed, ¬for being out of sync.

! ¬simply lower the floor. Kids Next Door's peak is **11 hits** — `MinimumHits=12` exists precisely to stop a verdict being read off a handful, and 1.57× off 11-vs-7 is noise where 2.30× off 20-vs-14 is not. Scaling the bar to onset supply must keep an absolute floor under it.

**Fixed** as `floor = Math.Max(MinimumHits, (int)(Math.Min(reachable, onsets) * MinimumHitShare))`. `PeakRatio` and `RivalRatio` are untouched — the gates that separate signal from noise are ¬what refused these titles. Re-measured, and it does exactly what it was meant to and nothing else:

| title | floor | before | after |
| --- | --- | --- | --- |
| Chappelle S02E01 | 33 → **12** | Inconclusive | **Aligned 150 ms** |
| Midnight Diner S03E07 | 24 → **12** | Inconclusive | **Aligned −50 ms** |
| Kids Next Door | 28 → 12 | Inconclusive | **Inconclusive** — 11 hits, held by `MinimumHits` |
| MPFC S01E01 | 32 — unmoved | Inconclusive | Inconclusive — rival 1.23 |
| Nathan For You | 34 → 12 | Inconclusive | Inconclusive — 5 hits, rival 1.00 |
| TNG S02E02 | 32 — unmoved | Misaligned 1400 ms | Misaligned 1400 ms |

**Swept over all 69** titles the 1.2.6.0 scan could not measure. **26 now return a verdict** — 20 `Aligned`, 6 `Misaligned`; 30 still refused on `rival`, 6 on both, 7 on the floor.

! **The offsets are the evidence the fix admits signal rather than noise.** All 20 `Aligned` land between **−325 ms and +375 ms**, clustered on zero the way correctly-timed subtitles are. A floor that had started passing noise would scatter answers across the whole ±4000 ms sweep; the 6 `Misaligned` do spread out (−1400, 525, 700, 1400, 2700 ms + one drift verdict), which is what real misalignment looks like.

**The effect is fewer writes, ¬more.** W/ `RequireAudioConfirmation` on, an `Inconclusive` **pre**-sync check still proceeds to sync and is refused afterwards — every one of these 69 paid for an engine run and lost it. The 20 now resolve `Aligned` *before* the engine is called → *skipped, already aligned*: no sync, no write, no refusal.

The 7 still held by the floor are almost all under `MinimumHits` — 4, 5, 7, 8, 11 hits. ! MPFC **S02E05** is the exception at 25 hits against a floor of 27, and holding it is **correct**: the twenty-fourth pass measured that title genuinely **612 ms** out. Two of the 20 rescues (Nathan For You S03E01, S03E06) sit *exactly* on `RivalRatio` at 1.25× — the narrowest margin the gates allow, reading −25 ms and −200 ms. **[O]** if either turns out wrong in a later scan, that pair is where to look first.

! Both sample rescues land **inside `AlignedWithinMs`** → they become *skipped, already aligned* rather than writes. The change buys back titles the plugin was paying to sync and then refusing, ¬titles it was about to overwrite. `calibrate.ps1` is **byte-identical** across all five titles × both conditions, ∵ on every one of them onsets outnumber reachable cues and the floor never moves. Two new `verifycheck` cases lock it: *few onsets still measure when they all agree* (fails w/o the fix, verified by mutation) and *few onsets agreeing with nothing are still refused*.

**More onsets is ¬the fix, and the cheap version was tested.** Dropping `silencedetect`'s `d` on Chappelle:

| d | onsets | /mean | /rival |
| --- | --- | --- | --- |
| 0.35 — as shipped | 42 | 2.29 | 1.43 |
| 0.20 | 70 | 2.12 | 1.36 |
| 0.10 | 139 | 1.57 | **1.24 ✗** |

3× the onsets and the discrimination *falls through the gate*. The onsets gained are micro-gaps, ¬line starts — the same result V9 and V10 reached from two other directions, reproduced here on a title neither of them covered.

### X3. ! The harness measures the **pre-sync** sidecar; the log records the **post-sync** verdict **[R]**

`SyncOrchestrator` :354 scores `Score(sample, placed)` where `placed` is the cue list of the file the **engine produced**. `check-inconclusive.ps1` profiles the sidecar on disk. Same audio sample, same window plan, **different cues** → the two answer different questions, and TNG S02E02 is the proof: its sidecar measures `Misaligned 1400 ms` (matching the twentieth pass exactly) while the log records `7 windows, peak 0.00` for the synced output. The check refused the engine's work, which is the check doing its job.

→ **the sweep answers the pre-sync question, ¬the log's.** Run over all 69 it says which titles the check can now judge *before* the engine is called, which is what X2 is measured against above and is worth having on its own. It does **¬** reproduce the post-sync verdicts the log recorded, and no sweep on this method can: the produced files were deleted on refusal. ! ¬use it to attribute field rejections — X1 now puts the hit count, floor and onset supply in the log line, so the next scan attributes its own.

The six-title sample is what exposed this; a straight 69-title run would have produced a confident and wrong answer. The harness keeps its value for the question it does answer — *given this audio and these cues, which gate refuses* — which is what produced X2. Two traps it cost: `Test-Path` needs `-LiteralPath` (a release folder named `[UTR]` is a wildcard character class → the title reads as missing media), and PowerShell's `[int]` rounds to even where the C# cast truncates → a borderline floor moves by one.

### X4. ! `calibrate.ps1`'s control set lives in the live library, and the plugin rewrote one **[F]**

The A/B above showed MPFC S01E02 reading `Aligned 200 ms` where the twenty-fourth pass recorded `Misaligned 975 ms` at the same 1.49× and the same six windows. ¬a regression: **identical on both code versions**. The 1.2.6.0 scan *synced* that title — `shifted 820ms`, verify passed `Aligned`, sidecar rewritten 22:33:34 — so the fixture is now the post-sync file. Ground truth had it 480 ms out, the engine moved it 820 ms, it now sits at 200 ms → the N5 raise from the twenty-fourth pass worked, and this is the confirmation.

! But the harness exists **∵ its five titles have recorded behaviour**, and the plugin edits the library those titles live in. Any scan can silently move a control. **The failure is bidirectional**: a drifted fixture invents a regression that is ¬there, or — the dangerous direction — moves a control from `Misaligned` to `Aligned` and reports an unsafe change as having preserved the set. Nothing distinguished either from a code change. It was only caught here ∵ an A/B happened to be running; a single-sided run would have read it as *"my change broke MPFC S01E02"*.

**Fixed.** The five sidecars are vaulted under `agentic/tools/verifycheck/fixtures/` w/ `fixtures.json` recording each one's SHA-256 plus the video's length and the SHA-256 of its **opening megabyte** (hashing a multi-gigabyte file over SMB costs minutes per title; a re-encode moves both). `calibrate.ps1 -Vault` records them — merging, ¬replacing, so `-Vault -Only x` cannot drop the rest — and a normal run compares the live files, prints `! DRIFT` w/ the sidecar's mtime, and **measures the vaulted copy** so the five keep meaning what this file says they mean. A drifted video is reported but ¬recoverable: the audio itself changed, so the recorded behaviour is genuinely stale and wants a deliberate re-measure. Verified by corrupting a lock entry: both checks fire, the run continues on the vault, and the clean run reproduces every live-file reading exactly.

! Third trap, found building the vault: **a `.ps1` w/ non-ASCII must be written w/ a UTF-8 BOM.** Windows PowerShell 5.1 reads a BOM-less file as ANSI, and an em dash (`E2 80 94`) decodes under CP1252 w/ a trailing `0x94` — a **right double quote** — which silently breaks string parsing several lines later. The house glyph key (`→ ∵ ¬ —`) puts these in every script here, so this is a standing hazard, ¬a one-off. Symptom is a parser error pointing at an innocent line.

! Second trap, same investigation: `Copy-Item` **preserves the source's `LastWriteTime`**. Restoring a file from a backup therefore hands MSBuild a source *older* than the built DLL → it skips the rebuild and the next run measures the code that was just reverted. Cost a full round of wrong readings before the floor value gave it away. Touch the file, or compare the binary's behaviour rather than trusting the build.

### Not concerning, checked

- **19 Trakt exceptions** in the same window (`PostToTrakt`, queued episode/movie events) — that plugin's own API handling. Possibly provoked by refreshes from sidecar writes; the failure is ¬ours.
- **2 × `MediaEncoder` filter warnings** — `overlay_vaapi` / `overlay_vulkan`, hardware **video** overlay probes at startup. Nothing to do w/ `silencedetect`.
- **Jellyfin cannot delete its own superseded plugin folders** — `AutoSubSync_1.2.0.0` / `_1.2.1.0` / `_1.2.4.0` / `_1.2.5.0`, each `UnauthorizedAccessException: ... .dll is denied` from `PluginManager.DiscoverPlugins`. The pre-restart process still holds the DLL. Jellyfin's cleanup path, ¬ours; stale folders accumulate and are safe to remove w/ the server stopped.

---

## 2026-08-15 (twenty-fourth pass) — the N5 window raise, swept over real titles

Scope: the twenty-third pass delta — `PlanWindows`' conditional raise, `DriftWindows` private → internal, `verifycheck --plan`. Driven by sweeping **32 real titles** instead of the fixed five, which is what turned up W1 and W2. ! The calibrate set could not have found either: no title in it sits in the affected band except MPFC.

New tooling: `verifycheck --plan` (window plan, no audio decoded — sweeps a library cheaply for the titles a planning change would move) and `check-verifier-error.ps1` (the check's reading beside ground truth, which is how W1 was finally characterised).

**The affected band is narrow.** The raise fires only for a cue span in ≈[27, 36) min. Swept across 21 titles from 21 to 102 min — Star Trek TOS, Firefly, Community, Better Call Saul, Mad Men, The Wire, Dexter, Peaky Blinders, Westworld, Narcos, Downton Abbey, Fallout, Invincible, The Tudors, Batman '66, Midnight Diner, Brooklyn Nine-Nine, Chappelle's Show, Mr. Inbetween, Nathan For You, The Leftovers — **not one changed**, and every one keeps a full 90 s window. The whole of MPFC (46 sidecars) is in the band: 40 gain a sixth window, 6 sit under 27 min and keep four.

### Eleven MPFC episodes measured **both ways**, four against ground truth

| Episode | 4/5 windows | 6 windows | ground truth | right answer |
| --- | --- | --- | --- | --- |
| S01E02 | Inconclusive, 0.00x | Misaligned 975 ms, 1.49x | out 480 ms | **6 win** |
| S02E12 | Misaligned 600 ms, 1.38x | Aligned 325 ms, 1.34x | out **299 ms** | **6 win** — the 4-win reading was a false refusal |
| S02E05 | Misaligned 575 ms, 1.43x | **Inconclusive, 0.00x** | out **612 ms** | **4 win** — a real miss the raise cannot see |
| S01E11 | Inconclusive, 0.00x | Misaligned 575 ms, 1.67x | — | 6 win, unverified |
| S04E03 | Inconclusive, 0.00x | Aligned −400 ms, 1.49x | — | 6 win, unverified |
| S01E05 | Misaligned −1275 ms | Misaligned −1250 ms, **+ drift −300 ms** | — | tie, drift gained |
| S01E08, S02E08, S03E02, S03E05, S03E08 | Inconclusive | Inconclusive | — | tie |

Verified head-to-head: **2–1 for the raise**, plus two unverified gains and a drift measurement where there was none. ! Window *length* was 90 s on both sides of every row — S02E05 degraded on window **placement**, ¬length. `stride = (spanMs - length)/(count - 1)` → changing the count moves every window, and on MPFC that walks one into a non-speech sketch. **More windows is ¬monotonically more information**, which is the assumption both earlier attempts rested on.

| # | Where | Defect | Consequence | Status |
| --- | --- | --- | --- | --- |
| W1 | `SyncVerifier.Score` | The reading sits up to **906 ms** from ground truth, and `AlignedWithinMs` is 500 | A title near the bound flips verdict on sampling alone — S02E12 read 600 ms and 325 ms for the same file. ! But the error is **one-directional** — 11 titles, 9 right, 2 refused-in-error, **0 wrongly accepted** → see below | **[A]** |
| W2 | `SyncVerifier.PlanWindows` :245 | The raise loses S02E05's genuine 612 ms miss to `Inconclusive` | W/ `RequireAudioConfirmation` **on**, `Misaligned` and `Inconclusive` both refuse → no write difference. W/ it **off**, `Misaligned` refuses but `Inconclusive` falls through to the score gate → a possible write. One case in eleven, non-default config only | **[A]** |
| W3 | `SyncVerifier` :123 | Two extra `silencedetect` passes per verify on a band title | ≈1400 → 1700 ms measured on MPFC, ≈+20%. Sequential in `SampleAsync`'s `foreach` → ¬a fan-out change | **[A]** |

### W1 measured properly: the check over-refuses, it does ¬over-accept

Two fixes were tried and one hypothesis died before the useful measurement was made.

**Attempt — coarse-to-fine refinement. [R]** `Hits` counts a cue as matched anywhere inside `±ToleranceMs`, so the sweep is flat across a ≈500 ms plateau and `BestShift` returns its midpoint, which is only the answer where the plateau is symmetric. Re-reading the offset as the **median residual** of the cues that actually matched looked obviously right. It is not:

| Title | plateau midpoint | median residual | ground truth |
| --- | --- | --- | --- |
| TNG S02E02 | 1400 ms | 1399 ms | 775 ms |
| MPFC S01E02 | 975 ms | 928 ms | 480 ms |
| MPFC S02E05, 4 win | **575 ms → Misaligned** | **499 ms → Aligned** | **612 ms — genuinely out** |

→ the sweep was never the imprecise part, and the refinement turned a correct refusal into a **false accept** by moving a reading 76 ms across the bound. Reverted. ! ¬re-propose: the plateau midpoint and the median residual agree to within 50 ms, so there is nothing to win here and a bound to fall over.

**What the gap actually is.** The check measures a cue against the *speech it belongs to*; ground truth measures it against the video's *own embedded track*. The difference is the subtitle's lead-in plus `silencedetect`'s own detection lag, and neither is separable from real misalignment using audio alone. `check-verifier-error.ps1` (new) puts both numbers side by side. Eleven titles across two shows:

| | gaps (check − truth) |
| --- | --- |
| MPFC ×6 | +495, **−906**, +125, +100, +26, −37 |
| TNG ×5 | +625, +523, +598, +300, +530 |

Worst 906 ms; TNG is consistently high, MPFC is not → the bias is per-source, ¬a constant that can be subtracted.

**But the verdicts are almost all right, and wrong only in the safe direction.** Scoring each verdict against ground truth, calling a subtitle genuinely fine at ≤500 ms:

| Outcome | Count | Titles |
| --- | --- | --- |
| Correct | **9** | TNG S02E02/03/05/06/07, MPFC S01E07/S01E10/S02E12/S02E05 |
| Refused a subtitle that was fine | 2 | MPFC S01E02 (read 975, truth 480), S01E05 (read −1250, truth −344) |
| **Accepted a subtitle that was out** | **0** | — |

→ W1 costs **coverage, ¬safety**: ≈2 in 11 good subtitles refused, and nothing bad written. That is the trade already asked for, so `AlignedWithinMs` stays at 500 and **no undecided band was added** — a band would refuse more of the 9 without removing a risk the evidence shows exists. ! The one measured false *accept* in this whole pass came from a **fix**, ¬from the shipped code.

**Verified clean:**

| Area | Why |
| --- | --- |
| `PlanWindows` inputs | `Starts()` sorts → `spanMs >= 0`. `count` clamped `[4,16]`; `spanMs / (DriftWindows*3)` is `long` integer division by the constant 18 → ¬overflow, ¬divide-by-zero. A crafted subtitle cannot drive the window count or length |
| Process spawning | Two more children per affected verify, each through the existing bounded `Reader` w/ its timeout and tree kill. No new spawn path, no concurrency change |
| Races | `PlanWindows` is a pure static over its arguments; no shared state |
| Dry run, write scoping, rollback, API, filesystem | Untouched — the delta is one arithmetic branch inside the audio check plus a harness-only output mode |
| `DriftWindows` private → internal | Same assembly; the harness links the source rather than copying the constant, matching `MinimumSpanMs`/`MinimumPairs` in `SubtitleOffsetProbe` |
| Comments | `check-comments` clean over 61 files |

---

## 2026-08-15 (twenty-third pass) — closing the eleven open findings

Scope: every `[O]` carried by the twenty-first and twenty-second passes, plus M5 from the thirteenth. All eleven close. N5 took three attempts and is the one worth reading.

### N5: window **count** and window **length** trade against each other **[F]**

```text
count  = clamp(spanMs / (MinutesPerWindow*60_000), MinimumWindows, MaximumWindows)
length = min(WindowSeconds*1000, spanMs / (count*3))
```

Buying windows spends window length, and a window too short holds too few onsets to correlate. Two attempts failed on that before the third worked.

**Attempt 1 — `MinutesPerWindow` 6 → 3. [R]** That moves the *divisor*, so it rescales every title, including ones that never had the defect:

| Title | `= 6` | `= 3` |
| --- | --- | --- |
| TNG S02E02 — control, genuinely 1400 ms out | **Misaligned 1400 ms, peak 1.29x, 7 win × 90 s** | **Inconclusive, peak 0.00x, 14 win × 60 s** |

The measurable control stopped being measurable. Reverted.

**Attempt 2 — `MinimumWindows` 4 → 6.** That moves the *clamp floor*, so it touches only titles already sitting on it. Both controls (7 and 16 windows) are above the floor and came back byte-identical. But `verifycheck` refused it: an 11-min span would get 6 windows of **37 s** — the same shrinkage, on titles too short to have been in the calibrate set. ! The harness caught what the five real titles could not.

**Attempt 3 — take the extra windows only where they are free. [F]** Raise the count to `DriftWindows` only if the span can afford that many at **full** `WindowSeconds` length:

```csharp
if (count < DriftWindows && spanMs / (DriftWindows * 3) >= WindowSeconds * 1000L)
```

→ the raise applies from a ≈27-min cue span up, and never shortens a window to buy one.

| Title | before | after |
| --- | --- | --- |
| MPFC S01E02 (≈30 min) | Inconclusive, peak 0.00x, 5 win | **Misaligned 975 ms, drift −50 ms, peak 1.49x, 6 win** |
| Simpsons S01E10 (≈23 min) | Inconclusive, 4 win × 90 s | unchanged — the raise declined, ∵ 6 would cost 14 s a window |
| Mad Men, TNG, Twin Peaks FWWM | — | **identical** |

**The new MPFC verdict was checked against ground truth**, ¬trusted ∵ it looked like an improvement. `check-vs-embedded.ps1` says that sidecar is genuinely out by 480 ms → the reading is real, ¬a phantom.

! **A "consistent display lead" was recorded here on two data points and is wrong.** Four titles do ¬support it — the gap between the check and ground truth is measurement variance, ¬a constant that can be subtracted:

| Title | audio check | ground truth | gap |
| --- | --- | --- | --- |
| TNG S02E02 | 1400 ms | 775 ms | +625 ms |
| MPFC S01E02 | 975 ms | 480 ms | +495 ms |
| MPFC S02E12 | 325 ms | 299 ms | **+26 ms** |
| MPFC S02E05 | 575 ms | 612 ms | **−37 ms** |

→ W1 below. The check is accurate on some titles and out by 600 ms on others, and nothing so far predicts which.

Three new `verifycheck` cases pin the rule: six windows where affordable, four where not, and the existing 11-min case that caught attempt 2.

### Fixed

| # | Fix | Note |
| --- | --- | --- |
| M5 | New `SyncCancellation` singleton: `Token`, `StopAll()`, `LinkWith()`. `FullLibrarySyncTask` registers `StopAll` on its own token; `LibraryEventHandler` links the shared token w/ `_shutdownCts`; `SyncItem` passes `_cancellation.Token`, ¬`CancellationToken.None` | ! `StopAll` cancels the old source and does **¬**dispose it — a caller that read `Token` a moment ago is still linking against it, and disposing underneath that throws |
| N3 | `ReopenFailed` clears `Stages` as well as status, bound, and message | |
| N4 | `MaximumUnverifiedShiftMs = 60_000` bounds a constant shift on an `Inconclusive` verdict | Only reachable w/ `RequireAudioConfirmation` **off**. Deliberately loose: a sidecar cut for another release is legitimately tens of seconds late |
| S1 | `LibraryScopeResolver.IsUnder` takes the OS-dependent comparison, matching `SyncOrchestrator.IsWithin` | |
| S2 | `MaxBytesRead = 16 MiB`, checked via `FileInfo.Length` before `File.ReadLines` | Bounds by **bytes**; the line cap could not |
| S3 | `AppendBounded` in all three stderr readers — 512 KiB kept, 64 KiB slack before a compaction | ! Verified the alignment parse is unaffected: `EngineAlignment.From` reads `score:`/`offset seconds:`/`framerate scale factor:`, all printed at the **tail** |
| S4 | 200 ms `matchTimeoutMilliseconds` on all 8 `GeneratedRegex` | Consistency w/ `SdhDetector`, ¬a ReDoS fix — there was none available |
| S5 | `NormalizeCue` rewritten to bounded bracket spans: `MaxMarkupTag = 48`, `MaxOverrideBlock = 160` | A stray `<` now swallows at most 48 characters, ¬the rest of the cue |
| S6 | `SupportedEncodings` allow-list; `Normalize()` falls back to `same_as_input` on anything else | A typo can no longer fail every sync in the library |
| S7 | `PayloadBootstrap` tracks its two tasks; `StopAsync` awaits them w/ a 5 s `SettleTimeout` before `Dispose` can strand them | |
| N1 | Accepted, unchanged — same TOCTOU shape as `RollbackAll`, ¬corrupting ∵ accessors clone | **[A]** |
| N2 | Accepted, unchanged — the whole-library re-examination is wanted here | **[A]** |

**Verified after the pass:** build clean · `check-comments` clean over all 61 files (nine of mine rewritten; ! a bare run reports `no git base available` and lints **nothing** — pass the source root) · configcheck, gatecheck, storecheck, dedupecheck, rollbackcheck, measurecheck, verifycheck, subcheck, placecheck, langcheck, namingcheck, payloadcheck, check-rate-bound all green · `calibrate.ps1` shows one changed title, in the intended direction, confirmed against ground truth.

**New tooling:** `check-vs-embedded.ps1` — compares a sidecar against the video's own embedded track, ground truth independent of both the engine and the audio check. ! Its first verdict rule averaged early and late offsets, which reports a **symmetric stretch as in-sync** — the exact defect it was written to find. It now tests spread against 500 ms before averaging.

---

## 2026-08-15 (twenty-second pass) — the 1.2.5.0 field logs: the engine's rate factor writes bad syncs

Scope: the delta since 1.2.5.0 — the two new refusal gates, `RequireAudioConfirmation`, `ReopenFailed`/`RetryFailed`, the accept-side debug line — plus the checklist against it. Driven by the 1.2.5.0 field logs, where a **proven** bad write was found and reproduced end to end.

### V12. The engine invents a ≈0.1% rate factor on unmeasurable titles **[F]**

Ground truth: the video's own embedded track, compared cue-for-cue against the sidecar (`check-vs-embedded.ps1`). Two MPFC episodes, before and after the plugin, both **Retimed** in place:

| | early | late | spread |
| --- | --- | --- | --- |
| S01E08 backup | −206 ms | −257 ms | **51 ms** — correct |
| S01E08 written | −217 ms | +741 ms | **958 ms** — drifting |
| S03E02 backup | −211 ms | −79 ms | **132 ms** — correct |
| S03E02 written | −377 ms | +396 ms | **773 ms** — drifting |

Chain, every step measured: audio check → `Inconclusive`, 4 windows, peak **0.00x** → engine reports rate **1.001** → confidence **36.9** / **37.1** a second → old gate `< 20` passes → a correct subtitle is stretched, ending 741 ms out, past `AlignedWithinMs`. In one run 47 of 230 accepted syncs sat in a 1200–1800 ms rate-correction spike — a near-constant ≈1.5 s across shows of differing runtime, ∵ the same factor recurring, ¬a real framerate conversion. ! `1.001`/`0.999` are genuine NTSC ratios → plausibility is ¬evidence.

| # | Where | Defect | Consequence | Status |
| --- | --- | --- | --- | --- |
| V12 | `SyncOrchestrator` verify block | Nothing bounded an **unmeasured** stretch. `MaximumRateDrift = 0.30` bounds the *ratio*; `SyncVerifier.Drift` needs `Windows >= 6`, and `PlanWindows` clamps to 4 below a 36-min cue span → every ≤36-min title reaches the write w/ drift never measured | A correct subtitle stretched by ≈1.7 s | **[F]** |
| V13 | `SyncOrchestrator` :388 | Gate at **20** vs. a real-alignment floor of 40 | 36.9 and 37.1 both passed and both wrote damage | **[F]** → 40 |
| V14 | `SyncOrchestrator` :389 | `confidence is { }` false when the engine printed **no** score → gate skipped → **accepted w/ nothing vouching for it** | Reachable via the A12 salvage path (engine exits w/o a result but writes a complete file); 3 hits in one run | **[F]** |
| N1 | `AutoSubSyncController.RetryFailed` | `_queue.InFlight` guard is TOCTOU | An `AutoSyncOnItemAdded` sync starting after the check upserts its own result over the reopen → that one record loses its retry. Accessors hand out clones → ¬corruption. Same shape as `RollbackAll` | **[A]** |
| N2 | `PluginConfiguration.OutcomeStamp` | Gained a field | Every existing `SettingsStamp` mismatches → the first scan after upgrade re-examines the **whole** library, ¬incrementally. Wanted here ∵ everything needs re-judging under the new gates | **[A]** |
| N3 | `SyncStore.ReopenFailed` | Leaves `Stages` from the failed attempt | The status panel shows the previous Verify/Sync stages until the record runs again; `RecordStage` updates in place → self-correcting | **[F]** ↓ |
| N4 | `SyncOrchestrator` :420 | With `RequireAudioConfirmation` **off**, an unverified *constant shift* has no bound — only the score gates it | −27.3 s and −11.4 s proposals on correct subtitles were stopped by score alone, at a 3-point margin | **[F]** ↓ |
| N5 | `SyncVerifier` :157, :239 | Drift still unmeasured under a 36-min span; V12 bounds the damage, ¬restores the check | Closing it = `MinutesPerWindow` 6 → 3, ≈2× the audio sampling on short titles | **[F]** ↓ — but ¬by that means |

**V12 fix**: refuse where `verdict.DriftMs is null && |change.DriftMs| > SyncVerifier.AlignedWithinMs` — an unchecked stretch is held to the tolerance the check applies. Covers `Inconclusive` **and** short-title `Aligned`. **V13/V14 fix**: score floor to 40; a never-scored engine result on an `Inconclusive` verdict now refuses instead of falling through. **New setting**: `RequireAudioConfirmation`, default **on**, returns before both score gates → on an undecided check the engine's opinion is never consulted. ! Existing installs inherit **on**: `XmlSerializer` leaves the initializer alone for an absent element, pinned by `configcheck`.

**Verified clean:**

| Area | Why |
| --- | --- |
| Endpoint elevation | Class-level `[Authorize(Policy = Policies.RequiresElevation)]` covers `RetryFailed` w/ no attribute of its own. ! The count invariant is now **5**-vs-1, ¬4-vs-1 |
| Dry run vs. the three new gates | All live inside `RunPipelineAsync`; the `DryRunMode` return at :110–120 still stands ahead of all filesystem work → unreachable |
| Write scoping | Every new refusal deletes only `attempt.ProducedPath`, a scratch file the plugin created. No library path is reached on any refusal branch |
| Store concurrency | Accessors `Clone()` on the way out → `ReopenFailed`'s in-place mutation cannot corrupt an in-flight orchestrator copy; a late `Upsert` overwrites cleanly |
| `RetryFailed` blast radius | Reopens `Failed` only. `Synced` and `Skipped` are untouched → pressing it cannot cause a rewrite of a good file |
| Rollback, process spawning, path traversal, shell strings | Untouched by this delta |
| Comments | `check-comments` clean after three of mine were shortened |

---

## 2026-08-15 (twenty-first pass) — the files that had never been audited

Scope: the 14 source files w/ **zero** prior mentions here, plus every path in `SubtitleContent`, `SubtitleSimilarity`, `SeConvRuntime`, `PayloadBootstrap`, `AssyConfigFile`, `LanguageCodes`. Nothing exploitable. Seven Low defects + one correctness fix in code written earlier the same day.

| # | Where | Defect | Consequence | Status |
| --- | --- | --- | --- | --- |
| E1 | `EngineAlignment.From` | Score read as **last** match, ¬best | A framerate search prints one `score:` per candidate; last = whatever ordering left there. The gate only ever *refuses* → reading low refuses a good sync | **[F]** 1.2.5.0 |
| S1 | `LibraryScopeResolver.IsUnder` :93 | `OrdinalIgnoreCase` unconditionally | `SyncOrchestrator.IsWithin` :708–710 picks by OS deliberately. Off Windows `/media/Movies` and `/media/movies` are two roots → an item under one matches the other's scope → an excluded library gets synced | **[F]** ↓ |
| S2 | `SubtitleContent` :9, :317–351 | `MaxLinesRead = 400_000` bounds **lines**, ¬bytes | A file w/ no line break = one allocation of its whole length. Extension-gated to `.srt/.ass/.ssa/.vtt` → takes a corrupt or mislabelled file, ¬a crafted one | **[F]** ↓ |
| S3 | `AssyCliRunner`, `SeConvRunner`, `FfmpegProcess` | stderr accumulates in an unbounded `StringBuilder` | `Tail(4000)` trims only what is *stored*. A chatty child grows the buffer until its own timeout fires | **[F]** ↓ |
| S4 | 5 of 8 `GeneratedRegex` | No `matchTimeoutMilliseconds` | `EngineAlignment` ×3, `SyncVerifier` `silence_end`, `SubtitleOffsetProbe` timestamp. All linear, single quantifiers, no backtracking blowup available → **¬**a ReDoS, only inconsistent w/ `SdhDetector`'s 200 ms | **[F]** ↓ |
| S5 | `SubtitleSimilarity.NormalizeCue` :146–157 | `<` and `{` share one depth counter | A stray `<` in dialogue ("5 < 6") swallows text to the next `>` or `}`. Feeds the duplicate score, which drives deletion. Symmetric across two files → cancels unless only one carries the stray bracket | **[F]** ↓ |
| S6 | `AssyArgumentBuilder` | `config.OutputEncoding` free text, blank-checked only | Passed as `--encoding <value>`. ¬injection (`ArgumentList`) but a typo = exit 2 on **every** sync, w/ nothing in the failure naming the setting | **[F]** ↓ |
| S7 | `PayloadBootstrap.Dispose` :102–106 | Disposes `_shutdownCts` w/ `RunAsync`/`FetchSeConvAsync` possibly still in flight | `ObjectDisposedException` on token access during shutdown; logged and swallowed | **[F]** ↓ |

**E1 fix**: `Numbers()` returns every match, `From` takes `scores.Max()` for the score and keeps `Last()` (the applied figure) for offset and rate.

**Verified clean:**

| Area | Why |
| --- | --- |
| Endpoint elevation | `[Authorize(Policy = Policies.RequiresElevation)]` is at **class** level on `AutoSubSyncController` :18 → all four `Http*` methods covered. ! The 4-vs-1 attribute count is the invariant holding, ¬a gap |
| Dry run vs. the two new paths | `SyncOrchestrator` returns at :109–119 **before** `RunPipelineAsync` → the A12 salvage and the score gate are both unreachable w/ it on |
| Path traversal | `MarkerSuffix.SanitizeMarker` admits only `IsLetterOrDigit`/`-`/`_`, defaults to `autosubsync`; `SubtitleNaming.Sanitize` strips `< > : " / \ \| ? *` and trims dots |
| Shell strings | `AssyArgumentBuilder` is pure `ArgumentList`; ¬concatenation anywhere |
| Concurrent payload installs | `PayloadRuntime.ClaimAttempt` = locked check-and-set on a 15 min cooldown, **and** `PayloadFetcher` holds a per-tool `SemaphoreSlim(1,1)` → repeated config saves cannot stack downloads |
| Executable resolution | `SeConvRuntime` probe list is `static readonly`; no setting reaches it. PATH searched first — a PATH entry writable by a lesser user shadows tesseract, **[A]**: that is already a compromised server, and it matches how every process resolves a tool |
| `AssyConfigFile` | Content is a compile-time constant dictionary; no user input reaches the file |
| `SdhDetector` | 200 ms timeouts, bounded quantifiers `{1,20}`/`{2,80}`/`{1,24}`, lookaheads over negated classes |
| `LanguageCodes` | Table lookups + `CultureInfo` inside a `CultureNotFoundException` catch. `ForFilename` passes a script-bearing locale through raw, but `SubtitleNaming.Sanitize` is downstream |
| Unbounded `ReadAllText` | Only `SyncStore` :247, :362, both on the plugin's own JSON. No untrusted media file is read whole |
| `PluginServiceRegistrator` | All singletons; ¬captive-dependency lifetime mismatch |
| `PlatformRid` | Returns null for an unsupported arch or OS rather than guessing |
| `SubtitleSlot`, `SubtitleOrigin`, `ISubtitleExtractor` | Declarations only |

---

## 2026-08-15 (twentieth pass) — A12 fixed, and the correlation redesign measured and dropped

Two deferred items taken up before 1.2.5.0. One shipped, one did not.

### A12 **[F]** — a finished sync is no longer discarded over a print

`RunEngineAsync` salvages the scratch output when the run did ¬time out, produced **no** parseable JSON envelope, and left a file whose cue count is within 5% of its input's. Reproduction (`Mr. Inbetween S02E03`, filename carries U+FF1F) writes 455 cues against an input of 455 → accepted; the same file truncated to 90% counts 410 → refused. Ten wasted syncs in one day of field logs recovered.

The four conditions are in `ARCHITECTURE.md`. ! The one worth repeating: a *handled* failure still fails — `{"ok": false}` parses, so this fires only where the CLI died without saying anything at all. Nothing is read out of the broken JSON; the accepted path is the `-o` the plugin itself supplied. The rate bound, the minimum-movement check and the audio check judge a salvaged file exactly as a reported one.

### V9. Adaptive-threshold correlation does not separate **[R]**

`ARCHITECTURE.md` carried a "what would measure them" paragraph describing an unshipped prototype: silence bar at the title's own mean level +12 dB, correlating speech envelope against subtitle envelope in place of edge matching. It fails on both the titles it was meant to rescue and the controls it had to preserve; the paragraph is corrected.

| title | shipping check | correlate | r | margin | /rival |
| --- | --- | --- | --- | --- | --- |
| Twin Peaks FWWM — measures perfectly | Aligned 275ms, 2.52× | −200ms | 0.106 | 0.023 | 1.27 |
| Simpsons S01E10 — unmeasurable | Inconclusive | −500ms | 0.104 | 0.023 | 1.28 |
| TNG S02E02 — measurable | Misaligned 1400ms | 700ms | 0.147 | 0.028 | 1.24 |
| MPFC S01E02 — unmeasurable | Inconclusive | 3450ms | 0.060 | 0.017 | 1.40 |
| Mad Men S02E06 — unmeasurable | Inconclusive | 3750ms | **−0.112** | 0.009 | — |

**No threshold admits row 1 and excludes row 2.** Twin Peaks is the strongest title in the set by the shipping check; Simpsons cannot be measured at any threshold. On every statistic the correlation offers they agree to three decimals. The raw-overlap sweep was replaced w/ a proper normalized coefficient first (theory: the baseline was burying the peak) — separates no better, and `/rival` *inverts*, ranking MPFC (1.40, unmeasurable) above TNG (1.24, measurable).

**It is also biased against the onset method by the width of the whole tolerance.** Cue boxes are shown early and hang past the end of the line → peak envelope overlap sits earlier than the speech onset by an amount depending on the subtitler's timing style, ¬on the sync. `AlignedWithinMs` = 500ms; observed bias 475ms (Twin Peaks), 700ms (TNG).

It does recover *relative* displacement (an injected 1500ms returns on four of five) — but a displacement it cannot certify and a verdict it would bias by half a second are ¬worth having, ∵ the failure is asymmetric: a wrong `Misaligned` **deletes a good sync**, where `Inconclusive` keeps it.

Mad Men is the clearest evidence: its best correlation over the whole sweep is **negative** — the best available alignment anticorrelates w/ detected speech — and that is precisely what a coarse refuse-only variant would have acted on, at 3750ms. Root cause unchanged from A11: these mixes have no silence between lines → no level threshold, adaptive or fixed, isolates speech. Separating them needs spectral VAD, a different piece of work. `verifycheck --correlate` keeps the prototype and prints the normalized coefficient beside the z-score → the negative result stays re-checkable rather than becoming folklore.

### V10. Spectral flux onsets do not rescue them either **[R]**

Independent second idea: read onsets as energy *transients* rather than silence boundaries — a voice starting over a laugh bed steps the level up even though it never lets the level fall. Per-frame RMS from `astats` + `ametadata` at ≈21 ms on the same single decode, band-limited 200–3400 Hz to suppress score and rumble, onsets = crests of the rise. In `verifycheck --flux` w/ `VC_RISE`, `VC_LOOK`, `VC_GAP`, `VC_BAND`; feeds the shipping `Score` so the gate is identical.

It works and does not help. Twin Peaks measures 75 ms as shipped, −1325 ms w/ 1500 ms injected — self-consistent, 100 ms coarser than the silence method, 1.40× against 2.52×. All four unmeasurable titles stay inconclusive across **18 parameter combinations** on Simpsons (rise 4/8/12 dB × lookback 60/150/300 ms × band-limited or not, 349–797 onsets per run). Not one produced a verdict.

Physical rather than parametric: a laugh bed and an orchestral score live *in* the speech band and are loud → a voice beginning over them is ¬a distinguishable transient. Level detection fails on these mixes in both directions — the floor never falls, and the steps are ¬speech.

### V11. The engine already computes the confidence we could not, and prints it **[F]**

`ffsubsync` scores its chosen alignment as (reference speech frames matched w/ subtitle speech) − (reference speech frames matched w/ subtitle silence) over 10 ms frames, on its own **VAD** rather than a level threshold. It prints `score:`, `offset seconds:`, `framerate scale factor:` on stderr of every run — `AssyCliRunner` already captured them and the plugin was discarding them. Measured w/ `agentic/tools/scorecheck`, pairing each video w/ its own subtitle and then w/ one that cannot possibly align:

| pairing | score | shown s | per second | offset | rate |
| --- | --- | --- | --- | --- | --- |
| Twin Peaks FWWM, own subtitle | 366,011 | 2,270 | 161.3 | −0.030 | 1.000 |
| Simpsons S01E10, own subtitle | 106,630 | 957 | **111.4** | −0.930 | 1.001 |
| Sahara, own subtitle | 211,521 | 2,785 | **75.9** | 0.000 | 1.000 |
| Futurama S01E04, own subtitle | 44,530 | 899 | 49.5 | 1.190 | **1.043** |
| TNG S02E02, own subtitle | 60,089 | 1,440 | 41.7 | 0.850 | 1.000 |
| Simpsons S01E10, a foreign subtitle | 14,573 | 1,399 | **10.4** | 31.820 | 1.001 |
| TNG S02E02, S02E01's subtitle | 13,277 | 1,399 | **9.5** | 34.500 | 1.042 |

**Simpsons is the finding.** A title our own check cannot measure at any threshold by either method separates 11× on the engine's score → the engine's VAD sees it; only our level detector is blind. Sahara — A11, the original "cannot be measured at all" title — reads 75.9 at an offset of exactly 0.000 → it was correctly aligned the whole time and nothing available to the plugin could say so. The foreign baseline is steady at ≈10 across two very different titles.

**Futurama S01E04 is the limit.** 49.5 — inside the honest range — while applying a 1.043 PAL stretch on an NTSC DVDRip, which our check refuses and `MaximumRateDrift` refuses independently. ! A high score is **¬**evidence of a correct sync; it is the engine agreeing w/ itself. This is exactly what the independent audio check exists for, and why the score cannot replace it.

→ the score is a **floor, never a warrant**. Shipped as `MinimumEngineScore = 20` in `SyncOrchestrator`: real pairings measure 41.7–161.3 per displayed second, unalignable ones 9.5–10.4 → the bar sits at half the lowest true reading and twice the highest false one. Consulted **only** when the post-sync verdict is `Inconclusive`, and it can **only refuse**: a score above the bar changes nothing, and no score overturns a `Misaligned`. It adds a verdict where there is none today and takes none away. `AssyCliRunner` reads the three numbers off the full stderr *before* the 4000-character tail is cut ∵ the engine prints them well above it.

The divisor is empirical rather than principled — seconds of displayed subtitle, which Simpsons exceeds at 111.4 against the engine's own 100 frames/s → ¬a bounded ratio. It separates by 4× at the narrowest across five titles from a 22-minute cartoon to a 2¼-hour film, which is what the bar needs. Normalizing by media duration from the vendored `ffprobe` would cost a probe per sync and was ¬measured to be better.

---

## 2026-08-15 (nineteenth pass) — pre-release audit for 1.2.5.0: the noise fix

Changed since 1.2.4.0: `SyncVerifier` only, plus two log lines in `SyncOrchestrator`. ¬new filesystem path, process, endpoint, or configuration. Checklist walked against the diff and the invariants it could plausibly touch.

**N1. A drift verdict on a short title is no longer available at all** **[A]**
`DriftWindows = 6` withdraws the rate-error verdict from anything under ≈36 minutes of cues (at the four-window floor each half is two windows). A genuine framerate mismatch on a 22-minute episode is therefore ¬refused by the audio check. It fails open → synced and kept, and `MaximumRateDrift` still refuses an engine rescale no framerate explains — that guard reads the subtitle files and is unaffected by window count.

**Sampling a short title harder was tried and is worse.** `MinimumWindows` raised to 6 so a 22-minute episode has three windows a side:

| | 4 windows (shipped) | 6 short windows | 6 full windows |
| --- | --- | --- | --- |
| Futurama S01E04 candidate, a bogus PAL stretch | Misaligned 1125ms, 1.41× | **Aligned 25ms** | **Inconclusive** |
| Futurama S01E04 original, unmeasurable | Inconclusive | **Misaligned, drift −5975ms** | **Misaligned, drift −5950ms** |

Both variants lose the true positive **and** bring back a drift refusal off half-fits w/ no whole-film answer — V7 exactly. Column 2 shrinks each window to 73s, so the length formula was relaxed to keep 90s in column 3; the outcome does not change → window length is ¬the cause. The half strengths behind those false refusals measure 1.10× and 1.18× against 1.24× for the early half of a real rate error — too thin to gate on. Three windows a side is not enough to fit a half, and no threshold separates it from noise.

**N2. `BestShift` now allocates its sweep** **[A]** — 321 tuples per fit, ≤3 fits per score, 2 scores per target ≈ 2000 stack-sized structs against a check that decodes minutes of audio. Runtimes unchanged across the regression set. Streaming the rival alongside the peak needs two passes or a ring buffer; ¬worth the loss of clarity.

**N3. `Drift` is skipped outright below six windows** — two fits per score disappear on short-form content, the majority of the library by item count → net cheaper than what it replaces.

**Verified clean:**

- **Process spawning** — `Reader` is a pure extraction of the argv already in `OnsetsAsync`. Still `ArgumentList`, still one ffmpeg per window through `FfmpegProcess` w/ its existing timeout and kill path. The filter string is a compile-time constant w/ nothing interpolated.
- **Write scoping / dry run** — the diff adds ¬filesystem call of any kind. `SyncVerifier` reads audio and returns a struct.
- **Rollback** untouched, `rollbackcheck` passes. **Authorization** — no endpoint changed; `VerificationResult` gained a member, serialized nowhere.
- **Logging** — the two new fields are a window count and a ratio. ¬subtitle content.
- **Null safety on the refusal path** — a drift verdict can carry a null `BestShiftMs` (the V7 case). `SyncOrchestrator` reads `DriftMs` whenever `drifting` is true and only falls back to `BestShiftMs ?? 0` otherwise; the non-drift path cannot produce a null. No dereference exists on either branch.
- **`Fit` assigns `strength` on every return** — incl. the `reachable == 0` early exit and the gated null from `BestShift` → `Math.Min(opening, closing)` in `Drift` never reads an unassigned value.

**Regression evidence.** Nine titles measured against their 1.2.4.0 field-log values, each also given a known 1500ms displacement. Every previously-measured title returns **exactly** its logged offset: 12 Angry Men 125ms, 28 Days Later 350ms, 300 250ms, Midnight Diner 125ms, Twin Peaks 275ms, Westworld S01E01 75ms, The Wire S01E01 500ms — the last sitting on the `AlignedWithinMs` boundary and staying `Aligned`. Bambi II stays refused at 1.92×, the Futurama S01E04 candidate at 1.41×. 20,000 Leagues reports inconclusive, and does so w/ the rival test disabled too → ¬a loss. Closest call is The Wire S01E01 at 1.38× against a 1.25× bar — the only measured title inside 0.15 of the threshold; everything else that measures sits at ≥1.9× and everything that does not at ≤1.10×.

---

## 2026-08-15 (eighteenth pass) — the audio check refuses on noise, found in the 1.2.4.0 field logs

Not a checklist pass. One defect class, found by reading the first full scan under 1.2.4.0 and measured against real media w/ `verifycheck --profile`.

**V7. A drift verdict was emitted off two half-fits that had each guessed** **[F]**
33 subtitles refused in the 1.2.4.0 scan, 26 as drifting, and **every one a TV episode; not one film.** Mr. Inbetween S01E01 scored the whole-film fit at null — the confidence gate correctly declined to answer — and was still refused, on a −900ms disagreement between two halves of two windows each. `Score` ran the drift branch before the null check, and the halves are fitted w/ the same gate applied to a quarter of the evidence. Fixed by requiring six windows before a drift verdict is available at all.

**V8. The peak-versus-mean test does not separate signal from noise** **[F]**
`PeakRatio` at 1.4× against the sweep's own mean passes noise routinely ∵ a noise sweep is a field of near-equal local maxima and the best of them clears the mean comfortably. Futurama S01E04 original scored 1.43× and was refused at −3950ms — the extreme edge of the ±4000ms sweep; Mr. Inbetween S01E01 scored 1.50×. ! Raising the bar does not fix it — a genuinely misaligned Futurama candidate scores 2.20×, four percent from Bambi II's 2.32×. Fixed by a second test w/ an actual separation: the winning shift must beat the best shift more than a second away from it by 1.25×. Noise measures 1.04–1.10× there, real misalignment 1.41–1.92×. The halves use 1.1× ∵ a stretched subtitle is smeared across its own error by construction and the full bar refuses every rate error (measured 1.24× on the early half of a 0.04% stretch).

**Two candidate causes measured and rejected.** Onset density: Bambi II runs 26 onsets/min, denser than Mr. Inbetween's 25.8 and Futurama's 21 → density separates nothing. More sampled coverage: windows every 2 min instead of 6 gives a 22-minute episode 10 windows over 15 of its 22 minutes and changes no verdict.

**Validated by displacement, ¬by opinion.** A subtitle is moved a known 1500ms and the check is asked for that number back. Twin Peaks FWWM: 275ms as shipped, −1225ms displaced, 2.5× strength. TNG S02E02: 1400ms and −100ms, 1.3×. Mad Men S02E12, TNG S01E01-E02 (14 windows), Community S01E01, MPFC S01E02, Simpsons S01E10 cannot return a displacement they were handed — unmeasurable, and each was refused under 1.2.4.0. They now report inconclusive → fails open to syncing and keeping.

**A12 root cause, established here** (fix shipped in the twentieth pass). The `UnicodeEncodeError` crashes — 10 in this log — are frozen CPython writing its JSON result to a redirected stdout under the machine's ANSI codepage. A U+FF1F in a Mr. Inbetween filename, echoed back into the result object. ! The obvious fix — `PYTHONUTF8` + `PYTHONIOENCODING` in the allowlisted child environment — was written and **measured not to work**: a PyInstaller freeze runs under an isolated interpreter configuration that ignores every `PYTHON*` variable. Reproduced w/ and without both set: identical crash, same frames (`main:812 → cmd_sync:277 → _emit_json:240 → dump:182 → cp1252 encode:19`). What the reproduction *did* establish is the shape of the real fix: **the sync completes** — stdout carries `{"ok": true, "input":` before the exception and the `-o` file is written in full, 29121 bytes of valid SRT, byte-identical w/ and without the variables. Only the status print dies, and the plugin does not need that JSON.

! **Do ¬set `StandardOutputEncoding` to UTF-8.** Tried and reverted: the child writes the ANSI codepage whenever it *can* encode → declaring UTF-8 would mis-decode every accented path that works today.

---

## 2026-08-15 (seventeenth pass) — the audio becomes the arbiter, and four settings disappear

`MaximumOffsetMs` deleted. It bounded how far a result moved and never asked whether the destination was right — which is how ffsubsync mis-latched Bambi II: a correctly-timed subtitle dragged 1490ms early, well inside the old one-minute bound and therefore accepted.

`SyncVerifier` replaces it, running twice per target off **one** read of the audio. Before the engine it decides whether the subtitle needs syncing at all; after, whether the result may be written. Both fail open — an inconclusive answer syncs, and keeps.

Four settings gone from the config model, ¬merely hidden: `MaximumOffsetMs`, `MinimumOffsetMs`, and the pair the audio check briefly carried (`VerifySyncResult` w/ `PreSyncCheck`, `VerificationToleranceMs`). ! Deleting rather than pinning matters — Jellyfin's deserializer drops unknown keys → a stored value from an older install cannot resurrect a switched-off safety. `AlignedWithinMs` (500) and `MinimumMovementMs` (150) are compiled in, and ∵ the retroactivity checks compare stored numbers against the constants, shipping a different constant re-opens exactly the records it changes.

`SubtitleDeduplicator` renames the survivor of a group to drop the `.0`/`.10` discriminator its duplicates made necessary; `Group` returns singletons so a slot deduplicated on an earlier pass is renamed retroactively. `AutoSyncOnItemAdded` + `RefreshItemAfterSync` default to false.

### Findings in the delta — all **[F]**

| # | Defect | Why it mattered |
| --- | --- | --- |
| V1 | The rename could strip a **digit** marker suffix | `MarkerSuffix` sanitizes to letters/digits/`-`/`_` → `123` is legal and a plugin file is `movie.eng.123.srt`. `CanonicalPath` read that tail as a discriminator → `movie.eng.srt`, removing the plugin's only mark. Discovery would re-sync its own output forever and `RollbackService.Delete` would refuse to remove it. `Canonicalize` now compares `IsPluginOutput` before and after and abandons the rename when the marker would ¬survive |
| V2 | The verifier read a **truncated** silence stream | `FfmpegProcess.RunAsync` kept the last 4000 chars of stderr — right for an error report, wrong for a measurement. A busy window emits more silence lines than that → the earliest onsets were dropped before the fit saw them. `RunAsync` now takes the keep size as a parameter; the verifier asks 512 KB |
| V3 | `BestShift` reported the **low edge** of the match plateau | Every shift within the ±250ms tolerance scores identically; taking the first strictly better shift returned the earliest → a systematic 250ms bias in the direction that makes a good subtitle look early. Now keeps every shift at the peak and returns the middle. Caught by `verifycheck`, which failed all four fitting cases by exactly 250ms |
| V4 | A refusal was stamped on the `Sync` stage | `Fail` funnelled through `SafeUpsert`, which stamped `Sync` → a subtitle the audio refused reported a failed *synchronization*, the one thing that had not failed. `Fail` now takes the stage kind |
| V5 | The downmix was applied **after** the measurement | `-ac 1` is an *output* option → ffmpeg applies it downstream of the filter graph and `silencedetect` read the source layout. On 5.1 it reports silence only where *every* channel is quiet → a continuous music bed in the surrounds hides every dialogue pause and the whole sweep goes flat. Now `aformat=channel_layouts=mono` inside the graph. `check-cue-lead.mjs` had the identical bug |
| V6 | The centre-channel read silently produced **silence** on stereo | The first fix for V5 used `pan=mono\|c0=FC`. On a stereo source ffmpeg fills the missing channel w/ zeros, **exits 0**, and returns one silence spanning the window → the "did it fail?" fallback never fired and every stereo title measured off near-silence (Old Yeller: 13 onsets across 13 windows). Removed rather than repaired — on the one 5.1 title available the centre channel yielded *fewer* usable onsets than the downmix |
| V7 | The hit floor was unreachable under sampling | `BestShift` demanded 25 matching cues, an absolute count, while onsets exist only inside the sampled windows (≈a tenth of a feature). Bambi II peaked at 21 hits out of 46 reachable cues — a textbook 45% peak — and was thrown away as inconclusive. Floor is now `max(12, ¼ of reachable cues)` |
| V8 | A rate error beat the sweep and read as inconclusive | When no single shift fits the film the global fit returns null, and a rate error is exactly that case. `Score` now computes the half-against-half drift first, and a disagreement past `AlignedWithinMs` refuses whether or not the global fit landed |

### Accepted, tracked

**A11. A subtitle needing a rate correction cannot be measured before the fix** **[A]**
Sahara yields no peak at any shift out to ±20s, sampled or whole-track. Running the engine explained it: that subtitle is 26.5s out **and** PAL-rated against film-rate video (1.043 = 25/23.976) → its cues drift minutes away and no shift inside a ±4s sweep can align them. The verifier says inconclusive, the pre-flight check falls through, the engine corrects both, and the post-check scores the result **Aligned at 200ms w/ zero drift** → the failure is open in exactly the direction that matters. ! A subtitle too broken to measure is a subtitle that needs syncing. (Later: V11 shows the engine's own score reads Sahara at 75.9 w/ offset 0.000 — it was aligned all along.)

**A12. `assy-cli` crashes writing its JSON result on non-ASCII output** — root-caused in the eighteenth pass, **[F]** in the twentieth.

**Verified clean:**

- **Process spawning** — the verifier builds its ffmpeg command through `ArgumentList` and runs it through `FfmpegProcess` → inherits the existing timeout, kill-the-tree and cancellation path. ¬new spawn site bypasses either.
- **Cost** — one sample per target shared by both checks; 11–16 seeks of 90s each, measured 2.1–5.5s per title against a full engine run of 30s–3 min. The pre-flight check pays for itself the first time it skips a sync.
- **Dry run** — both checks are inside `RunPipelineAsync`, unreachable w/ dry run on, and only read. The rename is guarded by `!config.DryRunMode` and logs what it would do.
- **Rollback** — `RenamedFromPath` records where the backup has to land, `Restore` removes the renamed copy before restoring under the old name, `Delete` still refuses any file whose name lacks the marker. V1 was the one path that could have broken that proof.
- **Write scoping** — the rename is a `File.Move` w/ no overwrite, abandoned when the target name exists, caught when another writer takes it first.
- **Status endpoint** — new failure categories render through the page's existing `escapeHtml` → engine text containing a media path cannot inject markup.

**Calibration.** Five titles, as shipped and w/ a −1490ms error injected (the exact Bambi II mis-sync):

| Title | Audio | As shipped | Injected |
| --- | --- | --- | --- |
| Bambi II | AVI, MP3 stereo | Misaligned 600ms | Misaligned 2125ms |
| Superbad | MP4, AAC | Aligned 50ms, drift −125ms | Misaligned 1600ms |
| Aladdin | MKV | Aligned 75ms, drift 50ms | Misaligned 1575ms |
| Old Yeller | AVI, mono MP3 | Aligned −100ms, drift −325ms | Misaligned 1375ms |
| Sahara | MKV, DTS 5.1 | Inconclusive (26.5s out, PAL-rated) | Inconclusive |

Every injected error refused, no correct subtitle refused. The Bambi II reading independently confirms a user report of ≈half a second of lag in VLC on that exact file, and it is what calibrates `AlignedWithinMs`: three known-good subtitles measure inside ±100ms, the one that is audibly wrong measures 600ms.

---

## 2026-08-15 (sixteenth pass) — P4 fixed, then the full checklist against the P1–P4 delta

**P4. A dropped leading cue is measured as a constant shift** **[F]**
`MeasureChange` is gone from `SyncOrchestrator`. `SubtitleOffsetProbe` owns `OffsetChange` + `Measure` and measures by **cue identity rather than cue position**: reads every cue from both files, keys each on its text, keeps only keys appearing exactly once on each side, fits `delta(t) = intercept + slope·t` across the matched pairs w/ one refit dropping the worst tenth of residuals. Constant = that line at the input's first cue; rate ratio = `1 + slope`; drift = slope over the input's span.

! `MaximumRateDrift` deliberately stays in `SyncOrchestrator.cs` — `check-rate-bound.mjs` greps that file for it, and moving the constant would silently disarm the check.

**Nothing measured today changes value.** The old endpoint measurement is kept as `Endpoints` and runs when either file is unreadable, when fewer than `MinimumPairs` (8) cues pair, or when the matched span is under `MinimumSpanMs` → a subtitle the matcher cannot handle gets exactly the answer it got before, and no case that previously returned a number now returns null. What changes is the affected 1.6%: Atlantis' four spurious 76628ms rejections become the real shift.

Records carry `MeasurementVersion`, and `SyncStore.Load` re-opens any `Failed` record whose `RejectedOffsetMs` was set by an older rule — ! a rejection measured by a rule that no longer exists is not evidence about the current one. Stamping marks the store dirty so the version persists; without that every restart re-opened the same rejections.

`measurecheck` links the real source, eleven cases. Its mutation note is a real defect the first draft had: `KeyFor` reassigned `tail` while iterating matches whose indices referred to the original string → end-timestamp digits leaked into the key → keys only matched when timings were unchanged → every shifted case fell through to the endpoints, and the fix would have been a no-op in production.

Measured against the live library: on 9 non-stripped backup/output pairs the shipping probe reproduces the recorded shift **exactly on 8**. The one disagreement is `Wallace and Gromit` (recorded 20ms, measured 59420ms), a known-affected file, and it matches the independent Node measurement to the millisecond.

**A1. `TryReadCues` materialized an unbounded media-tree file** **[F]** — `File.ReadAllLines` on a path from the media tree, where the pre-existing `TryGetLastCueMs` on the same class streamed w/ `File.ReadLines`. Now a capped read into a `List<string>` at `MaxLinesRead`. (See S2, twenty-first pass: the cap bounds lines, ¬bytes.)

**A2. `SyncStore.LoadBackup` migrated but did not remeasure** **[F]** — the corrupt-store recovery path called `Migrate` and ¬`Remeasure` → records restored from backup kept a stale `MeasurementVersion` for the session and their old rejections stayed closed. Self-healing on the next restart, but only then. `Remeasure` now runs on both load paths.

**A3. `SettledTwin` scans the store per target** **[A]** — `GetByItemId` is a linear scan that clones its matches. Same class as N5 (fourteenth pass), accepted on the same grounds: the store is one JSON list of a few thousand records, the scan is bounded by the *item* not the library, and it happens once per target against an engine run measured in seconds.

**A4. An adopted record gets no `Sync` stage** **[A]** — `Adopt` copies the outcome fields but does ¬append to `Stages` ∵ no stage ran. `Migrate` synthesizes one from the status on the next load, the same answer, and nothing gates work on `Stages`.

**Verified clean:**

- **`TargetLocks` refcounting.** Retain and Release both mutate `Waiters` under `_lock`; an entry is dropped and its semaphore disposed only at zero. `Lease.Dispose` signals the semaphore *before* releasing its count → the holder's own reference keeps the semaphore alive across the release. A cancelled `AcquireAsync` releases its reference before rethrowing.
- **No lock ordering cycle.** The lease is taken before the `SyncQueue` permit and never the other way; a worker holds at most one lease; a duplicate waiting on a lease holds no permit.
- **Cancellation.** `AcquireAsync` sits outside the `try` → a cancel while queueing propagates without touching the record. Nothing had started, so there is nothing to record.
- **Adoption scope.** `WroteNothing` admits only a measured rejection or a below-minimum skip → a `Synced` outcome is never adopted and no record can claim a sidecar that was not written. The match requires the same source hash, video hash **and** settings stamp.
- **Dry run** — `SettledTwin` + `Adopt` are past the `DryRunMode` return and touch no filesystem in any case. **Process spawning, write scoping, rollback, API authorization** untouched by this delta.

---

## 2026-08-15 (fifteenth pass) — duplicated work in a live scan, and a bad shift measurement

Not a checklist pass. Came out of a production log where one subtitle was synced twice, one item four times, and a 20-minute OCR was repeated. All **[F]**.

**P1. Concurrent producers ran the same target twice** — `FullLibrarySyncTask`, `LibraryEventHandler` and `AutoSubSyncController.SyncItem` all reach `SyncOrchestrator.ProcessAsync`, and nothing stopped two of them holding one target at once. ! The record was read at `ProcessAsync` entry, *before* the `SyncQueue` semaphore wait → `IsStillCurrent` was evaluated against a snapshot that could be ninety seconds stale by the time the pipeline ran. Confirmed in the log: `Dinosaur (2000).eng.1.srt` synced at 09:35:16, engine started on it again at 09:36:44 — an 88-second gap matching queue latency at 8 permits.

M4's `ItemChangeGate` cannot arbitrate this — the scheduled task deliberately does not consult it, and the handler's check ran before the task's `Commit`. This file had recorded the assumption that "the durable correctness gate is still `IsStillCurrent`, so the worst case here is a redundant scan"; the stale read is what made the worst case a redundant *sync* — an engine run and an in-place rewrite.

New `TargetLocks`: one lease per `(ItemId, TargetKey)`, taken before the record is read and held across the queue wait → the read is current for as long as it is used. Refcounted so the map drops an entry when its last holder leaves. The lease is always taken before the queue permit and a worker holds only one → the two cannot cycle.

**P2. A failed OCR was retried on every scan, forever** — M3's shape in a different place. `CaptureFingerprint` ran *after* the Convert stage → an OCR failure returned w/ `VideoPartialHash` still null, `FingerprintMatches` false, and `IsExhausted` structurally unable to match. `Gravity`'s bitmap track timed out after twenty minutes and was queued to do it again on the next scan, holding one of eight permits each time. `CaptureFingerprint` now runs before the first stage that can fail; it never needed the converted path — an embedded target returns on the video hash alone and an external target always has a `SubtitlePath`.

**P3. Identical sidecars each paid a full engine run** — `Atlantis: Milo's Return` carries four byte-identical sidecars (one SHA-256 across `.eng.srt`, `.eng.0/1/2.srt`) → four 20-second engine runs all reaching the identical rejection. `SubtitleDeduplicator` cannot collapse them: `ToCandidate` requires `Synced` or `Skipped`, all four are `Failed`, and the slot is poisoned by design. `SettledTwin` now adopts the outcome of another record on the same item w/ the same source hash, video hash and settings stamp. ! Restricted to outcomes that wrote nothing **and** came from a measurement — a rejected offset or a below-minimum skip. A tool failure or timeout is never adopted ∵ that can be transient and deserves its retry.

**P4 context** (fixed in the sixteenth pass): `MeasureChange` took the constant offset as `|firstCueAfter − firstCueBefore|`, assuming the engine preserves the first cue. Subtitles converted by common tooling carry a **zero-duration marker cue** at t≈0 holding the source framerate, and `ffsubsync` drops it → the measurement compared the marker against the first real line of dialogue and reported the gap as a shift that never happened. Atlantis is the whole of it: first cue `00:00:00,041 --> 00:00:00,041` containing `25.000`, first dialogue at `00:01:19,788`, reported movement 76628ms against a 60000ms limit. **Four of the six offset-limit rejections in the entire store are that one film's four copies**, every one spurious. A library scan found the pattern in 18 of 1161 source subtitles (1.6%); the markers were `25.000`, `***`, `_`, and bare musical notes.

**Validation.** `check-sync-output.mjs` compares a shipped subtitle against its vault backup: cue identity, fitted shift + rate, monotonicity, dialogue retention through a hearing-impaired strip, last cue against the video's real duration via the vendored `ffprobe`. Twenty-two titles checked against `records.json`; every recorded shift reconciled, **including all seven hearing-impaired strips** — `300` (183 marked cues → 0), `Narcos` (224), `Twin Peaks` (146), `Scooby-Doo` (97), `Kids Next Door` (74), `Futurama` (28), `Nathan For You` (11) — dialogue retained, ¬reordered or negative cues anywhere. Rate corrections cluster on 4.27% (25/23.976) and 4.17% (25/24), the conversions those bounds exist to admit.

Two results worth knowing independently of P4: `Top Gear: Apocalypse` ends its subtitles 32 min past the video's 73-min runtime and `Pete's Dragon` ends 32 min early against 128 min — both look like a subtitle from a **different cut**, which the plugin cannot detect and happily retimed. `The Wire` and `Drake and Josh` were inconclusive; their strips rewrote too much text for the matcher to pair enough cues.

---

## 2026-08-15 (fourteenth pass) — full checklist against the thirteenth-pass delta

Ten checklist items against the thirteenth-pass files plus `ItemChangeGate`, `SubtitleDiscoveryService`, `AutoSubSyncController`, `SubtitlePlacer`, `RollbackService`, `SyncStore` re-read in full.

| # | Defect | Why it mattered | Status |
| --- | --- | --- | --- |
| N1 | A VobSub pair named by its **index** half was reported unreadable | `ImageSidecarLabel` recognised `.sup`/`.sub`, but Jellyfin can register a VobSub pair w/ the stream path pointing at the `.idx` → fell through to "The sync engine does not read .idx subtitles". ! The message reads like an engine limitation; the real effect is `RequiresOcr` was never set → **enabling OCR would not have picked the track up**, so the user's remedy for the message they were shown did nothing. Fixed at *discovery*: `ResolveSidecarPath` maps an `.idx` onto the `.sub` beside it → one code path handles VobSub however Jellyfin named it. An `.idx` w/ no `.sub` stays unresolved and unsupported (an index w/ no bitmaps is genuinely unreadable) | **[F]** |
| N2 | Two streams naming one file produced two candidates | M1's root cause one level upstream and still live: `Discover` had no duplicate guard. M1 fixed the consequence inside the deduplicator; this fixes the source, and covers the case N1 introduces. `Discover` now keeps one candidate per resolved path; embedded targets have no `SubtitlePath` and are untouched | **[F]** |
| N3 | The manual sync endpoint left the refresh loop open | `SyncItem` wrote and triggered a refresh but never called `ItemChangeGate.Commit` → the resulting `ItemUpdated` reached the handler as a genuine change. Converges (the handler commits at the end of its own pass) but costs one avoidable pass per manual sync, incl. a full SHA-256 of each subtitle over the media filesystem | **[F]** |
| N4 | The gate held raw signatures | ≈700 bytes for a four-sidecar item against a 20,000-item bound ≈ 14 MB of live strings, for data only ever used in an equality test. Now a SHA-256 digest — same comparison, ≈a fortieth of the memory | **[F]** |
| N5 | `SyncStore.UpsertLocked` is a linear scan | `FindIndex` over the whole list per upsert. Unmeasurable at the ≈1,850 records this library produces, but O(n) per write against O(n) writes per scan → a 50,000-record library pays ≈700× the comparisons. A dictionary on `(ItemId, TargetKey)` alongside the list would make it constant time. ! Not changed ∵ the store is the durability boundary and the current shape is simple, correct, and provably fast enough at the sizes in use | **[A]** |
| N6 | `GetAll` deep-clones every record on every status poll | Correct — handing out live records would race the orchestrator — but a full deep copy incl. `Stages` to compute a handful of integers, walked six times for counts. A counts-only store method would avoid it. The page only polls while it is open | **[A]** |

! N1 widens what `SubtitlePath` can point at → write paths re-checked. A VobSub target w/ OCR off carries `UnsupportedReason` and returns at `SyncOrchestrator.cs:84` before placement; w/ OCR on it carries `RequiresOcr`, which `SubtitlePlacer.Place` requires to be false before overwriting. The `.sub` cannot be written over on either branch.

**Verified unchanged** — no shell strings (the new code spawns nothing) · no client-supplied paths (`ResolveSidecarPath` derives from a Jellyfin-supplied stream path by extension substitution in the same directory, never from request input) · backup gates every destructive step · unlinking scoped (the new code deletes nothing) · dry run (`ItemChangeGate` reads the filesystem and writes nothing; `DryRunMode` is inside `OutcomeStamp` → leaving dry run invalidates every stored signature and reopens the library, confirmed by a `gatecheck` case) · authorization · comment lint clean.

---

## 2026-08-15 (thirteenth pass) — post-1.2.1.0: five defects found in production logs

Not a checklist pass. Came out of reading a live full-scan log against a real library. ! Three were **mutually disguising**: the deduplicator's self-delete manufactured the "failed sync" rows, which manufactured the permanent retry, and the refresh loop was re-running the whole thing on top.

| # | Defect | Why it mattered | Status |
| --- | --- | --- | --- |
| M1 | The deduplicator deleted a file as its **own** duplicate | `Group` built one `Candidate` per *target*. When `GetMediaStreams` returns the same external path twice, two targets share a `Key`, resolve to the same `SyncRecord`, and become two distinct `Candidate` objects naming one file → `ReferenceEquals` is false, similarity scores 100%/100%, and the file is deleted as a duplicate of itself. Confirmed on disk: `Lilo and Stitch 2 (2005).eng.0.srt` and `Fun and Fancy Free (1947).eng.srt` were gone. Two independent guards: a `seen` path set in `Group`, and a path-equality test alongside `ReferenceEquals` in the removal loop — either alone closes the observed case; both kept ∵ they fail differently. The vault copy meant nothing was unrecoverable | **[F]** |
| M2 | A backup that could not be taken was reported as a failed removal | Second-order M1: pass 2 called `_vault.Store` on a file pass 1 had deleted, got null, logged "Backup failed … leaving the duplicate in place". The gate behaved correctly — no backup, no delete — so never a data-loss risk, only a misleading count. Closed by M1 | **[F]** |
| M3 | A deleted sidecar was retried on every scan, **forever** | `Remove` leaves `Status = Synced` w/ `OutputPath` naming the deleted file → `IsStillCurrent` fails on the missing file, the engine is handed an input that is not there, and `IsExhausted` can never match ∵ its fingerprint needs that same missing file to hash. The record was structurally incapable of settling. Six items in one scan. `RunPipelineAsync` now checks an external target's sidecar exists between `IsStillCurrent` and extraction and records `Skipped`, a stable state | **[F]** |
| M4 | `RefreshItemAfterSync` fed the library event handler **its own output** | Every write queues a metadata refresh; Jellyfin answers w/ `ItemUpdated`; the handler treated that as a fresh change. The 30s debounce collapses a burst but ¬a refresh arriving later → a full scan grew a second wave of event-driven syncs behind itself, each paying a full SHA-256 of the subtitle and a 128 KB partial hash of the video over SMB to conclude nothing had changed. ! That wave runs under `_shutdownCts`, outside the scheduled task's control — most of why stopping the task appeared not to stop anything. Fixed w/ `ItemChangeGate`: a stats-only per-item signature checked before discovery on the event path and committed by both entry points *after* processing → the refresh a write provokes compares equal. In memory by design; the durable correctness gate is still `IsStillCurrent` | **[F]** |
| M5 | A scheduled-task stop does not reach event-driven syncs | `LibraryEventHandler` runs under `_shutdownCts.Token`, tripped only by `StopAsync` on server shutdown. `AutoSubSyncController.SyncItem` is worse — `CancellationToken.None`. Neither is reachable from the scheduled task's token → stopping a full scan leaves any sync those paths started running to completion. ! ¬a kill-path fault: `AssyCliRunner` + `FfmpegProcess` both reap their process trees correctly on cancellation; nothing cancels them. M4 removes the large majority of the volume. Fixing it properly means a shared cancellation source that a stop trips for every origin → changes what "stop" means for work the user did not start w/ the task. **Deferred as a behavioural decision, ¬difficulty** | **[F]** ↓ 23rd |

! M4's recorded assumption — "the durable correctness gate is still `IsStillCurrent`, so the worst case here is a redundant scan" — was falsified by P1 (fifteenth pass): the stale pre-queue read made the worst case a redundant *sync*.

---

## 2026-08-14 (twelfth pass) — pre-release audit for 1.2.1.0

Full checklist against the eight files changed since `f19bb07`, read as a diff and then in context. The parallel scan is the release's main risk surface → races and write scoping got the most attention. All four fixed before the release.

**H1. `MaximumRateDrift` rejected the widest legitimate framerate conversion** **[F]**
`MaximumRateDrift = 0.25` w/ the comment "30/25 is the widest at 20%" — that enumerates the PAL pairs and misses the NTSC film-to-broadcast family entirely. Frame rates are exact rationals (23.976 = 24000/1001, 29.97 = 30000/1001):

| conversion | ratio | drift | verdict at 0.25 |
| --- | --- | --- | --- |
| 23.976 → 30 | 1.251250 | **25.125%** | **rejected** |
| 24 → 30 | 1.250000 | 25.000% | on the boundary |
| 23.976 → 29.97 | 1.250000 | 25.000% | on the boundary |
| 24 → 29.97 | 1.248751 | 24.875% | admitted |
| 25 → 30 | 1.200000 | 20.000% | admitted |

The test is `Math.Abs(ratio - 1) > MaximumRateDrift` → an exact 1.25 passes, but the ratio is computed from integer-ms spans so the measured value lands either side arbitrarily. Two of the five widest legitimate conversions are decided by **rounding**, and one is refused outright. This is G2's failure shape recurring: a bound chosen against an incomplete list of the conversions that actually occur. Fixed at **0.30**, which admits all twenty pairs from {23.976, 24, 25, 29.97, 30} and still rejects both adversarial shapes from the G2 table (201.01%, 49.49%) w/ room to spare; the eleventh pass's boundary table re-run at the new bound keeps every verdict. Pinned by `check-rate-bound.mjs` → `CLAUDE.md`. ! Re-pointed at 0.25 it fails w/ exactly the three conversions named above — which is what makes it a test rather than a restatement.

**H2. `AppliedOffsetMs` was persisted and never read** **[F]** — written on five paths, consumed nowhere; in every row of `records.json` w/ no reader. Added for "log sync success and by what threshold", which the log line already delivered. Fixed by *surfacing* it: `GetStatus` returns `MedianAppliedOffsetMs` over kept runs and the config page renders a "median shift" stat card.

**H3. Parallel OCR targets took a transient `Unsupported` during the seconv download** **[F]** — `ConvertAsync` marked a target `Unsupported` whenever `EnsureOcrReadyAsync` was not ready, and `ClaimAttempt` allows one fetch per 15 min → when several workers reach an image track before the 40 MB converter lands, one downloads and the rest read `Fetching`/`Unavailable`. Sequentially impossible (the first target awaited the fetch). Self-heals ∵ `Unsupported` is ¬terminal and `IsExhausted` tests only `Failed`. Fixed by making readiness say *which* it is: both `ISeConvRunner` readiness calls return `ToolUnavailable?` carrying `IsTransient` from `PayloadReadiness.Fetching`, and `ConvertAsync` leaves a transient target `Pending`. The Tesseract-missing branch reports `Ready` for the payload → correctly never transient.

**H4. The "half your cores" ceiling counted syncs, ¬processes** **[F]**
A sync is not one process — ffsubsync runs its engine under `multiprocessing`, and `killcheck` measured four descendants for a single sync. Until 1.2.1.0 the scan held one permit and never exercised the ceiling → the promise was never tested. ! **The larger multiplier turned out ¬to be `multiprocessing` at all.** ffsubsync's correlation runs on numpy, whose BLAS backend sizes its thread pool from the core count, and `NUMBER_OF_PROCESSORS` was on `AssyCliRunner`'s pass-through allowlist → on Windows every sync was handed the host's full core count and built a thread pool to match. **One permit could occupy the whole machine.** Fixed by pinning the child to a single thread: `OMP_NUM_THREADS`, `OPENBLAS_NUM_THREADS`, `MKL_NUM_THREADS`, `NUMEXPR_NUM_THREADS`, `VECLIB_MAXIMUM_THREADS` all `1`, set **after** the pass-through loop so an inherited value cannot win. A permit now costs ≈one core, which is what `MaxConcurrentSyncs` always claimed to be counting. A single sync gets slower in exchange; concurrency is this plugin's parallelism axis and `AdaptiveConcurrency` measures the outcome. Remaining descendants are one `multiprocessing` worker and an ffmpeg doing audio demux, neither CPU-hot for long. ¬measured under saturation — worth re-checking on the next `assy-cli` pin bump.

**Verified clean** — races under the parallel scan (`SyncStore` serializes every read and mutation behind one lock and hands out clones · `SubtitlePlacer`'s global `_gate`, written for two tracks of one video, covers cross-item parallelism unchanged · `BackupVault` writes under a per-record directory · `AdaptiveConcurrency` fully locked · `SyncQueue._inFlight` interlocked · `Discover` pure per call · `SubtitleTarget` built fresh per item so the `IsHearingImpaired` mutation in `TransformAsync` is ¬shared · scratch filenames are GUIDs) · payload download stampede (`ClaimAttempt` is a lock-protected read-modify-write → concurrent workers produce exactly one fetch; this is H3's cause, ¬a second bug) · process fan-out still bounded, `SyncQueue` the only admission point and the scan's `MaxDegreeOfParallelism` the gate's own ceiling rather than a competing limit · dry run returns ahead of the queue and every write · cancellation (`Parallel.ForEachAsync` throws `OperationCanceledException` unwrapped for the token it was given → the flush-and-rethrow path is unchanged) · the delta adds no spawn, path handling, deserialization or endpoint.

**Dismissed** — **Two library items sharing one video path** (possible if one location is added to two libraries: both would sync the same sidecar and the second's "backup" would capture the first's output). Pre-existing and unchanged by parallelism — the placer lock serializes the moves and the sequential loop had the identical outcome. ¬a 1.2.1.0 regression · **Out-of-order progress** — `Interlocked.Increment` yields distinct values that racing workers can report out of order. Cosmetic; the counter is monotonic and the task still ends at 100 · **Unconditional `MeasureChange`** — two extra subtitle-file reads on a server w/ both bounds at 0. Deliberate; the success log needs the numbers, and it is negligible beside a sync that read the whole video.

---

## 2026-08-14 (eleventh pass) — post-1.2.0.0: the concurrency ramp and the offset gate

Three findings from reading 1.2.0.0's runtime *behaviour* rather than its diff. All arose from questions about what the plugin actually does, ¬from the checklist. All **[F]**.

**G1. Adaptive concurrency ramped to its ceiling on a sequential workload**
`Decide` computed throughput as `_level / meanMsPerGigabyte`, using the *permitted* level as a stand-in for how much work was actually running. `FullLibrarySyncTask` awaits each sync in turn → exactly one job in flight during a scheduled scan → raising the level left `meanMsPerGigabyte` unchanged while doubling the numerator: level 2 read as `2/M` against level 1's `1/M`, cleared the 10% margin, and climbed every step to `AutoConcurrencyFor`.

! The consequence was ¬slow scans. A long scan parked the level at the ceiling **on evidence that never existed**, and the next burst of library-event syncs — which *are* concurrent — inherited that level w/ nothing having measured it. That is the unbounded-load case the component exists to prevent. Fixed by measuring realized concurrency: `SyncQueue` reports the in-flight count at admission and `Decide` uses `meanObservedConcurrency / meanMsPerGigabyte`. A sequential caller now reads flat, and the existing *a flat result settles low* rule settles it at 1. Proven by a `Sequential caller` case in `simulate-concurrency.mjs` whose `achieved` returns 1 regardless of level — before: 2/3/4/6 pinned at the ceiling in 200/200 runs, 8 at 8 in 168; after: 1 in 200/200 at every ceiling. The four pre-existing storage profiles converge to the same bands → this corrects the sequential case without retuning the saturated ones.

**G2. The maximum-offset gate rejected legitimate framerate corrections**
Found in a user's scan log. Three targets failed the 120,000 ms limit:

| title | rejected shift | last cue | shift / last cue |
| --- | --- | --- | --- |
| 20,000 Leagues Under the Sea (1954) | 311,050 ms | 02:01:32 | **4.265%** |
| Top Gear: Apocalypse | 278,014 ms | 01:40:40 | 4.603% |
| 101 Dalmatians II | 180,596 ms | 01:13:23 | 4.101% |

`25 / 23.976 − 1` is **4.271%**. ¬wrong-audio latches — PAL-sourced subtitles against film-rate video, and ffsubsync's rate correction was almost certainly right. Cause was an interaction the tenth pass introduced: F6 redefined `MeasureShift` as the *larger* of the displacement at each end (correct for catching a blowup) but `MaximumOffsetMs` kept its 120,000 ms default, chosen when the measurement meant a constant shift. Under a rate correction the end-of-file displacement is `runtime × ratio` → the gate rejects everything past `120000 / 0.0427` ≈ **47 minutes**. ! Every feature-length title needing a framerate fix refused **by construction**, and a framerate mismatch is the most common desync there is.

Fixed by splitting the measurement: `MeasureChange` returns `ConstantMs` (displacement of the first cue), `DriftMs` (change in the first-to-last span) and `RateRatio`. `MaximumOffsetMs` tests only the constant, a new `MaximumRateDrift` tests the ratio, and the minimum-offset skip tests `max(ConstantMs, DriftMs)` so a pure rate correction still counts as real work. A ratio is computed only when the input spans ≥`MinimumSpanMs` (one minute). ! The two bounds are **complementary**, which is what makes splitting strictly better than the maximum: a wrong-audio latch shows as a large constant, a rate blowup as an implausible ratio, and neither hides inside the other.

| case | constant | rate | verdict |
| --- | --- | --- | --- |
| 20,000 Leagues / Top Gear / 101 Dalmatians (PAL, real) | 200 ms | 104.27% | accept *(was reject)* |
| PAL plus a genuine 3 s shift | 3,000 ms | 104.27% | accept |
| ordinary 4 s shift | 4,000 ms | 100.00% | accept |
| already in sync | 10 ms | 100.00% | skip |
| wrong-audio latch, 5 min constant | 300,000 ms | 100.00% | **reject** |
| rate blowup, first cue pinned | 0 ms | 201.01% | **reject** |
| rate collapse, half length | 0 ms | 49.49% | **reject** |
| 30 s clip | 2,000 ms | not evaluated | accept |

The last rows matter: the blowup F6 was created to catch is still caught, now by the rate bound rather than by a maximum that also swept up every framerate fix. The three rejections already on disk recover without "clear database" ∵ F5 makes a limit-driven rejection retry once the limit moves. `MaximumOffsetMs` was then lowered 120,000 → **60,000 ms**: two minutes was chosen when the measurement conflated shift and stretch; now the rate bound handles stretch, so the constant-shift limit can say what it means — a subtitle a whole minute out is a latch onto the wrong audio, ¬a subtitle. (Both settings deleted entirely in the seventeenth pass.)

**G3. The full library scan ignored the concurrency setting entirely**
`ExecuteAsync` swept items w/ `for` + `await` → exactly one sync ever in flight during the scheduled scan → `MaxConcurrentSyncs` governed only `LibraryEventHandler` and the manual endpoint, never the one workload where throughput is the whole point. Also G1's root cause. Fixed w/ `Parallel.ForEachAsync` at `MaxDegreeOfParallelism = Clamp(ResolveMaxConcurrentSyncs(), 1, SyncQueue.HardMax)`, progress from an `Interlocked` counter. ! Each item's own targets stay **sequential** ∵ `SubtitleDeduplicator.ProcessItem` compares that item's outputs against each other and needs all of them settled. The degree of parallelism is deliberately the *ceiling the gate could admit*, ¬`HardMax` and ¬enforcement — `SyncQueue` remains the single gate. Offering fewer items than the ceiling would cap the gate below its own limit; offering more parks threads on the semaphore while they hold pre-queue work (`IsExhausted`'s fingerprint hashing reads the video file outside the gate). `AutoConcurrencyFor` also lost its `<= 4 cores` special case → `Clamp(cores / 2, 1, 8)` throughout; the special case predates the scan being parallel and made the automatic setting contradict its own description on a quad-core box.

---

## 2026-08-14 (tenth pass) — pre-release audit: the single-engine collapse

Scope: the collapse from an engine chain to `ffsubsync` alone, the removal of `.sub`/MicroDVD from `SupportedExtensions`, the SDH detector, and the new maximum-offset gate. Full ten-item checklist.

| # | Defect | Note | Status |
| --- | --- | --- | --- |
| F1 | Three harnesses had been **uncompilable since 1.1.2.0** | `storecheck`, `placecheck`, `payloadcheck` all failed `CS0246: PluginPaths could not be found`. The 1.1.2.0 fix moved path derivation into a new root file and every harness linking `SyncStore`, `BackupVault` or `PayloadStore` needed it linked too; none got it. ! These three guard the payload traversal check, the backup-before-overwrite gate and store durability — **the destructive paths** — and they were dead through a shipped release | **[F]** |
| F2 | `verify.ps1` never ran a harness | The reason F1 survived a release. ! A harness that stops compiling against the code it guards is indistinguishable from one that passes. `verify.ps1` gained a harness section w/ `-SkipHarness`; `synccheck`, `formatcheck`, `killcheck` stay manual ∵ they need media or a staged payload | **[F]** |
| F3 | Two comments described the removed engine chain | The rethrow they annotate is still correct and needed; only the stated reason was stale | **[F]** |
| F4 | `MeasureShift` parsed both files even when neither bound was enabled | Two file reads per sync, ¬a hot loop. Moot — both bounds deleted in the seventeenth pass | **[F]** |
| F5 | A maximum-offset rejection was **permanent** | `IsExhausted` was `Status == Failed && FingerprintMatches(...)` and never consulted the configuration → raising `MaximumOffsetMs` never re-tried what the old value rejected, and the only recourse was "clear database", which also destroys rollback. ! A configuration-derived rejection was being remembered as an engine failure. This is the shape later generalized as `MeasurementVersion` (sixteenth pass) | **[F]** |
| F6 | The maximum-offset gate read **one cue** | Accepted for the minimum-offset skip (A9) where a wrong answer leaves a subtitle alone; load-bearing for a *safety* gate a wrong answer destroys a subtitle. A pure rate correction that leaves cue one near its original position and drags the last cue by minutes passed cleanly. Fixing it caused G2 | **[F]** |
| F7 | `IsExhausted` took a `config` it never read | Leftover from when exhaustion was compared against `MaxAttempts` | **[F]** |
| F8 | `Normalize()` did not cross-validate the two offset bounds | Nothing rejected `Minimum > Maximum` → every result either `Failed` (above the max) or `Skipped` (below the min), **no window in between**, no subtitle ever written, and nothing saying why | **[F]** |

**Dismissed** — **The VobSub/`.idx` discrimination is ¬dead code.** `SubtitleDiscoveryService` reads `.sub`, no longer in `SupportedExtensions`, but `ImageSidecarLabel` is called *before* the allow-list check: a `.sub` w/ a sibling `.idx` is labelled VobSub and routed to OCR; a bare `.sub` returns null and falls through to the allow-list, which names it unsupported instead of handing MicroDVD to an engine that cannot read it. Both branches live. · **seconv's silent refusal to overwrite is unreachable from the plugin.** Given an `--outputfilename` that already exists, `seconv` leaves the file alone, writes nothing, **exits 0**. `ScratchPath` mints a fresh GUID per call → no caller can reach it. ! Recorded in `ARCHITECTURE.md` ∵ the GUID is now load-bearing.

---

## 2026-08-12 (ninth pass) — pre-release audit for 1.1.2.0: the plugin data location

Scope: every path the plugin derives from `IApplicationPaths`, plus the config page's save path. Triggered by a user report that the plugin's settings, its payloads and its data folder were all gone after each restart.

**E1. Writing plugin data under `PluginConfigurationsPath` made Jellyfin delete every plugin's configuration on restart** *(data loss, critical)* **[F]**
`SyncStore`, `BackupVault` and `PayloadStore` all rooted at `<PluginConfigurationsPath>/AutoSubSync`. That path is inside `plugins/`, which `PluginManager.DiscoverPlugins` enumerates; `TryGetPluginDlls` then globs `*.dll` **recursively** through each candidate. The unpacked `assy-cli` PyInstaller freeze supplied hundreds of native DLLs → `plugins/configurations` became a plugin candidate that failed to load, was marked `Malfunctioned` w/ a `meta.json` written into it, and on the next start was removed w/ `Directory.Delete(path, true)` — **taking the configuration of every plugin on the server.**

Verified against the `v10.11.11` tag, ¬the branch head. ! ¬a Jellyfin regression: the enumeration and the recursive glob are long-standing and inert until something puts a directory containing DLLs in there. **This plugin armed it.** Fixed by `PluginPaths`, a DI singleton rooting everything at `<DataPath>/AutoSubSync` which in its constructor `Directory.Move`s the legacy tree across and deletes any stranded `meta.json`. ! The **move** is deliberate — an early revision of this fix deleted the legacy directory outright, which would have destroyed the user's backup vault and record store along w/ it. All releases before 1.1.2.0 were withdrawn from `manifest.json` and deleted from GitHub ∵ every one carries the defect.

**E2. The settings page saved a form it had failed to populate** *(data loss, moderate)* **[F]**
`load()` left the form at its markup defaults when `getPluginConfiguration` or `getVirtualFolders` failed — every checkbox unchecked, every text input empty — and left the submit button armed. `save()` reads each field from the DOM unconditionally → one save after a failed load wrote `ConvertImageSubtitles=false`, `RemoveHearingImpairedTags=false`, `DeduplicateSubtitles=false`, an empty language allow-list and an empty library selection. Fixed w/ a `configLoaded` flag gating `save()` and disabling submit until a load has fully populated the form. ! Initially and **wrongly** diagnosed as the cause of the reset, before a log line showing an already-installed payload reported as *"not downloaded yet"* ruled a config-page write out entirely.

---

## 2026-08-12 (eighth pass) — pre-release audit for 1.1.1.0: config persistence and the status panel

Scope: the 1.1.1.0 change set — `PluginConfiguration` and its new array-typed collections, `GetStatus` and its two summarizers, `SeConvRuntime`/`SeConvRunner` after the readiness split, `SyncToolCapabilities`, the `MaxAttempts` removal, and `configPage.html`. Full ten-item checklist.

**D1. Nothing truncates a chain containing repeats now that `MaxAttempts` is gone** **[F]** — `SelectChain` applies `Distinct(OrdinalIgnoreCase)`. ! **This finding was first written w/ a false premise and is corrected rather than deleted.** It claimed the `XmlSerializer` collection-append bug had *persisted* a repeated chain into stored configuration. Neither part is true: the duplication was a **load-time artifact only** — `XmlSerializer` appended stored elements onto the property initializer's, so the *in-memory* list had four entries, but the only write path is `Plugin.UpdateConfiguration`, which calls `Normalize()`, which already applied `Distinct`. Every save collapsed it back, the on-disk value was never anything but the distinct set, and it never compounded. Behaviour was correct too ∵ 1.1.0.0 ran `.Take(Math.Max(1, MaxAttempts))`. The bug was visible in the settings box and nowhere else. What survived was narrow: removing `MaxAttempts` removed the `.Take()`, so a chain that *does* contain repeats would run them, obtainable only by hand-editing the XML. **Moot 2026-08-14** — `SyncToolChain` was retired w/ the engine chain.

**D2. `Normalize()` dereferences collections the API can set to null** **[F]** — `UpdateConfiguration` calls `Normalize()` on an object deserialized straight from the request body, and `System.Text.Json` assigns `null` to an array-typed property for `"SyncToolChain": null` → `NullReferenceException`, 500, configuration unsaved. Pre-existing (`List<T>` had the same exposure) and reachable only w/ elevation, but this release rewrote all three properties → fixed rather than carried. All three null-coalesced at the top of `Normalize()`.

**D3. Stale log message after the attempt budget was removed** **[F]** — "is out of attempts and unchanged" described a mechanism that no longer exists, to someone reading the log to understand why a subtitle was not retried.

**D4. `Status` clones the entire record store on every poll** **[A]** — `GetAll` deep-clones every record and its stage list under the same lock the sync workers write through, and the page calls it every 2s while a sweep is running — the moment the store is busiest. Mitigated by choosing 2s over 1s for the busy interval. Real fix is a counts-only aggregation on `ISyncStore`. (Restated as N6.)

**D5. Tesseract is resolved twice per status poll** **[A]** — `GetConverterStatus` walks every `PATH` entry plus six probe directories and `SummarizeDependencies` calls `ResolveTesseractDirectory` again. ! Reusing the first result looks free but is **wrong**: `GetConverterStatus` returns a null directory whenever the *converter* payload is missing → a server w/ Tesseract installed but seconv not yet downloaded would be told Tesseract is missing. Correctness beats two `stat` sweeps.

**Verified clean** — process spawning unchanged (taking the resolved status as a parameter did not change how the process is launched, timed or killed) · the readiness split means `RunAsync` is gated by the status its *caller* resolved → the OCR path cannot be entered w/ a converter-only check and the hearing-impaired path cannot demand Tesseract · write-scoping inventory unchanged from the seventh pass · both dry-run gates intact and ahead of all filesystem work · authorization · every value interpolated into `innerHTML` passes `escapeHtml`, incl. the new dependency `Name` and `Message` · `UnsupportedReasons` groups by `record.Message`, all from a fixed set — no engine stderr reaches that field · one `setTimeout` re-armed from the response handler rather than an interval, cleared on `pagehide` and `visibilitychange` · no reference to `agentic/` anywhere under `Jellyfin.Plugin.AutoSubSync/`.

---

## 2026-08-12 (seventh pass) — pre-release audit for 1.1.0.0: subtitle deduplication

Scope: deduplication and everything it touches. Full nine-item checklist w/ weight on write scoping and rollback correctness ∵ this is the **second** code path in the plugin that removes a user's file. All five fixed in-pass. (! IDs collide w/ the eighth pass's D1–D5; these are the deduplication set.)

| # | Defect | Note |
| --- | --- | --- |
| D1 | Rollback **restored** plugin-created duplicates instead of deleting them | `Remove` set `Provenance = Superseded` unconditionally and `Undo` maps `Superseded` to restore → a duplicate the *plugin* created (an embedded extraction, an OCR result) came back into the media folder on "Roll back everything", and again on every subsequent rollback ∵ the record still said `Superseded`. ! The **common** path, ¬an edge case: `ChooseKeeper` sorts `OrderBy(IsPluginFile)`, so the file removed is preferentially the plugin's own. Promotion is now conditional on the record being `Retimed`; a `Created` record keeps its provenance and `Delete` returns `Skipped` |
| D2 | The vault gate did ¬copy the bytes it was gating | `BackupVault.Store` returns an existing destination rather than overwriting one, and a `Retimed` record already holds a vault entry under the removed file's own name → dedupe's `Store` copied nothing, returned the old path, and **the gate passed on a backup of different content**. ¬data loss (the original is the better thing to restore) but the invariant in `CLAUDE.md` claimed a copy that did not exist. `Store` now takes an optional label and dedupe passes `duplicate`; `BackupPath` is assigned only when null so the original remains what rollback restores |
| D3 | Deduplication was invisible outside the server log | `ProcessItem` returned a report both call sites discarded and `/Status` had no counter. ! Bit hardest in **dry run**, whose entire purpose is inspecting what *would* happen. Now a `SubtitleStageKind.Deduplicate` stage |
| D4 | Every comparison re-parsed both files | Scoring one pair cost four passes; a group of *n* cost `4(n−1)` w/ the keeper re-parsed per candidate. Added `SubtitleProfile.Read`. `dedupecheck` scores identically before and after → a refactor rather than a change |
| D5 | The cue and formatting readers had no line bound | `MaxLinesScanned` (4000) applied only to `HasCues`; `ReadLinesSafe` was unbounded. Bounded at `MaxLinesRead` — far past a dense ASS fansub at ≈30,000 lines. (See S2: the cap bounds lines, ¬bytes) |

**Verified clean** — dry run is still a hard lock (`Remove` is the only writer to the media filesystem and sits behind the check) · deduplication spawns no process, adds no endpoint, accepts no client input; every path it touches is `record.OutputPath`, derived server-side · **group poisoning is conservative**: any target in a slot whose record is missing, ¬`Synced`/`Skipped`, absent from disk, or unparseable abandons the whole slot → an unsynced copy is never compared against a synced one · a store failure cannot abort the sweep (`MarkStage` catches around `Upsert` — B1's lesson applied to the new call site) · **formats are never merged**: `FormatKey` requires the same format *and* extension and returns null for anything unrecognized → a `.srt` and an `.ass` score zero on both axes.

Also in this release, ¬audit findings: the content metric moved from cue multisets to word bigrams and the threshold from 0.90 to 0.85, both measured → *Why word bigrams, and why 0.85* in `ARCHITECTURE.md`. ! The old metric scored a re-split subtitle at **0.0%** — a silent false negative no threshold could correct.

---

## 2026-08-12 (sixth pass) — build-tooling determinism, found while cutting 1.0.0.0

Not a code audit. Building both required platforms for the first release exposed three defects in `agentic/tools/`, all the same class: **a script that produces a different answer depending on which PowerShell edition and OS runs it.** The lock is written by both platforms → every one of these would have shipped. All three are now standing rules in `CLAUDE.md`.

1. **`Get-TreeHash` hashed the same directory differently on Windows and Linux** *(high)* — `AppendLine` emits `Environment.NewLine` and `Sort-Object` compares culture-aware. The `linux-x64` payload built under WSL failed its own integrity check the moment a Windows run verified it, and ! the failure **looked exactly like tampering** — "does not match its recorded hash. It was modified after the build." Now an ordinal sort and an explicit LF. The tree hash is the only evidence a staged payload was not altered between build and release, and a check that cries wolf on a clean tree gets ignored.
2. **The lock's JSON formatting flipped w/ the writing edition** *(low)* — 5.1 aligns colons and escapes apostrophes as `'`, 7 does neither, `Set-Content -Encoding UTF8` adds a BOM on 5.1 only → every alternating build rewrote the whole file. Replaced w/ `ConvertTo-StableJson` + `Write-TextFile`; a 5.1 write and a 7 write of the same lock are byte-identical. Low, ! but it **buries a one-line hash change in a whole-file diff**, which is where a bad hash hides.
3. **`Test-UploadedAssets` died instead of reporting** *(medium)* — `2>$null` does not suppress a native command's stderr under `ErrorActionPreference Stop` → a missing release raised `NativeCommandError` and aborted the gate before it printed its findings. Now via `Invoke-Captured`. The release gate's job is to enumerate **every** problem, and this one stopped at the first.

Also removed `resolved.python`/`resolved.pyinstaller`: whichever platform built last overwrote them → the file recorded one interpreter while the two payloads were frozen on different ones. Authoritative values are per-RID under `payloads.<rid>`, which is what `Test-PlatformsAgree` reads.

**Release verification.** Both payload archives were downloaded from the URL compiled into `PayloadManifest.g.cs` and hashed, matching the lock; the plugin zip was downloaded from the `sourceUrl` in the served manifest and hashed, matching `checksum`. The fetch path is verified end to end, ¬just asserted.

---

## 2026-08-12 (fifth pass) — full sweep after Phases 8, 9 and the second vendored tool

Every file under `Jellyfin.Plugin.AutoSubSync/` and every script under `agentic/tools/`, full checklist incl. the comment rule.

| # | Defect | Note |
| --- | --- | --- |
| B34 | Stripping SDH from an `.ass` subtitle wrote **SubRip into the `.ass` file** *(data corruption)* | `SeConvRunner` always emits `subrip` → the Transform stage turns an `.ass` input into `.srt` content, and `Place` took the Overwrite branch on any external text subtitle → SubRip bytes in a file named `.ass`, which Jellyfin cannot parse. The user loses a working subtitle and gains an unreadable one. Reachable w/ `RemoveHearingImpairedTags` on, Overwrite (the default), and any non-SRT SDH sidecar. `Place` now requires source and result extensions to match before overwriting. Two `placecheck` cases, the first of which fails against the old code |
| B35 | Pruning **stranded** backups it could never reach again *(data loss)* | `Prune` removed the record for every unresolved item but discarded the backup only when the video file was also gone. The guard was right and the removal was not: a record whose video still exists (unmounted share, moved mount point, library removed and re-added) was deleted while its backup stayed on disk. ! The record is the **only index into the vault** → that backup became unreachable permanently. Both conditions are now required to prune |
| B36 | Neither ffmpeg extractor had a timeout or a kill-the-tree path *(resource exhaustion)* | Extraction runs **inside** the target's `SyncQueue` slot → an ffmpeg stalled on an unresponsive mount held that slot for the process lifetime and the queue permanently lost a worker; enough of them and the plugin stops syncing entirely w/ no error anywhere. Cancellation was worse than useless in `ImageSubtitleExtractor` — its `catch` filter excluded `OperationCanceledException` → a cancelled sync left both an orphan ffmpeg and an undeleted temp file. Both now go through `FfmpegProcess`. ! The sync engines already had all of this; the extractors were the gap |
| B37 | An external `.sup` was rejected while the identical **embedded** track OCR'd fine | `BuildExternalCandidate` tested the extension allow-list before the image test → a PGS sidecar was recorded as "No sync engine reads .sup subtitles" even w/ `ConvertImageSubtitles` on. The image test now runs first and names the format, which also guarantees a bitmap sidecar never reaches `SubtitleContent.HasCues` or an alignment engine |
| B38 | The engine-readiness status was computed and thrown away | The controller took `AssyRuntime` and never read it, and `/Status` hid its panel whenever the record count was zero. ! Those two conditions **coincide exactly when it matters**: a server whose payload never downloaded has no records *∵* it has no engine → the config page showed nothing at all on the one screen an admin would check for an explanation |
| B39 | `IsWithin` compared paths case-insensitively on case-sensitive filesystems | Not reachable — the path compared comes from a pinned binary handed `-o` explicitly, and the fallback is the plugin's own path either way. Fixed ∵ a permissive path check is not worth keeping as a matter of taste. (! S1 is the same defect surviving in `LibraryScopeResolver`, where it *is* reachable) |
| B40 | A queued `SyncItem` could raise an unobserved task exception | The fire-and-forget `Task.Run` wrapped `ProcessAsync` (which swallows everything) but also `_store.Flush()` (which does not) |

All **[F]**.

**Verified clean** — no shell strings anywhere · dry run still a hard filesystem lock, preceding the queue and therefore extraction, OCR, the engine, the transform and the placer · no endpoint accepts a path · deletion doubly scoped · rollback never deletes before restoring · archive extraction path-checked in both formats, w/ tar entries that are ¬regular files or directories **skipped rather than resolved** ∵ a symlink target is not checkable at extraction time · payload pruning scoped to one tool (keying the cache by tool name is what made this true) · concurrency re-read unchanged.

**Release-pipeline rehearsal, same day.** The release process was run end to end without publishing. The gate caught the two real blockers. Five further defects, all metadata rather than code: `manifest.json` + `build.yaml` both advertised "a **bundled** build" — nothing has been bundled since Phase 11 · `build.yaml` `artifacts:` still listed `assy-cli/` beside the DLL · `targetAbi` was documented as "Jellyfin server version" w/ a worked example contradicting `build.yaml`; ! it is a **minimum** and Jellyfin hides the plugin from any server below it, so the file was right and the document was wrong · two code comments still called `assy-cli` "bundled" · a `README.md` typo.

**Accepted, tracked** — **`ShiftAsync`/`BuildShift` and `Models/AssyBatchSummary` are unreferenced.** Both appear in the design document's file listing and `batch` is explicitly decided against there. Documented dead weight w/ no security or correctness impact; removing them means editing the plan's structure listing. · **`AssyConfigFilePath` was a settable path handed to a child process** — the design document's "advanced escape hatch". **Resolved 2026-08-14: retired**, and it went the other way than "keep it and document it". ! Leaving `--config-file` unset turned out ¬to be neutral — `assy-cli` then reads the *desktop application's* own config from the platform user-config directory, so engine behaviour depended on whether anyone had run the GUI on that host. Replaced by `Cli/AssyConfigFile`, which renders a config the plugin owns and passes it on every invocation; the two knobs worth reaching became typed settings → no setting names a path any more. ! The legacy shape in `agentic/tools/configcheck/Legacy.cs` still carries the property and **must keep it** — it is the verbatim 1.1.0.0 config, and `XmlSerializer` ignores the now-unknown element.

---

## 2026-08-11 (fourth pass) — Phase 11: fetching the payload on first run

Audit of the generated manifest, `PayloadStore`, `PayloadFetcher`, `PayloadBootstrap`, the `AssyRuntime` rewrite, and the tooling that pins them. ! This is the first feature in the plugin that **downloads and then executes code** → the negative cases were written and made to fail before anything was built on top of them (`tools/payloadcheck/`, eight cases).

**B33. A failed promotion destroyed the working payload** *(data loss)* **[F]**
`Promote` deleted the destination directory and then moved staging into place. Any failure between those two — a file locked by a running sync on Windows, a full disk, a permissions change — left the server w/ **no payload at all**. The window is small but the loss is total, and it lands on the **upgrade path**, which is exactly when a working payload already existed. The previous payload is now renamed aside, restored if the move throws, and deleted only once the new one is in place. Pinned by *a failed promotion restores the previous payload*, which fails against the old code.

**Verified clean** — verification precedes extraction: the SHA-256 is computed on the archive on disk and compared **before** `ZipFile.OpenRead` is called at all; a mismatch deletes the archive and returns without creating a payload directory · traversal refused, incl. rooted entry names (`Path.Combine` discards the root when the second argument is absolute and the prefix test then fails). ! The assertion **discriminates**: without the guard the entry writes successfully and the install *succeeds* → the test fails on outcome, ¬only on the escaped file · the download host is a compile-time constant · the manifest cannot drift from the lock, confirmed live during the audit when an edit to `release.assetRepo` without regenerating was caught by the gate rather than by review · readiness is reported once, ¬per item, so a missing payload produces log lines rather than a `Failed` record per subtitle · superseded payloads are pruned only after the replacement verifies.

**Accepted, tracked** — **`PayloadFetcher.IsRunning` is a non-volatile `bool` read from another thread.** A stale read only selects between two readiness *messages*; it cannot cause a fetch to be skipped or duplicated ∵ single-flight is enforced by a semaphore. · **A hash mismatch does not retry.** Only network-shaped failures retry w/ backoff. A truncated body normally surfaces as an `IOException` and therefore does retry; the residual case is a server returning a complete but wrong object, where retrying the same URL is unlikely to help and looping on it would obscure the real problem. The install is retried on the next server start.

**Scope withdrawn during this pass**, both at the user's direction — **`PD-OFFLINE`, the drop-in directory**: installing a Jellyfin plugin already requires reaching a repository manifest → a server that cannot reach the internet never acquires the plugin to begin with. The premise was wrong. · **`PD-SILENT`, the payload UI**: the payload is an asset of this plugin's own release → **installing the plugin is the consent**. Config panel, progress polling, download button, opt-out setting and both API endpoints removed in favour of log lines.

---

## 2026-08-11 (third pass) — adaptive concurrency

`Services/AdaptiveConcurrency` and the `SyncQueue` rewrite. ! The control law was checked **by simulation** rather than by reading, ∵ a feedback loop that looks correct and oscillates is the expected failure here. All **[F]**.

**B30. Rebuilding the semaphore over-admitted on every level change** — `SyncQueue` created a fresh `SemaphoreSlim` whenever the resolved limit changed. The new instance starts fully available while in-flight work still holds permits on the old one → for the duration of those runs the real concurrency was the new limit *plus* whatever was outstanding. Tolerable when the limit only moved on a settings change; adaptation moves it every six syncs, ! which turns a rare transient into the normal case **and lands the overshoot precisely when the controller is trying to measure a level**. Replaced w/ one process-lifetime semaphore of 8 permits where a limit of *n* is enforced by holding `8 − n` back as ballast. Shrinking is best-effort — a permit in use is reclaimed when it returns rather than by interrupting a sync.

**B31. A settled controller could never probe back upward** — `_step` reverses when a step makes throughput worse → a controller that probed 1 → 2, found 2 worse, and settled at 1 kept `_step = −1`. Every later re-probe tried level 0, which clamps to 1, compares equal, and settles again → **the level was pinned for the process lifetime no matter how conditions changed.** Re-probing now picks its direction from where the headroom is.

**B32. A ceiling drop left a stale baseline** — `CurrentLevel` clamps `_level` down when the ceiling falls (fewer cores visible after a cgroup change, or a config edit). It reset the sample accumulator but ¬`_measuredLevel` → the next decision compared a mean taken at the new level against a throughput measured at the old, higher one, guaranteeing a spurious "worse" verdict and an immediate settle.

**Verified clean** — ! **it cannot exceed the old default**: the ceiling remains `AutoConcurrencyFor` → adaptation can only ever conclude it should use *less* than the previous formula would have taken · an explicit `MaxConcurrentSyncs` bypasses the controller entirely and is clamped 1–8 — a number the user typed is an instruction, ¬a starting point for a search · samples cannot be misattributed: a run whose level changed while in flight is discarded, and only runs that returned normally report at all → a library of unsyncable files cannot drive the level anywhere · division guards · convergence simulated over 200 runs per case w/ ±10% jitter at every reachable ceiling. ! The jitter is **seeded** — the first draft used `Math.random` and failed on one run in three, which is an assertion that cannot distinguish a regression from an unlucky draw.

**Accepted, tracked** — **The level can settle one slot below optimal.** W/ perfect scaling the gain from the *n*th slot is `1/n` → it closes on the 10% decision margin as *n* rises and a six-sample mean sometimes stops early. The miss is always **downward**, which is the direction this setting should err in. · **`simulate-concurrency.mjs` mirrors the C# rather than binding it.** It asserts the *constants* still match and fails if they drift, ! but a passing run proves the control law converges, **¬that the shipped code implements that law**. Both must be re-read together when either changes.

---

## 2026-08-11 (second pass) — Phase 6: rollback and vault pruning

`RollbackService`, the `RollbackAll` endpoint, and the vault half of `Prune`. Every branch traced against the case where the branch is wrong — ! that is the one place in this plugin where a mistake destroys a file the user cannot get back. All **[F]**.

**B27. Pruning deleted backups for media that was merely unreachable** *(data loss, high)* — `Prune` drops records whose `ItemId` no longer resolves, and the design document requires the matching `Discard` or the vault grows forever. ! But an unmounted share, a renamed mount point, or a library removed and re-added all make Jellyfin drop items **for media that still exists** → discarding on that signal alone destroys the only copy of a subtitle the plugin overwrote. The vault entry is now discarded only when the record's video file is also gone from disk. (B35 is the mirror defect: the *record* was still pruned either way, stranding the backup.)

**B28. A renamed marker suffix silently orphaned plugin output** — `Delete` refuses any path not carrying the current `MarkerSuffix`, which is right. It counted the refusal as `Skipped`, and skipped records were removed from the store → changing the suffix after a sync meant rollback dropped the record while leaving the file, **w/ nothing left pointing at it**. A refusal is now a failure: the record survives, the report counts it, and the log names both the path and the marker that did not match.

**B29. Concurrent rollbacks walked the same records twice** — a double-clicked button. The second pass would find backups already discarded and report failures for work that had in fact succeeded. `RollbackAll` is now single-flight.

**Verified clean** — the restore-before-delete ordering: a `Retimed` record restores its backup and never deletes; a `Created` record deletes and never restores. ! The two verbs are mutually exclusive **by construction, ¬by ordering** → there is no window where a file is gone and its replacement not yet written · a failed restore keeps its backup, and the record for the same reason — it is the only pointer to it · `SubtitleProvenance.Retimed` is the **zero value** → a record deserialized from before the field existed defaults to the branch that *restores*, never the branch that deletes · deletion scope.

**Accepted, tracked** — **Rollback runs while `DryRunMode` is on.** ! Dry run is a lock on writing *into* the library, and rollback only ever writes a file the user already owned or deletes one the plugin made. Blocking it would leave a user who re-enabled dry run unable to undo anything. A dry-run record has no `OutputPath` and no `BackupPath` → rollback is inert for work done under it either way. **Recorded so later audits of checklist item 8 do ¬re-flag it.** · **Rollback overwrites a file the user edited after the sync.** The plugin cannot tell an edit from its own output without storing a second hash, and the confirmation dialog states that backups are restored over what is there. · **`InFlight > 0` is a guard, ¬a lock.** `RollbackAll` refuses while syncs are running, but an `ItemAdded` event a moment later can still start one. The window is small and the outcome is a re-synced subtitle rather than a lost one.

---

## 2026-08-11 — Phase 4: result placement and the engine fallback chain

`SubtitlePlacer`, the fallback loop, and the attempt budget. All **[F]** in-pass. (! B23 here is distinct from the OCR pass's B23 below.)

**B23. Overwrite mode re-synced the same subtitle on every scan** *(correctness, high)* — `SourceSha256` was captured from the input *before* syncing, and in Overwrite mode the synced file then replaced that exact input → on the next scan the stored hash no longer matched what was on disk, `IsStillCurrent` returned false, and the subtitle was synced again, **this time against the already-synced file**. Left alone it would drift a little further on every scan, each pass burning a full audio analysis per subtitle. `RunPipelineAsync` now re-fingerprints from the output whenever placement reports `Retimed`. `BackupVault.Store` returning an existing backup rather than overwriting one meant the true original was never at risk, but nothing else about the loop was benign.

**B24. Concurrent placement could resolve the same sidecar name twice** — `ResolveCollision` picks a free name by testing `File.Exists`. Two targets on one video sharing a language and flags — two embedded tracks, typically — can be in flight at once, both find the same name free, and both move onto it → one file survives, two records claim it, and rollback would delete a file the other record still points at. `Place` now holds a lock across resolution and the move.

**B25. Cancelling mid-engine left the scratch file behind** — every cancelled scan leaked one file per in-flight target.

**B26. The engine's reported output path was trusted unconditionally** — `AssyInvocationResult.Output` comes from parsed CLI stdout and was used as the file to place if present. ¬attacker-controlled in any realistic path (the plugin supplies `-o`) but it is external input naming a file the plugin then **moves into the media library**, w/ no check tying it to the scratch directory. Now accepted only when it resolves inside scratch.

**Verified clean** — backup-before-overwrite ordering: `Overwrite` stores the backup and **aborts the whole placement** if the store fails · scratch-then-move: no engine is ever pointed at a media directory; every `-o` is a GUID path under `TempDirectory`, and a failed attempt's output is deleted before the next engine runs · attempt budget resets when the input fingerprint changes → a repaired subtitle is not stuck at an old failure.

---

## 2026-08-10 (third pass) — found while researching OCR

Not a scheduled audit; surfaced while reviewing how image-based subtitles are handled. All **[F]**.

**B21. VobSub sidecars passed discovery** — `SupportedExtensions` included `.sub` and `.idx`, both VobSub (image data plus its index) → external VobSub sidecars were discovered and handed to an engine that cannot read them, producing a guaranteed per-item failure where a clean skip belongs. ! Embedded image codecs were correctly excluded, **so the two paths disagreed**. `.idx` removed outright; `.sub` is genuinely ambiguous (MicroDVD text in some releases, VobSub bitmap in others) so it is kept and resolved by looking for a sibling `.idx`, the only reliable discriminator.

**B22. No engine/format compatibility check** — the orchestrator took `SyncToolChain.FirstOrDefault()` and handed the input to it regardless of format, but the engines differ. An extracted `.ass` w/ `autosubsync` configured, or a MicroDVD `.sub` w/ the default chain, would fail on every item w/ no useful message. Added `SyncToolCapabilities` mirroring upstream's `SYNC_TOOLS` format lists. (Moot from the tenth pass: one engine.)

**B23. Unprocessable tracks were dropped silently** — image-based and otherwise unreadable tracks were dropped in discovery by returning null → no `SyncRecord` existed and the config page could not report them, while `ARCHITECTURE.md` claimed they were "reported as skipped rather than silently ignored", which was untrue. ! Discovery now distinguishes **capability** rejections from **scope** rejections: capability rejections (image codec, unreadable extension) produce a target carrying an `UnsupportedReason`; scope rejections (language allowlist, external/embedded toggles, the plugin's own output) still return null — recording those would put a row against every foreign-language track in the library. `Unsupported` is a status distinct from `Skipped`: `Skipped` means processed and discarded as a no-op, `Unsupported` means never processable. **Collapsing them would have made the count meaningless.**

---

## 2026-08-10 (second pass) — full code audit of the Phase 1 skeleton

All 26 source files (≈2,400 lines), `configPage.html`, and the docs, against the full nine-item checklist. All 19 findings presented, approved, fixed. Numbering continues from the design audit's A-numbers.

| # | Defect | Sev |
| --- | --- | --- |
| B1 | **A store write failure aborted the entire scan.** `ProcessAsync` catches everything and then calls `_store.Upsert(record)` *inside* the catch block; `Upsert` does synchronous file I/O and can throw → the exception propagates out of the catch, out of `ProcessAsync`, and aborts `FullLibrarySyncTask`, violating the invariant that one bad file must never abort a sweep. ! Worst exactly when it matters most: **a full disk kills the whole run instead of failing one subtitle** | High |
| B2 | **`RollbackAll` was a stub that reported success.** Returned `200 OK` w/ `{Restored: 0, Deleted: 0}` and did nothing; the page said "Rollback complete." ! A non-functional destructive-recovery path that **claims** success is worse than one that is visibly absent | High |
| B3 | **Store write amplification was O(n²) over a scan.** Every `Upsert` serialized the *entire* record list plus a backup copy and two file moves, one to three times per subtitle track → a 5,000-item library ≈15,000 targets and up to ≈45,000 full rewrites of a multi-megabyte file, while the same scan saturates CPU w/ sync work. ! It is ¬the record *count* that breaks the JSON-store assumption, **it is the write pattern** | High |
| B4 | **Embedded targets extracted before the skip check** → every full scan re-extracted every embedded track even when nothing changed, defeating the fingerprint guard for exactly the targets where the work is most expensive. Secondarily, fingerprinting an embedded target from the freshly extracted temp file is fragile — a different ffmpeg build could produce different bytes and silently invalidate every embedded record; the video hash already covers the track | High |
| B5 | **`SyncStore` handed out live object references** → the `lock` protected the list structure but not the elements, and the orchestrator mutates a record outside the lock while `GET /Status` may be enumerating it | Med |
| B6 | **`GetLibraryId` prefix match had no separator boundary** — `/media/movies-4k/film.mkv` matched a library rooted at `/media/movies`. ! Same bug class explicitly guarded against in the now-deleted `PathMapper`; **it survived here** | Med |
| B7 | `POST /SyncItem/{itemId}` ran the whole sync inline — an item w/ three tracks could hold the HTTP request open for an hour | Med |
| B8 | **No server-side configuration validation.** The dangerous one is `MarkerSuffix`: set it empty and `IsPluginOutput` always returns false → the plugin stops recognizing its own output and re-syncs its own results forever, the exact runaway the marker exists to prevent | Med |
| B9 | Config page numeric fields could serialize as null — `parseInt` on an empty input yields `NaN`, which `JSON.stringify` emits as `null` | Med |
| B10 | Five configuration settings declared but never read, three of them presented as working controls | Med |
| B11 | `ParseLastJsonObject` mis-handled the NDJSON case it claimed to support — for `batch --json` the final line is `{"summary": {...}}`, which deserializes into an `AssyResult` w/ every field defaulted incl. `Ok = false` → scanning backwards returns a "failed" result for a batch that succeeded | Med |
| B12 | `LibraryEventHandler` did blocking I/O on the library event thread **before** the debounce | Med |
| B13 | `FileFingerprint.TryComputePartial` assumed a full read — a short read produces a different hash for identical content → spurious re-syncs | Low |
| B14 | `IsPluginOutput` matched too loosely — the `EndsWith(markerSuffix)` arm meant a user file named `my-autosubsync.srt` was treated as plugin output | Low |
| B15 | `Prune()` writes the store during dry run → the invariant in `CLAUDE.md` was overstated and now says "no writes to the **media library**" | Low |
| B16 | `/Status.LastRunUtc` was `max(UpdatedUtc)` across records — the last record change, ¬the last scan | Low |
| B17 | `SyncQueue.Dispose` could throw on in-flight work | Low |
| B18 | **`ClearDatabase` silently destroyed rollback capability** — backup and output paths live only in the store, so clearing it strands every backup on disk w/ nothing to restore them, and the confirmation text did not mention it | Low |
| B19 | `Dashboard.confirm` availability unverified — it exists in some Jellyfin web versions and not others | Low |
| B20 | `MatchesGlob` compiled a fresh regex per (item, pattern) pair | — |

All **[F]**. **Verified clean** — command injection · child environment allowlisted (A6) · authorization · typed deserialization w/ per-line failures swallowed rather than thrown · process lifetime (timeout + `Kill(entireProcessTree: true)` at both spawn sites, semaphore released in `finally`, cancellation distinguished from timeout) · store corruption recovery (temp-then-rename, backup before write, restore on parse failure) · dry run · self-output recognition · fingerprint guard.

---

## 2026-08-10 — design audit of the plan and Phase 1 skeleton

! Ran against the **design before implementation**, which is the cheapest time to catch most of it. Nine checklist items incl. the four plugin-specific ones.

**A1. Fingerprint covered only the subtitle, ¬the video** *(high)* **[F]** — a user who replaces a movie w/ a different release while keeping the same sidecar leaves the subtitle byte-identical → the guard reports "unchanged" and skips it, **leaving a subtitle synced to a video that no longer exists, silently and permanently.** Added `VideoLength` + `VideoPartialHash`; a target is current only when *both* match. Video hashing is `size + first 64KB + last 64KB`, never a full read — hashing a 40 GB remux each sweep would cost more than the sync it avoids. Strategy borrowed from upstream's `processed_items_manager.py`.

**A2. Scratch files went to the system temp directory** **[F]** — inconsistent w/ the extractor, and in a container `/tmp` is frequently a small tmpfs that an extracted subtitle plus a failed run can fill. Both now use `IApplicationPaths.TempDirectory`.

**A3. Partial output could land in the media folder** *(high)* **[F]** — `assy-cli` writes wherever `-o` points, and pointing it at the final destination means a crashed or timed-out run leaves a partial subtitle file **exactly where Jellyfin will index it**. `-o` always receives a scratch path; the result moves into place only after a successful, non-cancelled exit.

**A4. `GetVirtualFolders()` called per item** **[F]** — a 15,000-item sweep meant 15,000 enumerations. Folders are fetched once per sweep and threaded through.

**A5. Store grew without bound** **[F]** — nothing removed records for deleted media, and the store is fully loaded at startup.

**A6. Child process inherited the full server environment** **[F]** — `assy-cli` received Jellyfin's entire environment, which may hold API tokens or database credentials it has no business seeing. Now cleared and repopulated from a conservative allowlist. ! Noted ∵ it was raised during review: **a virtualenv does ¬address this.** A venv only manipulates `PATH`, `VIRTUAL_ENV` and `sys.prefix` and provides no environment-variable isolation. Process-level isolation is the only mechanism that works, and it is independent of how the payload is packaged.

**A7. `KeepBackups = false` w/ `Overwrite` silently disabled rollback** **[F]** — ! the original fix was **wrong**, and this is why. Shipping the combination behind a warning was accepted here and should not have been: a warning transfers responsibility to the user for a choice w/ **no upside** — the off position's only effect is to make an irreversible operation irreversible, and side-by-side mode already serves anyone who wants originals left alone. `KeepBackups` was deleted from `PluginConfiguration`, `Overwrite` backs up unconditionally and refuses the write when the backup fails. ! **Deleting the property rather than pinning it to `true` matters** — Jellyfin's deserializer drops unknown keys, so a stored `false` from an older install cannot resurrect it. (The same reasoning is applied again in the seventeenth pass.)

**A8. `LibraryEventHandler` uses `Task.Run` per event** **[A]** — during a full library scan `ItemUpdated` fires constantly. The 30s per-item debounce bounds the damage and everything downstream passes through `SyncQueue` → no unbounded *work*, but thousands of tasks can be parked awaiting the semaphore.

**A9. `MinimumOffsetMs` compares only the first cue** **[A]** — a pure rate correction can leave the first cue nearly unmoved while shifting the end of the file by seconds. Partially mitigated at the time by biasing toward keeping real work. ! Escalated to a defect when F6 made the same approximation load-bearing for a *safety* gate; properly fixed by P4's cue-identity fit, then made moot when the setting was deleted in the seventeenth pass.

**A10. `GetItemsInScope` materializes every movie and episode** **[A]** — acceptable at the scale this plugin targets, and `ILibraryManager` has no streaming enumeration that avoids it.

**Verified clean** — command injection (! removing the Docker and remote execution modes eliminated the one place that tokenized user-supplied arguments) · process lifetime · typed deserialization · API authorization · store durability, mirroring a sibling plugin's `PairStore` which has this pattern in production · dry run — ! `ProcessAsync` returns before any filesystem work and there is currently **exactly one entry point** to the pipeline, so the check cannot be bypassed; **this must be re-verified whenever a new entry point is added** · self-output recognition.
