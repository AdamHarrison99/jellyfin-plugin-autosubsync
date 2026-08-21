# JellyfinPlugin-AutoSubSync

Routing index + the invariants. Working rules and the comment rule → `agentic/AGENT-HANDOFF.md`; read that before writing code.

> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with.

## What this is

Finds out-of-sync subtitles across a Jellyfin library and realigns them by shelling out to [AutoSubSync](https://github.com/denizsafak/AutoSubSync)'s headless CLI, `assy-cli`. Two invariants govern everything:

1. **Every subtitle is aligned against the video's own audio.** `assy-cli` can align subtitle-to-subtitle — far faster, but it assumes the reference is correctly timed and there is no way to know that; an embedded track can be as desynced as a sidecar. Audio is the only ground truth available → the only reference used.
2. **Original media files are never modified.** Embedded tracks are extracted, synced, written back as sidecars. ¬remuxing, ¬in-place container surgery.

A third invariant governs the download feature alone: **nothing unconfirmed by the video's own audio reaches the library.** A subtitle downloaded for a language the item has nothing in is kept only where the check confirms it; everything else is discarded from scratch. ! The check refuses a wrong-title match by **abstention, ¬detection** — it fails to confirm rather than recognising the mismatch — so *unconfirmed → discard* is load-bearing and ¬a conservatism to tune away.

## Layout

```
Jellyfin.Plugin.AutoSubSync/              ← git repo root (.git is here)
├── manifest.json  build.yaml  README.md  LICENSE
├── Plugin.cs  PluginPaths.cs  PluginServiceRegistrator.cs
├── Cli/           ← argv building, process spawn, payload fetch + resolution
├── Subtitles/     ← discovery, ffmpeg extraction, sidecar naming, offset probe
├── Services/      ← orchestrator, concurrency queue, library scope, audio check
├── Tasks/         ← FullLibrarySyncTask
├── EventHandlers/ ← library event hooks
├── Api/           ← REST controller
├── Configuration/ ← config model + config page
├── Models/        ← SubtitleTarget, SyncRecord, AssyResult
├── Data/          ← SyncStore (JSON persistence)
└── agentic/       ← agent docs + tooling. Published, but ¬part of the plugin
    ├── CLAUDE.md AGENT-HANDOFF.md ARCHITECTURE.md AUDIT.md IDEAS.md
    ├── JellyfinPlugin-AutoSubSync plan.md   ← design document
    ├── memory/  plans/                      ← agent memory, shaped-idea write-ups
    ├── payload.lock.json                    ← what every vendored tool is pinned to
    ├── payload/  dist/                      ← staged builds <tool>/<rid>, release archives (¬git)
    └── tools/                               ← scripts + harnesses; ffmpeg/ vendored (¬git)
```

### The field server

Field evidence — logs, the record store — comes from a real Jellyfin server the maintainer runs the plugin on, reached over SMB from the dev machine. Its host is **not recorded here**; ask for the share path. ! **Read-only — ¬write, ¬delete, ¬restart anything there without being asked.** It is a **real library and the only copy of the plugin's state.**

| What | Path |
| --- | --- |
| Logs | `\\<server>\Jellyfin\Server\log` — `log_<yyyyMMdd>.log` is the server log; other plugins' files (`FFmpeg.*` and the like) sit beside it |
| Installed plugin | `<ProgramData>\Jellyfin\Server\plugins\AutoSubSync_<version>\` (server-local, seen in the log) |
| Plugin data + payloads | `<ProgramData>\Jellyfin\Server\data\AutoSubSync\` — store, vault, `payloads/<tool>/<version>/<rid>/` |

From bash use forward slashes. A day's log runs to several MB and carries ~20k plugin lines during a full scan → grep it, ¬cat it.

! **A rejection logs on two `WRN` lines**, one carrying the target key and one not → a naive `grep -c 'Rejected the sync for'` **doubles the count**. `check-inconclusive.ps1` and `check-stretch.ps1` take these logs as `-Log`.

! `Jellyfin.Plugin.AutoSubSync/` is the git repo root — run every git command from there, incl. for anything under `agentic/`. Origin = public `AdamHarrison99/jellyfin-plugin-autosubsync`, matching `sourceUrl` in `manifest.json`.

## Target platform

**Jellyfin 10.11.x, `net9.0`**, via `Jellyfin.Controller`/`Jellyfin.Model` **10.11.0**.

! Pin to the **oldest** 10.11.x supported, ¬the newest available. The emitted assembly reference carries the package version → building against 10.11.11 makes `MediaBrowser.Controller 10.11.11.0` a load-time requirement and a 10.11.0 server cannot satisfy it, whatever `targetAbi` claims. `targetAbi` gates which servers Jellyfin *offers* the plugin to; the package version decides which can actually load it. They must agree, and the build failing is what proves no newer API is used. Jellyfin 12 unreleased → ¬its RCs. 10.11 targets `net9.0`; the packages refuse to restore on net8.0.

## Security invariants

! Do not regress these without a deliberate decision.

- **No shell strings.** Every child process launches via `ProcessStartInfo.ArgumentList`. Nothing concatenates a command line → injection through a media path is structurally impossible.
- **No client-supplied paths.** API endpoints take item IDs; paths are derived server-side from a resolved `BaseItem`.
- **No user file is removed without a backup first.** Four steps, in order: **vault → abandon on failure → remove → restorable record.** `SubtitlePlacer.Overwrite` copies the original into the vault and abandons placement if that copy fails; only then `File.Move(overwrite: true)`. Overwrite mode *does* destroy the user's unsynced subtitle — the vault + `Retimed` provenance is what makes that reversible, ¬restraint. `SubtitleDeduplicator.Remove` is the second such path and pays all four; ! it stores under a `duplicate` label ∵ `BackupVault.Store` returns an existing entry rather than overwriting one, and a `Retimed` record already holds one under that filename → an unlabelled call copies nothing and the gate passes on someone else's bytes.
- **Unlinking is scoped.** The only `File.Delete` against a media path is `RollbackService`, firing only when **both** the marker suffix and a matching `SyncRecord.OutputPath` say the plugin wrote the file. Scratch/payload deletes never touch the media tree.
- **Dry run locks every effect observable outside the plugin's own record store**, ¬the filesystem alone and ¬a logging mode. No write to the media library, **and no provider search or fetch** — a search is a network request under the admin's account against a finite allowance, so it is the same rule. The plugin's own record store is still written. On by default on a fresh install. ! A dry run reports *would search*, ¬*would download*: how many gaps a provider could fill is unknowable w/out asking it.
- **All API endpoints require `Policies.RequiresElevation`** — carried by a class-level `[Authorize]` on `AutoSubSyncController` → a new endpoint inherits it.
- **The child process environment is allowlisted, ¬inherited.** `AssyCliRunner` + `SeConvRunner` clear `ProcessStartInfo.Environment` and repopulate only what a frozen CPython or a .NET single-file app needs. ! ¬replace w/ inheritance for convenience — a venv doesn't provide this and nothing else does.
- **The plugin holds no provider credential and ships no downloader.** It asks `ISubtitleManager` for what the admin already installed and credentialled → no account, no key, no quota of its own. ! **`ISubtitleManager.DownloadSubtitles` is never called** — it writes an unmarked sidecar into the media folder, unrollbackable and unchecked. `GetRemoteSubtitles` is the only fetch, and the stream lands in scratch.
- **No configuration setting resolves an executable path.** Tesseract is found via `PATH` + a fixed probe list; a settable path on an elevated endpoint = arbitrary code execution.
- **The download base URL is compiled in**, rendered into `PayloadManifest.g.cs` from the lock. A configurable host turns the fetcher into a download-and-execute primitive.
- Subtitle *content* is never logged; only paths and engine messages.

## The status panel invariant

! **The UI may lag. It may never lie.** Every number on the config page describes the library *as it is now*. A figure that was true of an earlier run and is no longer true of anything is a defect, ¬a cosmetic issue — the panel is the only view a user has, and a stale count sends them looking for a problem that does not exist, or hides one that does.

- **A record whose target discovery no longer offers is stale** → it must stop being counted. Gone from the library, gone from the enabled libraries, excluded by a setting, no longer a track: all the same case.
- **Stale means ¬counted, ¬necessarily deleted.** ! A record carrying a `BackupPath` is the **only** pointer to its vault copy — deleting that row strands the backup and `RollbackService` can never restore the file. Rollback outranks tidiness. A row w/ restorable state is retained and excluded from the counts; only a row w/ nothing to restore may be removed outright.
- **Lag is acceptable, staleness is not.** The panel may show the previous scan's totals until the next one re-stamps them; it may ¬show a count no run would produce again.
- **Work the plugin performed does ¬stop having happened.** A row the plugin closed itself — a duplicate it deleted, a track it set aside — leaves the cards, ∵ they describe the library. It stays on the stage table, ∵ that describes work. `SyncRecord.Retired` is that distinction; `Stale` means only *gone from the library*. Conflating them hid **546** removals and two OCR runs → K1, K3.
- ! **`Retired` is the only split permitted across the panel.** Cards read `!Stale && !Retired`, the stage table `!Stale`, and nothing else diverges — `IsAudioRefusal` decides rejected-versus-failed **once**, wherever the panel draws the line. Any further split reproduces what made `FAILED` disagree w/ *failed*.
- ! **A suppression is ¬an unsupported track.** A track a setting declined to process is `SyncStatus.SetAside` and appears on **no card** — only as a skip on its stage row. The cards no longer sum to `Total` and that is the deliberate cost; ¬fold it back onto *unsupported* to restore the sum.
- ! **A refusal is ¬a failure, and ¬counted as one to keep a number on screen.** The stage table has no `Rejected` column ∵ only `Verify` could ever fill it; the refusals are ¬folded into `Failed` to compensate. They are reported by the *rejected by audio check* card and its reason block. Reporting a figure in one place is enough; reporting it in the wrong place is ¬.

## The pinned payloads

The plugin runs pinned builds of its two vendored tools, ¬whatever the admin installed. Neither is **in the plugin zip**; each is fetched on first use and verified against a SHA-256 compiled into the assembly → that changes *when the bytes arrive, never which bytes*.

| Tool | Role | Acquisition |
|---|---|---|
| `assy-cli` | Subtitle alignment | **Built** here w/ PyInstaller, published as an asset of this plugin's release |
| `seconv` | OCR + hearing-impaired text removal | **Pinned** to Subtitle Edit's own release asset; nothing rebuilt or re-hosted |

**¬runtime update path, ¬version negotiation, ¬config setting for either executable path.** A pin moves only when a developer moves it. Each platform is a **separate release asset** and the plugin downloads only its own; the plugin zip is DLL-only. ! Putting a payload back in makes every user pay for every platform, at install and at every update.

! **A tool's `version` is the payload revision, ¬the upstream tool's.** `assy-cli` 2.0 freezes upstream 6.4; the upstream figure lives at `upstream.version` beside the tag. The two were the same number until the payload gained code of its own (→ `assy-entry/`), and splitting them is what makes a rebuild of the same tag shippable. ! **`PayloadStore` keys its cache on that version and never re-hashes an installed payload** → anything that changes the bytes must change the version, or every server that already fetched keeps the old payload for ever. `build-assy.ps1 -PayloadVersion <n>` sets it; the release is tagged `payload-v<version>`, so **the old releases stay exactly where they are** — a plugin still pinning 1.0 goes on downloading 1.0.

! **The UI names the bundled version too.** `PayloadTool.ToolVersion` renders `upstream.version` into the manifest and every user-facing string reads it → the status panel says *assy-cli 6.4*, ¬the payload revision. `Version` remains the cache key.

! **The archive filename carries the bundled tool's version, ¬the payload revision:** `assy-cli-6.4-win-x64.zip` on the `payload-v2.0` release. The name says what is *inside* the zip, the tag says which payload it is. `release.assetName` is `assy-cli-{upstream}-{rid}.zip` and `Expand-AssetTemplate` fills `{upstream}` from `upstream.version` → an upstream bump renames the archives, a payload-only rebuild does ¬. → two payloads freezing one upstream tag ship **identically named** archives under different tags; the plugin resolves by URL, which carries the tag, and verifies by SHA-256, so nothing downstream can confuse them. ! A human downloading both gets two files of one name — keep them apart by release.

`agentic/payload.lock.json` = single source of truth, one entry per tool under `tools`: upstream tag, version, and the name/SHA-256/size of every platform asset. `build-assy.ps1` + `pin-seconv.ps1` write it, `check-payload.ps1` verifies it, `verify.ps1` runs that check on every build. It makes six failure modes unshippable: a payload rebuilt without regenerating the manifest · a hand-edited manifest · a payload modified on disk after its build · a release cut w/ a platform missing · a pinned hash w/ no asset behind it · a pin silently left behind upstream.

! `Cli/PayloadManifest.g.cs` is **generated from that lock** and committed. Never hand-edit — a hand-written hash eventually says what someone wished were true, and it is the plugin's only trust root for a downloaded payload. `check-payload.ps1` re-renders it and fails on any difference.

```powershell
.\agentic\tools\build-assy.ps1 -ManifestOnly   # regenerate after editing the lock; no Python needed
.\agentic\tools\build-assy.ps1 -Tag v6.5       # upgrade; once per platform, PyInstaller can't cross-compile
.\agentic\tools\build-assy.ps1 -PayloadVersion 2.1  # rebuilt bytes, same tag; ! bump or nothing refetches
.\agentic\tools\pin-seconv.ps1 -Check          # exit 2 = upstream has moved on
.\agentic\tools\pin-seconv.ps1 -Tag v5.2.0 -Download
.\agentic\tools\fetch-seconv.ps1 [-Rid linux-x64]   # dev box only; the OCR harnesses need a local seconv
```

An assy-cli upgrade clones the tag, freezes it, asserts no ffmpeg was bundled, smoke-tests the binary, confirms every engine in `expectedEngines` is offered, stages the payload, zips it into `agentic/dist/`, records tag + commit + both hashes in the lock, regenerates the manifest. Then ① upload the archives as assets on the `payload-v<version>` release, ② re-verify the JSON contract fixtures, ③ cut a **minor** release ∵ the pinned dependency changed. Each run updates one entry under that tool's `payloads`; a release needs every RID in `requiredRids`.

`pin-seconv.ps1` resolves one upstream release and records each asset's name/SHA-256/size. ! **Always pass `-Download` when moving the pin** — it fetches every asset and recomputes each hash locally instead of trusting GitHub's digest; the lock records this as `verifiedLocally`. Nothing is uploaded, the assets stay on Subtitle Edit's release. Also a **minor** bump. `check-payload.ps1` ignores `agentic/payload/seconv/` ∵ seconv is pinned rather than built.

! **The lock is written by both platforms → everything that writes it must be edition-independent.** `payload-lock.psm1` hand-rolls JSON (`ConvertTo-StableJson`) ∵ `ConvertTo-Json` indents and escapes apostrophes differently across Windows PowerShell 5.1 and PS7; writes UTF-8 **without** a BOM via `Write-TextFile` ∵ `Set-Content -Encoding UTF8` emits a BOM on 5.1 only; and `Get-TreeHash` sorts ordinally + joins w/ an explicit LF ∵ `AppendLine` plus the culture-aware sort default hashed the same directory differently on Windows and Linux → the Linux payload then failed its own integrity check the moment a Windows machine verified it. ¬reintroduce any of these.

Per-RID `python`/`pyinstaller` live under `payloads.<rid>`, ¬`resolved` (which records whichever platform built last). `check-payload.ps1`'s `Test-PlatformsAgree` fails if the platforms were frozen from different tags, commits, or interpreter series, or if either disagrees w/ the tool's `buildPython` pin.

! **Two load-bearing `assy-cli` payload constraints:**

- Must be a **PyInstaller onedir freeze**, ¬a virtualenv. Upstream's `NEEDS_STATIC_FFMPEG` is false only when `sys.frozen` is set; without it the CLI downloads its own ffmpeg on first run. Onedir ¬onefile ∵ onefile unpacks on every invocation and this plugin invokes once per subtitle.
- Must **¬**bundle ffmpeg/ffprobe. If it does, upstream sets `FFMPEG_DIR` and prepends it, silently overriding the ffmpeg we hand it. The child gets Jellyfin's own ffmpeg on `PATH` via `IMediaEncoder.EncoderPath` → the bundle carries no second copy.

## The assy-cli contract

The interface most likely to drift, so it is written down rather than rediscovered:

- `sync <reference> <subtitle> -o <out> --json` → **one JSON object on stdout**, all logging to **stderr**. Capture the streams separately.
- `batch --json` → **NDJSON**: one object per pair, then `{"summary": {...}}`. Parse line-by-line, ¬as one document.
- Exit codes: `0` ok · `1` at least one sync failed · `2` usage/config error · `130` SIGINT. ! Exit 2 = the plugin built a bad command line → log the full argv at ERROR.
- Engines offered: `ffsubsync` (`.srt .ass .ssa .vtt`) · `alass` (`.srt .ass .ssa .sub .idx`) · `autosubsync` (`.srt` only). **The plugin dispatches to `ffsubsync` and nothing else**, always named w/ `-t` → *Why there is one engine* in `ARCHITECTURE.md`.
- `vad <video> --ffmpeg <path> --window <start>:<length> … --json` → **ours, ¬upstream's**: speech onsets from the `webrtcvad` already inside the freeze, one JSON object on stdout. Handled by `agentic/tools/assy-entry/`, which dispatches on `argv[0]` and hands every other name to upstream's `cli.main()` untouched. ! **No global option may precede it** — `--no-color`/`--config-file` ahead of `vad` miss the dispatch and upstream's argparse exits 2. The onset rule is a copy of `vadcheck/vad-onsets.py`; changing one without the other invalidates every measurement behind the fallback.
- The engine also prints its own VAD `score:`, `offset seconds:` and `framerate scale factor:` to stderr. `EngineAlignment` reads them; the score is used **only to refuse** an otherwise-unmeasurable title → V11 in `AUDIT.md`.

## Harnesses

Each exists ∵ something shipped wrong once. Most link the real source → they cannot drift from what ships. ! Run the relevant one whenever its subject changes; a drifted harness validates nothing.

| Harness | Invocation | Proves / why it exists |
|---|---|---|
| `simulate-concurrency` | `node .\agentic\tools\simulate-concurrency.mjs` | `AdaptiveConcurrency`'s control law converges. Asserts its constants match the C# → fails on drift |
| `check-rate-bound` | `node .\agentic\tools\check-rate-bound.mjs` | `MaximumRateDrift` admits every legitimate framerate conversion and rejects a rescale no framerate explains. Reads the constant out of `SyncOrchestrator.cs`. Also fails a drift landing **exactly** on the bound ∵ the ratio comes from integer-ms spans → a tie is decided by rounding, ¬by the rule. Exists ∵ the bound shipped at 25% against a list stopping at `30/25` = 20%, silently refusing the NTSC film-to-broadcast conversions → H1 |
| `acquirecheck` | `dotnet run --project .\agentic\tools\acquirecheck` | **The download feature w/out a network**: the shipped whitelist strings verbatim, the ask order `AdditionalDownloadProviders` imposes, the gap test (embedded fills the slot · an SDH track fills its language · the plugin's own output fills the one it bought · an unlabelled track makes the item ineligible), every pre-fetch filter, SDH **three** ways (advertised flag · the name a provider offers it under, incl. the *SDH Removed* inversion · the bytes), hash-match ranking, the per-item budget across providers, fall-through on every refusal the shared gates reach, per-provider **and per-source** retirement w/ the id cross-check behind it, and the ledger. Drives the real `SubtitleAcquirer` and the real `SdhDetector` over a `StubSource`. ! It must keep proving that a **filtered** candidate costs no budget and a **post-fetch** discard does |
| `verifycheck` | `dotnet run --project .\agentic\tools\verifycheck` | The audio check: window planning, shift fitting, scoring, rate error, unrelated onsets, too few onsets, displacement past the sweep. `--video <p> --subtitle <p>` runs the shipping check against real media · `--shift <ms>` applies a known displacement first · `--profile` prints the whole sweep, which is what an unmeasurable title looks like · `--correlate`/`--flux` keep the two rejected prototypes re-checkable (V9, V10) · `--vad <exe>` drives the **real payload** as the fallback detector, so the argv contract and the JSON reading are proved against the shipped binary rather than a stub. ! **`--mismatch` is the standing wrong-title control** — the single measurement the download feature rests on. It reads the untracked `calibrate.local.json`, pairs every video w/ every **other** title's subtitle, prints the matrix and **exits non-zero on one `Aligned`**. ¬in `verify.ps1`: five titles is twenty real audio reads over a network library, so it is run deliberately on a change to the check, the engine or a threshold. `--cases <path>` runs a subset. ! Expect mostly `Inconclusive` — the protection is abstention, ¬detection, and that is the finding. Also covers the centred bound, raw drift, and the second pass: consulted only on `Inconclusive`, never on `Misaligned`, and a second inconclusive returns the first result. Exists ∵ the first draft returned the low edge of the match plateau (systematic 250 ms bias) and the downmix landed after `silencedetect` instead of inside the filter graph → V3, V5, V6 |
| `calibrate.ps1` | `.\agentic\tools\verifycheck\calibrate.ps1 [-Mode none\|correlate\|flux] [-Only x] [-Shift ms]` · `-Vault` | Runs the fixed 5-title set (Mad Men S02E06, MPFC S01E02, Simpsons S01E10, TNG S02E02, Twin Peaks FWWM) → a verdict change is measured against titles whose behaviour is already recorded. ! Media paths are machine-local and live in **untracked** `verifycheck/calibrate.local.json` — `{ Id, Video, Subtitle }` per case; without it every case reports *not reachable*. The subtitle fixtures the vault held are **¬ in the repo** (third-party text); `fixtures.json` keeps the hashes so drift is still detected, and `-Vault` re-records a local copy. ! Those titles sit in the library **the plugin writes to** — a scan that syncs one silently replaces the evidence and the next run reads it as a code change, in either direction → X4 |
| `vadcheck` | `dotnet run --project .\agentic\tools\vadcheck -- --video <p> --subtitle <p>` · `sweep.mjs` · `analyse.mjs` · `audio-truth.mjs` · `authoring-floor.mjs` | **The measurement bench behind the voice-detection fallback and the centred bound**, ¬a pass/fail gate. `Program.cs` scores one title twice over the *same* window plan — shipping `silencedetect` onsets, then a real VAD — w/ everything downstream linked from `SyncVerifier` → a verdict change here is a verdict change in the plugin. `sweep.mjs` draws a population out of the record store by panel bucket, `analyse.mjs` reduces it to safety / recovery / drift / recall, `audio-truth.mjs` judges a title against **its own audio** (invariant ①, ¬an embedded track), and `authoring-floor.mjs` is where `TypicalLeadMs=170` came from. ! `vad-onsets.py` is the reference implementation of the onset rule — the payload's `assy_vad.py` is a copy of it, and the two drifting apart silently invalidates every number in `IDEA-VAD`. ! `audio-truth.mjs` reports a per-title standard error and refuses to call a reading measurable past 100 ms; an unweighted median over titles it cannot measure is how a floor gets mistaken for a constant |
| `orchestratorcheck` | `dotnet run --project .\agentic\tools\orchestratorcheck` | **The verify step's gate methods** — `StillOurOutput`, `IsStillCurrent`, `IsExhausted` and the retroactivity hooks behind them, against records whose history is known: a retimed and a created row still ours, a row the plugin never placed refused ∵ `Retimed` is the enum default, a replaced subtitle and a replaced video both refused, a demoted row carrying a successful offset **¬** reopened (U2), a shift the check would now refuse reopened, and every refusal a version could have stored unable to reopen itself (D11). Links the **whole plugin** as source ∵ the methods are `internal` and a copy would drift. ! `SyncOrchestrator` takes fifteen dependencies and cannot be constructed here — this reaches its **decisions**, ¬its pipeline; the two skip exits and the stretch guard are still covered by inspection alone |
| `check-verifier-error` | `.\agentic\tools\check-verifier-error.ps1 -Video <p>` · `-Show <dir> -Take N` | The check's reading beside `check-vs-embedded` ground truth. Exists ∵ the gap between them was recorded as a subtractable "display lead" off two data points, and a third and fourth killed it → W1 |
| `check-inconclusive` | `.\agentic\tools\check-inconclusive.ps1 -Log <p> [-From n]` · `-Pairs <csv> [-Take n]` | **Which** of `BestShift`'s three gates refused a title, over the ones a field log could not measure. Reads hits/floor/onsets out of the shipping verdict → ¬drift. Separated the floor-limited titles from the genuinely flat ones → X2. ! It measures the **pre-sync sidecar** where the log records the **post-sync** verdict — ¬use it to attribute field rejections; the log line now carries them itself → X1, X3 |
| `check-stretch` | `.\agentic\tools\check-stretch.ps1 -Log <p>[,<p>] [-Take n] [-Csv <out>]` | The **magnitude** of the rate change behind each stretch-guard refusal, ∵ the guard logs a millisecond figure alone and 1261 ms is a defect on a clip but exact pulldown on a 21-min episode. Sidecars only, no video → cheap against a network library. Ranked the refusal buckets → Z2. ! **The Conversion column is ¬evidence of correctness** — ffsubsync picks its rate from a fixed list of standard ratios, so every output lands on one, incl. output from another show's subtitle → Z1 **[R]**. ! Reconstructs from the logged figure ÷ the *live* sidecar's span → approximate, sign unrecoverable (`Measure` returns `Math.Abs(slope)`), and wrong outright for any title a later run rewrote |
| `check-stretch-outcome` | `.\agentic\tools\check-stretch-outcome.ps1 -Pairs <csv>` · `-Log <p> -Take n` | The refused run reproduced end to end: the check's verdict on the original, the engine's declared scale + score, then the check's verdict on what the engine produced. The last column is the only one that decides anything, ∵ the plugin deletes the produced file at :395 and the log cannot be asked. ! Reads whole video audio twice per title — keep the sample small. ! `$ErrorActionPreference = 'Continue'`, ∵ PS 5.1 wraps a native exe's stderr in `ErrorRecord`s and ffsubsync logs its whole run there |
| `scorecheck` | `.\agentic\tools\scorecheck\run.ps1` | Runs a real sync and reports the engine's own score per second of displayed subtitle. Produced the separation behind `MinimumEngineScore` → V11 |
| `measurecheck` | `dotnet run --project .\agentic\tools\measurecheck` | `SubtitleOffsetProbe.Measure` reports what the engine actually did: pure shifts, PAL stretch, dropped leading markers + trailing credits, thinned cues, unmatchable/repeated text falling back to endpoints, ASS centiseconds. Two paths measures those instead. Exists ∵ the first cue matcher leaked end-timestamp digits into its keys → every shifted case fell through to the endpoints → P4 |
| `rollbackcheck` | `dotnet run --project .\agentic\tools\rollbackcheck` | Rollback restores the library and refuses what it can't prove: retimed file restored from the vault · created file deleted · unmarked file untouched · removed duplicate restored · both paths deduplication's rename opens (a renamed survivor **w/** a backup restores under the old name, one **without** is only named back) · a failed record keeps its row ∵ that row is the only pointer to its backup · a kept download deleted like anything else created · **a row whose candidates were all refused keeping its row and its ledger**, ∵ rollback undoes files and cannot un-buy a download. Nothing else covered `RollbackService`, the one component that deletes user files |
| `stalecheck` | `dotnet run --project .\agentic\tools\stalecheck` | `RecordReconciler`, the thing standing between the status panel and a count no run would produce again: an offered target left alone · a failed row for a vanished subtitle removed · a row holding a backup **kept** and uncounted · a `Created` output still on disk keeping the row rollback needs · a returning target counted again · deduplication's renamed survivor matched by path ¬key · an item outside the enabled libraries uncounted · a stale row `ReopenFailed` refuses to queue · **a retired row off the cards but still on the stage table**, left alone by `Reconcile` and refused by `ReopenFailed` · a downloaded subtitle through its whole life — offered w/ nothing bought, kept and placed, deleted by the user, its library leaving scope. ! **A download stops being offered ∵ it succeeded**, so the placed file is what keeps its row live. Exists ∵ the panel is the only view a user has → *The status panel invariant*. ! Matching on `TargetKey` alone reports the renamed survivor gone; deleting every unoffered row strands its vault copy |
| `gatecheck` | `dotnet run --project .\agentic\tools\gatecheck` | `ItemChangeGate` both directions: a refresh provoked by the plugin's own writes is absorbed; an edited sidecar, replaced video, added/removed sidecar, and every retroactive setting reopen the item. Exists ∵ `RefreshItemAfterSync` fed the event handler its own output → a full scan grew a second uncontrolled wave behind itself. Neutering `Commit` reproduces the loop; dropping the offset bounds from the gate stamp fails the retroactivity cases |
| `dedupecheck` | `dotnet run --project .\agentic\tools\dedupecheck` | `SubtitleSimilarity` against pairs whose relationship is known — reflowed, re-split, restyled, retranslated, OCR'd, forced-cut → 0.85 is measured, ¬guessed. Two paths scores those instead. Caught the defect scoring a differing ASS style definition at 99.4%: one declaration outvoted by a per-cue token per cue → formatting is the **worse** of declarations and usage, never a blend |
| `killcheck` | `dotnet run --project .\agentic\tools\killcheck -- --exe <assy-cli> --video <p> --subtitle <p> --out <p>` | Cancelling a sync reaps its children. `ffsubsync` runs its engine through `multiprocessing` → each worker is another `assy-cli`, and `Process.Kill(entireProcessTree: true)` is all that stands between a cancelled task and a machine full of orphans. Green on win-x64: 4 descendants incl. a worker + an ffmpeg, all reaped. **Windows-only** — reads the process table via WMI ∵ .NET exposes no parent pid |
| `synccheck` | `node .\agentic\tools\synccheck\run.mjs --video <mkv> --truth <srt>` | Scores an engine against a correctly-timed subtitle: identity / fixed shifts / PAL 25↔23.976 stretch → median/p90/max start error, share within 150/500 ms, gaps welded shut. `engine@preset` writes a JSON config passed as `--config-file`. Established that `alass` + `autosubsync` had to be dropped → *Why there is one engine*. Still runs all three so that case stays re-checkable on a pin bump |
| `formatcheck` | `node .\agentic\tools\formatcheck\run.mjs --truth <srt> --video <f>` | Which formats/codecs/containers an engine accepts at all. Writes the same cues as SRT/ASS/SSA/VTT/MicroDVD, applies a known shift, scores what returns → a rejected format and an accepted-then-wrong format are distinguishable. `--media <dir>` sweeps; `make-media.ps1` builds that directory varying only codec or container |
| `check-sync-output` | `node .\agentic\tools\check-sync-output.mjs --records <records.json> --vault <dir>` | Validates shipped syncs against their vault backups: fitted shift + rate vs. the recorded value, hearing-impaired marker counts, dialogue retention, cue ordering, last cue vs. the video's real runtime. `--vault` remaps the server's `BackupPath`. Prefers the vendored `ffprobe` |
| `langcheck` | `dotnet run --project .\agentic\tools\langcheck` | `LanguageCodes` against two- and three-letter forms, `/B` vs `/T` variants, region qualifiers, and the country codes users mistype. Also `TesseractLanguage`: tessdata names ¬ISO codes, ! the five script-chosen models surviving where `Normalize` would have lost the subtag — incl. `sr-Latn`, whose unsuffixed model is the Cyrillic one — and the ISO placeholders getting **no** language flag |
| `vobsubcheck` | `dotnet run --project .\agentic\tools\vobsubcheck [-- <real .idx>]` | `VobSubIndex` + `VobSubStaging`: every declared stream read w/ its own index, a split carrying the header and exactly one stream, a mid-list stream, an undeclared stream refused, an index past the line cap refusing rather than truncating, the payload staged **once** per file w/ a `.sub` beside each split index, two streams staging side by side, a live staging surviving the sweep. ! The link case writes a byte to the payload and reads it back through the pair — a **copy** passes every other check and silently restores the 2.9 GB-per-film cost → A1. Links both real sources → ¬drift. Exists ∵ a 24-language `.idx` is 24 tracks wearing one filename and seconv converts every one of them into a single file, dying on the timeout first → Z4. Passing a real `.idx` prints its stream table |
| `ocrcheck` | `dotnet run --project .\agentic\tools\ocrcheck [-- <srt>]` | `OcrReadability`, the gate that refuses OCR output nobody could read. Links the real source → ¬drift. ! The nine measured subtitles behind it — five real sidecars, two failed colour-isolation reads, the same two read correctly — are **¬ in the repo** (third-party text); those cases report *skip (fixture absent)* until `<name>.srt` files are dropped into `ocrcheck/fixtures/`. The synthetic cases always run. ! The two bounds are **and**ed, ¬or'd — the mean-length bound alone sits within reach of a language of short words, and one check fails if a sample failing a single signal is refused. Also fixes that a CJK track and a six-caption forced track are left **unjudged**, ¬refused, and that timing lines never count as words. Exists ∵ every gate downstream judges timing, and OCR timings come from the index → all of them passed text that was noise → C1 |
| `check-ocr` | `.\agentic\tools\check-ocr.ps1 -Sub <p> -Stream N [-Truth <srt>] [-Raw]` | The OCR link executed end to end: the real stager, the pinned seconv, then word statistics against a reference sidecar. Exists ∵ everything up to the seconv call was verified and the call itself never was → C1, which it found. ! Score w/ `-Raw`; `--fix-common-errors` rewrites unreadable glyphs into plausible characters. **Real dialogue runs ≈4.5 chars a word w/ ≈0% all-caps; noise runs under 3 w/ half its tokens one or two characters.** Needs `fetch-seconv.ps1` and a local Tesseract |
| `supsample` | `.\agentic\tools\supsample\make-sup.ps1 -OutPath x.sup -Style Solid\|Outline` | Writes a valid PGS `.sup` from rendered text → an OCR claim is measured against known ground truth w/ typography as a controlled variable (*Step 22d result*). **Windows-only**, rasterizes via `System.Drawing` |
| `supsample/score` | `node .\agentic\tools\supsample\score.mjs` | Scores OCR against a reference: exact-cue count + character error rate (*Step 22e result*). Point at a directory of `<sample>-<engine>-<variant>.srt`. ! **Score raw OCR output**, never output through `--fix-common-errors` — that pass rewrites the `*` placeholder into plausible characters and hides exactly what the score measures |

`payloadcheck`, `storecheck`, `namingcheck`, `subcheck`, `placecheck`, `check-comments`, `verify.ps1` → `AGENT-HANDOFF.md`.

## Commit messages

! **Never write one from memory of the session.** Read the **actual history** and the **actual diff** first, every time. A message written from what an agent remembers doing describes the work it *meant* to do, in a voice the log does not use — and neither error is visible to the person approving it.

```powershell
git log --format='%H%n%B%n=====' -12   # the voice: subject, bullet style, wrapping, trailer
git status --short                     # what is in the commit
git diff                               # what each file actually did
```

- **Subject:** imperative, sentence case, no full stop, ≈70 chars. Names the effect, ¬the refactor.
- **Bullets:** one per change, verb first, wrapped ≈75 chars w/ a two-space hanging indent. Terse — ¬rationale paragraphs, ¬background on why the old code was wrong. Explanation belongs in `agentic/ARCHITECTURE.md` or the design document, on the same reasoning as the no-documentation-in-comments rule.
- ! **Only what the diff contains may appear.** Writing bullets for work that is not in the commit has happened more than once. `agentic/` is inside the repo → harness and doc changes **do** belong, when they are in that commit.
- **Audit findings get one bullet**, `Fix the audit findings <range>`, listing only the ones actually **[F]** — an accepted or open finding is ¬fixed, and a range that swallows one claims a fix that was never made.
- **Trailer:** the `Co-Authored-By:` line the log already carries.

! **Hand the message over, ¬run it** → *Never commit or push without being asked*, below. The release commit is the one exception to all of the above: it has a **fixed** message, → *The release commit stands alone*.

## Release process

Jellyfin uses 4-part versions (`1.0.4.0`).

> ! **Do not perform any step below, or any part of one, unless the user explicitly asks for a release.** ¬bump a version "ready for" one, ¬pre-fill a `manifest.json` entry, ¬commit or push at any point without being asked. Completing a feature triggers none of this.

| Bump | When |
|---|---|
| **Patch** `1.0.x.0` | Bug fixes, sync accuracy improvements, a setting that only tunes an existing one |
| **Minor** `1.x.0.0` | New features, a config option that adds a capability, either pinned dependency moving |
| **Major** `x.0.0.0` | Breaking changes, full rewrite/rerelease |

! **A new checkbox is ¬automatically a minor bump.** What decides it is whether the release adds a **capability**, ¬whether an option appeared on the page. A setting that only chooses how a released build behaves on a path it already had — a threshold, a stricter or looser gate — is a **patch**, however new the control is. ! Judge it against the **last release**, ¬the last commit: a setting added and reworked several times before it ever shipped is still one arrival. → **1.6.2.0 is the worked example**: `RequireConclusiveDownloads` was a first-time control, but the download path shipped in 1.6.0.0 and the setting only picks how strict its existing audio gate is, so it went out as a patch.

### ! The release commit stands alone

Its own commit, containing nothing but the version bump + the manifest entry — never a logic change, and a logic commit never carries a version bump. Exactly three files: `Jellyfin.Plugin.AutoSubSync.csproj`, `build.yaml`, `manifest.json`. Fixed message:

```text
Release 1.1.2.0

- Bump AssemblyVersion, FileVersion, and build.yaml to 1.1.2.0
- Add the 1.1.2.0 manifest entry
```

→ `git log --oneline` reads as a release timeline, and a release can be reverted without taking working code with it.

! **The work is committed before step 0.** Every step edits one of those three files → a version bumped while the work is uncommitted lands in the work commit and destroys the split. Order: finish work → `verify.ps1` → **the user** commits → *then* step 0. Until that commit exists the working tree stays at the **previous** version. Bumped out of order → `git checkout -- build.yaml Jellyfin.Plugin.AutoSubSync.csproj`.

**0.** Check `README.md` for needed updates (new features, changed defaults, renamed tasks, new options). **Decide whether either pinned dependency moves** — a move makes this **minor**, ¬patch.
  - `assy-cli` moved → `build-assy.ps1 -Tag <new>` on every required platform *before* continuing, then upload `agentic/dist/` archives to the `payload-v<version>` release. ! **Publish these before the plugin release** — a manifest pinning a hash w/ no asset behind it installs and can never sync anything.
  - `seconv` → `pin-seconv.ps1 -Check`; exit 2 → `-Tag <new> -Download`. Nothing uploaded. The release gate fails on a stale pin → leaving it is a deliberate decision, ¬a skipped step.

**1.** `AssemblyVersion` + `FileVersion` in `Jellyfin.Plugin.AutoSubSync.csproj`. Jellyfin reads the version from DLL assembly metadata → skip this and the dashboard reports the old version. **Also `build.yaml`** in the same commit: `version`, `changelog`, `targetAbi`, `framework`. No tooling reads that file (¬CI, ¬JPRM) — which is exactly how it silently drifts.

**2.** Build Release + run the gate, both from the **repo root**:
```powershell
dotnet build .\Jellyfin.Plugin.AutoSubSync.csproj -c Release
.\agentic\tools\verify.ps1 -ReleaseMode
```
! Name the `.csproj` explicitly. A bare `dotnet build -c Release` at the repo root fails ∵ there is no solution file and `agentic/tools/` holds many other projects. Steps 3–4 use paths relative to the repo root.

`-ReleaseMode` promotes payload warnings to failures: a required platform w/ no payload · a hash not matching the lock · a payload built from a different tag than the lock pins · a stale or hand-edited `PayloadManifest.g.cs` · a pinned archive w/ no asset behind it · a pin behind upstream · `manifest.json`'s newest entry ≠ the version being built · any entry whose `sourceUrl` no longer downloads. ! That last check covers **every published entry**, ¬just the newest: withdrawing a release leaves its entry behind and Jellyfin then offers a 404 to anyone pinned to it — which is how 1.1.2.0 outlived its release. ¬proceed past any failure here; each ships a broken plugin.

The gate's last check compares the newest manifest entry against the version being built, and **step 5 is what adds that entry** → this run always ends on `manifest.json newest entry is '<old>' but the build is '<new>'`. That one failure is expected; every other check must pass. ! **Re-run `verify.ps1 -ReleaseMode` after step 5** and require it fully green before committing — narrowly, ∵ step 5 edits `manifest.json` alone and no harness, the linter or the build reads that file:

```powershell
.\agentic\tools\verify.ps1 -ReleaseMode -SkipBuild -SkipLint -SkipHarness
```

! The **full** run still belongs at step 2 and before every commit, where code has changed — the harnesses are the only thing standing behind `RollbackService`, `RecordReconciler` and the audio check. Narrow it only for the metadata-only steps after step 2. The entry being released is **skipped** w/ `not published yet (this release)` ∵ its asset can't exist until step 7; the next release checks it. → after step 7, ¬re-run the gate to confirm the new URL, which it will skip for exactly that reason; download the published asset and compare it against the manifest instead:

```powershell
Invoke-WebRequest -Uri <sourceUrl> -OutFile $tmp -UseBasicParsing
(Get-FileHash $tmp -Algorithm MD5).Hash.ToLower()   # must equal the checksum in manifest.json
``` Abandoned after step 5 → remove its manifest entry rather than leaving it for that check. The uploaded-asset and stale-pin checks need `gh` authenticated against the plugin repo and fail closed when network or `gh` is unavailable.

**3.** Zip — DLL only; the payload is fetched at runtime and is never part of it:
```powershell
Compress-Archive -Path "bin\Release\net9.0\Jellyfin.Plugin.AutoSubSync.dll" -DestinationPath "bin\Release\autosubsync-v{VERSION}.zip"
```

**4.** MD5 **the zip**, ¬the DLL: `(Get-FileHash "bin\Release\autosubsync-v{VERSION}.zip" -Algorithm MD5).Hash.ToLower()`

**5.** `manifest.json` — new entry at the **top** of `versions`: `version` (4-part) · `changelog` (bulleted, → *The changelogs are bulleted*) · `targetAbi` = the **minimum** server version supported, ¬the one built against (Jellyfin hides the plugin below it) = `10.11.0.0` matching `build.yaml`, raised only when something genuinely needs a later 10.11.x · `sourceUrl` = `https://github.com/AdamHarrison99/jellyfin-plugin-autosubsync/releases/download/v{VERSION}/autosubsync-v{VERSION}.zip` · `checksum` = step 4 · `timestamp` = ISO 8601 **with time**, actual current UTC, ¬midnight.

**6.** Commit + push to `master` — the second of the two commits. `git show --stat` must list exactly three files.

**7.** `gh release create v{VERSION} "bin\Release\autosubsync-v{VERSION}.zip" --title "v{VERSION}" --notes "changelog"`. Release notes = bullets only; requirements and install steps live in `README.md`.

Pushing `manifest.json` to `master` is what triggers installs and updates — that file is what servers poll.

### ! The changelogs are bulleted

Three surfaces carry the same list — `build.yaml` (step 1), the `manifest.json` entry (step 5) and the release notes (step 7). Write it **once, as bullets**, and reuse it in all three. The manifest holds it as one JSON string → join the bullets with `\n`, each still opening with its `-` marker.

- **One bullet per user-visible change**, a line or two each. Effect first: what an admin now sees, gets or no longer has to do.
- ¬a class name, ¬a setting's property name, ¬a finding id, ¬the refactor behind it. That is the commit message's job, and the two audiences are ¬the same.
- ! **¬a prose paragraph, in any of the three.** The manifest string is what the dashboard shows someone deciding whether to update → a wall of sentences is ¬read, and an update worth taking then looks like one worth postponing.

## Pre-release audit

Before every release, audit the whole codebase. ! Read `AUDIT.md` first → ¬re-flag known false positives.

1. Security: injection, path traversal, unsafe deserialization, OWASP top 10
2. Efficiency: N+1, unnecessary allocations, redundant I/O, blocking async calls
3. Race conditions in event handlers + concurrent operations
4. Filesystem operations have proper safety checks
5. API endpoints: authorization + input validation gaps
6. **Process spawning** — ¬shell command strings; every child has a timeout and a kill-the-tree path; ¬unbounded fan-out
7. **Write scoping** — every write/delete lands inside a resolved library root and is either a file the plugin created, a backup it made, or a user file already in the vault. ! Confirm the vault copy **gates** the destructive step rather than merely preceding it
8. **Dry run integrity** — trace every filesystem call **and every outbound network call**, confirm unreachable while `DryRunMode` is on. A filesystem leak silently modifies a user's library; a network leak silently spends their provider allowance. ! Tracing writes alone passes a build that downloads during a dry run
9. **Rollback correctness** — restores backups before deleting outputs; never deletes a file it can't prove it created
10. **Comment standards** — comments are ¬documentation; run the linter, then read them by hand (the script catches wording + run length, ¬whether a comment states *why* or has gone stale)
11. **No personal data in anything published** — `agentic/` ships **inside the public repo**, so every file in it is world-readable at the next release. Sweep the whole tracked tree, ¬only the diff

### ! The personal-data sweep (audit step 11)

**What may never appear in a tracked file**, in `agentic/` or anywhere else: a **real name** · a **hostname**, share name or UNC path · an **IP address** · a **machine-local path** (drive letter, `C:\Users\…`, `/home/<user>`) · a **quotation of anything the user said**, ¬even paraphrased close · **third-party subtitle or video content**, incl. fixture text · an account name, email, token or key.

! **A release is the moment this becomes irreversible** — git history keeps what a later commit deletes, so the sweep belongs *before* the release commit, ¬after someone notices.

Five patterns, run separately over **tracked files only** — ¬one combined regex; the combined form is unreadable, and its hits cannot be triaged by class. Verified against this tree:

```powershell
$pats = [ordered]@{
  'drive-letter path' = '[A-Za-z]:\\[A-Za-z0-9_ .-]+'
  'UNC path'          = '\\\\[A-Za-z0-9._-]{2,}\\'
  'unix home'         = '(/home/|/Users/)[a-z]'
  'private IPv4'      = '(192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.|169\.254\.)[0-9]{1,3}\.[0-9]{1,3}'
  'address:port'      = '[0-9]{1,3}(\.[0-9]{1,3}){3}:[0-9]{2,5}'
}
foreach ($k in $pats.Keys) {
  $hits = git grep -nIE $pats[$k]
  Write-Output "=== $k : $(@($hits).Count) hits ==="
  $hits
}
```

! **Do ¬grep for bare IPv4 in this repo.** Jellyfin ABI and plugin versions are 4-part (`10.11.0.0`, `1.5.1.0`) → a general IPv4 pattern returned **115** hits, every one a version number, which is how a real address hides in a list nobody reads. The private-range and `address:port` forms above are the ones that carry information.

! **Run these in PowerShell, as written.** Transcribed into bash the backslash forms either error or match nothing, and every pattern reports **0 hits** over a tree that is full of them — a sweep that reads clean because its regex never matched is worse than no sweep. A correct run over this tree returns tens of drive-letter hits and a handful of UNC ones, all benign; a **0** on either is the transcription failing, never the tree being clean. ! **The sweep covers *tracked* files only** — a new file git has not been told about is invisible to it, so sweep the untracked ones by hand before the commit that adds them.

Then **read** what returns — a hit is ¬automatically a defect and a clean run is ¬automatically a pass. Known-benign classes, all present today:

- **Regex literals.** `score:\s*`, `silence_end:\s*` — `e:\s` looks exactly like a drive path. The largest false-positive class by far.
- **Standard install locations that name no machine**: `C:\Program Files\Tesseract-OCR`, `C:\Program Files\Jellyfin\Server`, `<ProgramData>\Jellyfin\…`. These are in the code deliberately as probe paths.
- **Synthetic fixture paths** in the harnesses: `C:\m\Movie (2001).eng.srt`, `C:\media\Movie (2001)\…`.
- The `\\<server>\…` placeholder, and `127.0.0.1`.

**Never caught by any grep, so check by eye** — this is the half that matters: a real name · a user quotation · a library, share or machine *name* · subtitle text quoted as an example · a list of titles that discloses a private collection · an account or email.

! **Confirm the untracked files that legitimately carry local paths are still untracked**, w/ `git check-ignore -v <path>` — *"it is not in `git status`"* proves nothing, ∵ an **already-tracked** file stops appearing there. Covered today: `verifycheck/calibrate.local.json`, `agentic/payload/`, `agentic/dist/`, `agentic/tools/ffmpeg/`.

! **The third-party-fixture rule is `agentic/tools/*/fixtures/*.srt` — `.srt` only.** A fixture dropped in as `.ass`, `.vtt`, `.sup`, `.sub` or `.idx` is **¬ ignored** and commits third-party subtitle content on the next `git add -A`. Widen the rule before adding a fixture in any other format.

! **A machine-specific note is ¬deleted, it is relocated** — it goes to the agent's own private memory outside the repo, along w/ the rule saying so. Deleting it loses what the next agent needs.

! **Do not commit code or cut a release until all findings have been presented to the user for review** — each w/ location, severity, proposed resolution; proceed only after approval.
! **Always update `AUDIT.md` with results immediately after completing an audit — do not ask for confirmation first.** Then check whether `README.md` needs updating for anything added since the last release.

## Notes

- Scheduled tasks are auto-discovered via `IScheduledTask` → ¬DI registration.
- Rollback and "clear database" are config-page buttons backed by API endpoints, ¬scheduled tasks.
