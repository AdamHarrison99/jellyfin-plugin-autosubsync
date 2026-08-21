# Agent Handoff — JellyfinPlugin-AutoSubSync

Read fully before touching code. **"Read the handoff" = read this entire file top to bottom, ¬grep/skim** — notation is compressed precisely so a full read is cheap; keyword search misses the cross-references that prevent repeat mistakes. Working rules + standing traps. ¬a changelog, ¬history.

> **Maintaining this file.** ! **Information must earn its place — context is finite.** Default to ¬adding. Before adding, ask: would this change a decision someone makes later? No → cut. Notation is deliberately telegraphic — optimise for agent parse, ¬human readability; write additions the same way. Prefer trimming an existing section over appending. Correct anything stale.
>
> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with.

## Documentation map

`agentic/` lives **inside** the repo, at `Jellyfin.Plugin.AutoSubSync/agentic/`, and is published with it. Repo root = `Jellyfin.Plugin.AutoSubSync/` — run every git command from there. ! It carries no personal data: no real names, hostnames, IPs, local paths or quotes of the user — keep it that way.

- **`agentic/CLAUDE.md`** — platform pin, payload/lock system, assy-cli contract, security invariants, the status panel invariant, release process, harness index. Read before doing anything.
- **This file** — working rules + traps.
- **`agentic/ARCHITECTURE.md`** — what each component does and **why built code works that way**.
- **`agentic/AUDIT.md`** — audit history. Read before auditing → ¬re-flag known false positives.
- **`agentic/memory/`** — the agent's own persistent memory, index in `MEMORY.md`. Lives here → it travels w/ the repo and is readable. ! Write every **project** memory here. A memory that needs a real name, hostname, drive letter or a quotation to make sense does **¬** belong in the repo — it goes in the agent's own local store instead, along w/ the rule saying so.
- **`agentic/IDEAS.md`** — unshaped ideas only. Anything w/ a data model → design document, *Roadmap: the staged pipeline* (Phases 8–10).
- **`agentic/JellyfinPlugin-AutoSubSync plan.md`** — design + rationale for work **¬yet built**.
- `Jellyfin.Plugin.AutoSubSync/README.md` — user-facing starter overview, ¬documentation. One short line per setting; the config page carries detail.

**There is no changelog and none is to be created, under any name.** It had one; removed deliberately ∵ it drifted into a second copy of the design document and accumulated rationale for code that did ¬exist yet.

| Content | Home |
|---|---|
| Why an *unbuilt* feature is designed a certain way | Design document |
| Why *built* code works the way it does | `ARCHITECTURE.md` |
| What an audit found + what was done | `AUDIT.md` |
| What a line does, when ¬obvious | A short comment |

## Comments (hard rule)

**A comment is a short note for a human reading the line. It is never documentation.** It may say *what* a line does when the line doesn't say so itself. It may never say *why* it was done that way, what was tried before, what would break otherwise, what the alternative was → that is documentation → `ARCHITECTURE.md`.

Applies to source under `Jellyfin.Plugin.AutoSubSync/`. **`agentic/` is exempt** — its comments are agent working notes, any length.

| Rule | Limit |
|---|---|
| `comment-run` | ≤ **2** adjacent comment lines, `!` traps included |
| `comment-length` | ≤ **100** chars/line |
| `comment-block-length` | ≤ **180** chars across adjacent lines |
| `comment-prose` | ≤ **2** sentences across adjacent lines |
| `rationale-word` | Connectives, history, tutorial voice — list lives in the linter |
| `notation-shorthand` | `¬` `∵` `→` `w/` — this file's notation, ¬a shipped comment's. Plain words |
| `doc-comment` | `///` banned; documentation by construction |
| `block-comment` | `/* */` banned in source |
| `commented-code` | Dead code in a comment → delete |
| `redundant-comment` | Short comment whose every word appears in the line below |

! **Only exception: a standing trap a later reader could silently re-break** → one terse `!` line, the instruction alone, ¬the reasoning. A trap is exempt from **nothing** — every limit above still applies, and it is where documentation creeps back fastest. Before writing any comment, ask whether its absence costs a later reader real time. No → don't.

```
node agentic/tools/check-comments.mjs                           # lines this branch changed
COMMENT_LINT_BASE=HEAD node agentic/tools/check-comments.mjs    # uncommitted work only
node agentic/tools/check-comments.mjs <path>                    # whole-file scan
```

`node`, ¬`npm run`.

## ! Keep `agentic/` out of the plugin's own comments

`agentic/` is published now, so naming it is no longer a dangling pointer — but a comment saying *"see ARCHITECTURE.md"* is still **documentation in a comment**, which the rule above forbids. Put the pointer nowhere: the explanation goes in `ARCHITECTURE.md` and the comment says what the line does. `check-comments.mjs` still fails the build on a reference to a file that does **¬** exist in the repo (`CHANGELOG.md` and friends).

## Build + verify

```powershell
dotnet build Jellyfin.Plugin.AutoSubSync.csproj   # intermediate check, from the repo root
.\agentic\tools\verify.ps1                 # build + comment lint + payload check
.\agentic\tools\verify.ps1 -SkipBuild      # lint + payload only
.\agentic\tools\verify.ps1 -ReleaseMode    # release gate; payload warnings become failures
```

`verify.ps1` runs **before every commit**, ¬after every edit — use `dotnet build` or the one relevant harness meanwhile. Exits 1 on a build error, **any build warning**, a comment violation, or payload drift. ! The zero-warning bar doesn't get lowered to get a commit through.

Target **Jellyfin 10.11.x on net9.0**. Jellyfin 12 unreleased → ¬bump to its RCs. The 10.11 packages refuse to restore on net8.0.

## ! Git and releases

- **Never run `git commit`, `git push`, or `gh release create` without the user asking for that action in that moment.** Approving a task ≠ approving publication; approving a *plan that mentions committing* is ¬permission to run it. Staging + reviewing a diff is fine. The user authors every commit.
- **Never bump `AssemblyVersion`/`FileVersion`, edit `manifest.json` versions, or build a release artifact unless told to.** A release is a deliberate act w/ a checklist in `CLAUDE.md`, ¬the natural end of a piece of work. Finished = builds + passes `verify.ps1` + documented, nothing more.
- Commit messages: subject line + terse bullets, ¬rationale paragraphs. ! **Read `git log` and `git diff` before writing one** — never from memory of the session → *Commit messages* in `CLAUDE.md`. Release notes = bullets only; requirements/install steps live in `README.md`.
- Files an agent never invents: `manifest.json` version entries (release only), `assy-cli/` payload contents (`build-assy.ps1` only).

## Tools

`agentic/tools/` — committed, so the next agent inherits them. Anything worth running twice goes there; a throwaway for one sweep stays in the scratchpad and dies there. Diagnostic binaries too: `agentic/tools/ffmpeg/` holds vendored `ffmpeg`/`ffprobe` (¬in git).

| Tool | Purpose |
|---|---|
| `verify.ps1` | Pre-commit gate, above |
| `check-comments.mjs` | Enforces the comment rule |
| `build-assy.ps1` | Builds the assy-cli payload, archives it, records it in the lock, regenerates the manifest |
| `pin-seconv.ps1` | Pins seconv to an upstream asset + records hashes; `-Check` reports a stale pin |
| `check-payload.ps1` | Verifies payload ↔ lock ↔ generated manifest ↔ uploaded assets agree |
| `payload-lock.psm1` | Lock/manifest helpers (¬run directly) |
| `payloadcheck/` | A bad payload archive is refused: wrong hash, traversal entry, missing binary |
| `storecheck/` | The v1 → staged `records.json` migration, against a fixture. Also `SyncOutcome` — how the status panel groups a stored outcome, incl. ! that a **stale `Verify` stage cannot outvote `RefusedByAudio`** — and that a reopen clears everything describing the old run |
| `namingcheck/` | Same-language tracks get distinct sidecar names; single-track names never change. Also the slot rule that skips a bitmap when readable text serves it — ! incl. the forced and hearing-impaired cases, ∵ a language-wide rule drops signs and songs |
| `subcheck/` | Cue detection per format + SDH thresholds, incl. cases that must **¬**trip |
| `placecheck/` | Placement never overwrites an OCR'd source; a stripped track loses its `sdh` token |
| `acquirecheck/` | The download feature w/out a network: whitelist, ask order, gap test, filters, budget, fall-through, retirement, ledger. Indexed in **`CLAUDE.md`** |

Measurement harnesses (`verifycheck`, `scorecheck`, `measurecheck`, `synccheck`, `dedupecheck`, `rollbackcheck`, `gatecheck`, `killcheck`, `formatcheck`, `langcheck`, `supsample`, `simulate-concurrency`, `check-rate-bound`, `check-sync-output`) are indexed in **`CLAUDE.md`** w/ what each exists to prove — ¬duplicated here. Most link the real source file → they cannot drift from what ships. ! Re-run the relevant one whenever its subject changes; a harness that drifted validates nothing.

## Pinned dependencies

`agentic/payload.lock.json` = the only answer to "what is actually pinned". `Cli/PayloadManifest.g.cs` is generated from it and committed — ! **never hand-edit**; a hand-written hash eventually says what someone wished were true, and it's the plugin's only trust root for a downloaded payload. `resolved`/`payloads`/`assets` are script-written; hand-editing defeats the only check proving the shipped payload is the one built. PyInstaller can't cross-compile → `build-assy.ps1` runs once **per platform**. Full mechanics + upgrade steps: `CLAUDE.md`.

## Working style

- **Multi-task request → a visible todo list**, updated every turn. Audit always last.
- **Report status as a Done / Not done table**, ¬prose. Outstanding and blocked items are always listed, w/ what each is waiting on.
- **Media lives on a slow SMB share** → ¬recursive scans, one title per media command.
- Present audit findings w/ location + severity + proposed fix, then **stop** — apply nothing until approved.
- ! **Ask before adding, removing or restyling anything on the config page.** Approving a finding is ¬approving a UI change as its fix — describe what the user would see, and wait. A data-only fix (a corrected count, a new record field) needs no ask; anything visible on screen does. 1.3.0.0 shipped three new status cards this way, off two approved findings.
- ! **Read the docs on a component before concluding about a defect in it** — `ARCHITECTURE.md`, `AUDIT.md`, the design document. Grep the component and its key type names. **A recorded decision the code now contradicts is itself the finding**, and a stronger one than what the code alone suggests; it is also invisible to anyone reasoning from the code. Twenty-ninth pass D5 was exactly this, and three findings in the same pass were already recorded as accepted.
- Cite by **stable ID or heading name**, never by position: `RM-SCOPE`, `B21`, `V11`, *Provenance* under *Data Model* — ¬"idea 4", "the third concern", "the item above". Positional refs go wrong silently the moment anything is inserted or reordered, and nothing detects it. Phase numbers are stable IDs; the numbered steps inside a phase are ¬, so cite the phase.
