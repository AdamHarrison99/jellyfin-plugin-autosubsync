# Architecture

**What the built system does, and why it is built that way.** Home for every "why" and every rejected alternative concerning code that **exists**. None of it belongs in a code comment → `AGENT-HANDOFF.md` for the rule + linter. Rationale for work **¬yet built** → the design document. ¬changelog, and none is to be created.

> Key: `→` leads to / therefore · `∵` because · `¬` not · `!` trap, do not violate · `w/` with · `≈` about.

See also: `../JellyfinPlugin-AutoSubSync plan.md` (design + phasing) · `AUDIT.md` (findings, cited below as `H1`, `V9`, `M1`, …) · `IDEAS.md`.

---

## Pipeline overview

```text
FullLibrarySyncTask ─┐                        ItemChangeGate (event path only)
LibraryEventHandler ─┼─> LibraryScopeResolver ─> SubtitleDiscoveryService ─> SyncOrchestrator
POST /SyncItem      ─┘                                                              │
                                                                                    v
                                    SyncQueue ─> AssyCliRunner ─> assy-cli (fetched payload)
                                                                                    │
                                                                                    v
                                                       SubtitlePlacer ─> BackupVault
                                                                                    │
                                                                                    v
                                                                SyncStore (JSON, per target)
```

`POST /RollbackAll` runs the graph backwards: `RollbackService` reads the store, restores from `BackupVault` or deletes what the plugin wrote, removes the rows.

**Per-target order in `SyncOrchestrator`:** ⓪ take the lease (`TargetLocks`, before the record is read) → ① skip if current (`IsStillCurrent`, ahead of any extraction) → ② produce an input (sidecar path, or ffmpeg extraction) → ③ Convert (OCR, bitmap targets only) → ④ read the audio (one `SyncVerifier.SampleAsync`, shared by ⑤ and ⑧) → ⑤ does it need syncing? (a subtitle the audio agrees with never reaches the engine) → ⑥ Sync (one `ffsubsync` run) → ⑦ bound the change (`MaximumRateDrift`; `MinimumMovementMs`) → ⑧ Verify (same sample; ahead of the transform so it reads the cues the engine placed) → ⑨ Transform (strip HI) → ⑩ Place → ⑪ `IProviderManager.QueueRefresh`.

## Cross-cutting rules

These recur in a dozen components; they are stated once here and cited rather than re-argued.

- ! **Failure is biased toward keeping work.** Unmeasurable → sync and keep. An unparseable timestamp leaves its bound untested rather than refusing. A cosmetic pass that fails returns the good sync unchanged. ∵ the errors are asymmetric: a wrong refusal deletes a correct subtitle, an over-permissive one costs a re-sync.
- ! **Nothing throws out of `ProcessAsync`.** One bad file must never abort a library sweep. Every store write goes through `SafeUpsert`, which swallows + logs — `ProcessAsync` writes records from inside its own catch blocks.
- ! **Settings are retroactive**, by two mechanisms → *Models/* below. Anything gating work must participate in one of them or the guarantee silently stops at that gate.
- ! **Provenance decides restore-vs-delete**, and is stored, never inferred → *`RollbackService`*.
- ! **Dry run is a filesystem lock on the media library**, ¬a logging mode. The plugin's own record store is still written. Re-verify unreachability whenever a new pipeline entry point is added.

---

## Why there is one engine

assy-cli offers three; the plugin dispatches to `ffsubsync` and only `ffsubsync`. Measured 2026-08-14 w/ `synccheck` against subtitles known correctly timed — the harness transforms the truth, feeds it to the engine, scores the output back against the truth → the right answer is always known exactly. Figures = absolute start-time error in ms, matched cue by cue.

| title | case | ffsubsync | alass | autosubsync |
| --- | --- | --- | --- | --- |
| Breaking Bad S02E03 (2008, stereo) | identity / +5s | 250 | 237 | 300 |
| Batman: The Movie (1966, mono, PGS-only) | identity | 60 | 18738 | 14100 |
| Batman: The Movie | ±5s / +30s | 60 | 18611 / 18738 | 14100 |
| Batman: The Movie | PAL stretch | 59 | 18503 | 14100 |
| Aladdin (1992, animated musical) | identity / +30s / stretch | 0 / 0 / 0 | 49 / 49 / — | 0 / — / — |
| The Lego Movie (2014, 5.1) | identity | 0 | 192 | 50 |
| The Lego Movie | +30s | 0 | 199 | **7438** |
| The Lego Movie | PAL stretch | 1 | **209711** | 100 |
| The Apartment (1960, mono) | identity / +30s / stretch | 100 / 100 / 100 | — | — |

`ffsubsync` was correct on every title and case, worst error 100 ms. The other two were each catastrophically wrong on ≥1 title, and `alass` welded 1–3 real gaps shut on Batman — unrecoverable damage, ¬a fixable offset. What makes that disqualifying rather than merely worse:

- **They fail silently.** Both return `ok` and write a file. On Batman each returned the *same* wrong answer whether the input was already correct, shifted 5s or 30s, or stretched — input timing did not influence the result at all.
- **A chain only advances on failure** → since neither ever reports failure, a fallback chain doesn't protect against them; it hands them the file whenever `ffsubsync` genuinely fails, and they overwrite a working subtitle w/ an 18-second error.
- **Tuning doesn't rescue them.** `alass`'s `split_penalty` at upstream default 7 → ≈18.7 s error; at `-1` (`--no-split`) and at 60 → ≈125 s, those two agreeing within 13 ms. Splitting was camouflage for a bad global alignment, ¬the defect.

They cost nothing to lose: `autosubsync` reads only `.srt`, a strict subset of `ffsubsync`'s `.srt/.ass/.ssa/.vtt`. `alass` added exactly one format `ffsubsync` can't read — MicroDVD `.sub` → which is why `.sub` is no longer discovered at all; `IDEA-SUBCONV` in `IDEAS.md` is the conversion route that would restore it without a second engine.

**The gap this left.** Rejecting a result that moved *too little* is half a guard; nothing asked whether a result that moved a lot moved to the *right place*. `MaximumOffsetMs` was the wrong instrument — it bounded the size of the movement, and size is not the thing that's wrong. Bambi II proved it: a correctly-timed subtitle dragged 1490 ms early, far inside the one-minute bound → accepted. That bound is deleted; `SyncVerifier` replaces it. `MinimumMovementMs` stays and means what it always did: below it the result is a no-op, record reads `Skipped`, ∵ nothing was wrong.

! **Unless the no-op is the plugin re-reading its own work.** Two exits close a target w/out writing — the pre-check finding it already aligned, and the engine moving it under `MinimumMovementMs` — and both stamped `Skipped` regardless of who wrote the file being read. → the first run after a `CheckRevision` bump migrated the plugin's entire synced library onto the *skipped* card, ∵ a correctly synced subtitle is exactly one the check leaves alone and the engine cannot improve. The cards describe **the library as it is now**, and as it is now those subtitles are synced, by this plugin, w/ a vault copy behind each one → `StillOurOutput` decides it: a `BackupPath` or an explicit `Created` provenance, **and** `FingerprintMatches`. Both true → the row stays `Synced` and the skip reasons are cleared; either false → `Skipped` as before → **U1**.

- ! **Provenance alone can ¬prove it.** `SubtitleProvenance.Retimed` is `0`, the default on a row that was never placed → `Provenance == Retimed` is true of every record ever created. Only a non-null `BackupPath` or `Created` is positive evidence.
- ! **The fingerprint half is what keeps it honest, and it proves less for `Created` than for `Retimed`.** A user who replaces the plugin's sidecar w/ a different one that happens to be aligned gets `Skipped: already aligned`, ¬a claim the plugin synced it — this is the *"unless that sub or its media is changed"* half of the rule, and it is `IsStillCurrent`'s own comparison, spelled once. ! **`RefreshSourceFingerprint` runs only on a `Retimed` placement**, so `SourceSha256` is the **output's** hash there and the **source's** everywhere else → for a side-by-side or extracted sidecar the test reads *this target is unchanged*, ¬*our output is untouched*. A user editing a `Created` sidecar leaves the row `Synced`. ¬a regression: `IsStillCurrent` compares the same field, so editing that file never reopened the record under any version → **AA1**, wording corrected rather than behaviour.
- ! **A demoted row is unreachable w/out a lever.** Once it reads `Skipped` w/ a matching stamp and fingerprint, `IsStillCurrent` short-circuits it for ever — fixing the exits does nothing on its own. `CheckRevision` → `check3` is what reopens them.

---

## Cli/

### `PayloadManifest` (generated)

Rendered from `agentic/payload.lock.json` by the build tooling and committed. One `PayloadTool` per vendored tool — name, binary name, version, compiled-in base URL, and SHA-256 + size + archive format per RID.

Generated ¬hand-maintained ∵ those hashes are the plugin's only trust root for a downloaded payload, and a hand-written hash eventually says what someone wished were true. `check-payload.ps1` re-renders from the lock and fails on any difference → assembly and lock cannot drift.

`acquisition` records how each tool is obtained. `assy-cli` is **built** here (PyInstaller freezes an upstream tag, once per platform; the archive is an asset of this plugin's release) — a freeze of upstream's CLI **plus** an entry wrapper of ours, → *The payload entry wrapper*. `seconv` is **pinned**: Subtitle Edit already publishes per-platform binaries w/ a SHA-256 per asset → the plugin downloads upstream's archive directly, nothing rebuilt or re-hosted. Pinning gives the same trust property w/ no build step, no re-hosting, no per-platform build machine; the cost is that upstream ships Linux as `.tar.gz` → the fetcher understands two archive formats. `pin-seconv.ps1 -Check` reports a newer upstream release, and the release gate runs it → a stale pin is a decision, ¬an oversight.

**Version, tag and filename are three different strings.** `PayloadTool.Version` is the payload revision (`2.0`) and keys the on-disk cache. The **tag** is `payload-v<version>`, and the compiled base URL ends in it. The **archive name** is `assy-cli-<bundled version>-<rid>.zip` — the version of the tool inside the zip (`6.4`), ¬the payload revision, ∵ a filename should say what it holds. A payload rebuilt against the same upstream tag therefore carries the same archive name under a new release tag; the URL disambiguates them and the SHA-256 proves which one arrived.

! **The panel and the logs name the bundled version, ¬the payload revision.** `PayloadTool.ToolVersion` carries `upstream.version` from the lock (falling back to `version` for a tool pinned rather than built, where the two are one number) and is what every user-facing string reads — *"assy-cli 6.4 is ready"*, ¬*"assy-cli 2.0 is ready"*, which names a number nothing upstream has ever published. `Version` stays the cache key and appears in the install log beside it, ∵ that is the figure a stale cache is diagnosed by. ! The generator renders that field, so it is **ASCII only** — PS 5.1 reads the BOM-less `payload-lock.psm1` as ANSI and a `¬` in the emitted comment comes out mojibake on Windows and correct on Linux, which is exactly the cross-platform lock divergence `check-payload.ps1` exists to catch.

! The base URL is a constant, never a setting. A configurable download host turns an elevated endpoint into an arbitrary-download-and-execute primitive — the one thing this feature could become if built carelessly.

### The payload entry wrapper

`agentic/tools/assy-entry/` is what PyInstaller is actually pointed at; upstream's `cli.py` is imported by it rather than frozen as the entry point. It exists ∵ the payload already ships everything a second measurement needs — a Python runtime, `webrtcvad`, `numpy` — and the plugin could reach none of it: the frozen binary exposed only upstream's subcommands, and adding a detector to the C# side would mean shipping a second native dependency for a capability already inside the first.

Dispatch is on `argv[0]`. A name in the wrapper's own `LOCAL` list is handled here; anything else falls through to `cli.main()` **unmodified** → every upstream subcommand keeps its exact behaviour, exit codes and JSON contract, and an upstream bump does not have to be re-tested against a rewritten front end. `LOCAL` currently holds one name, `vad`.

`vad <video> --ffmpeg <path> --window <start>:<length> … --json` decodes each planned window through the named ffmpeg to mono 16-bit 16 kHz, runs `webrtcvad` at aggressiveness 3 over 10 ms frames, and reports the **rising edges** — a speech frame following 250 ms of non-speech, w/ a 100 ms minimum run — as absolute milliseconds in the video. It prints `{"ok", "onsets", "frames", "speechFrames", "windowsRead", "windowsPlanned", "perWindow"}` on stdout, one object, logging to stderr like everything else here. A window that fails to decode is skipped and counted, ¬fatal: a partial reading is still a reading, and `windowsRead` is what says how much of one. ! **The audio is scored as it arrives, ¬buffered.** A window is planned over the cue span, and a short subtitle late in a long film plans one spanning the hours before it — held whole, that is ≈2 MB of PCM per minute of window, in as many payload processes as there are concurrent syncs. The reader keeps ≈32 s in flight and accumulates only the per-frame flags. ! ffmpeg's stderr goes to a **temporary file, ¬a pipe**: nothing drains a pipe while stdout is being read, and a full one deadlocks the decode.

! **The rule is a copy of `vadcheck/vad-onsets.py`, deliberately.** That script is where the detector's numbers were measured against the real library, and a payload that reads them differently would make every one of those measurements describe something that no longer ships. `--self-test` synthesizes tone-and-silence audio and asserts the onsets come back, so the build fails on a payload whose detector cannot detect.

! **The freeze needs `multiprocessing.freeze_support()` and both modules named as hidden imports.** The wrapper is imported, ¬discovered — PyInstaller follows `import cli` only ∵ the spec is told to.

### `PluginPaths`

Single owner of where the plugin keeps anything on disk: `<DataPath>/AutoSubSync`. `SyncStore`, `BackupVault`, `PayloadStore` all hang off it; none computes a root of its own.

! **Nothing the plugin writes may go under `PluginConfigurationsPath`** (= `<ProgramDataPath>/plugins/configurations`, a child of the directory Jellyfin scans for plugins). `PluginManager.DiscoverPlugins` enumerates every top-level directory in `plugins/` → picks up `configurations` as a plugin candidate; `LoadManifest` finds no `meta.json`, invents one named `configurations`; `TryGetPluginDlls` globs `*.dll` through the whole tree w/ `SearchOption.AllDirectories`. On a stock server that glob returns nothing and the candidate is discarded. Up to 1.1.1.0 this plugin unpacked a PyInstaller freeze there — hundreds of native DLLs → promoted `configurations` to a real plugin → failed to load → marked `Malfunctioned` w/ a `meta.json` written back into the folder → next start removed it via `Directory.Delete(path, true)`, taking **every installed plugin's configuration** with it. That path is only ever meant to hold flat `<PluginName>.xml`, which is what `BasePlugin.ConfigurationFilePath` writes. All releases before 1.1.2.0 were withdrawn over this.

The constructor performs the one-time repair → it is a DI singleton the three stores depend on, ¬a static helper, ∵ construction order is what guarantees it runs before any of them touch disk. It `Directory.Move`s the legacy tree to the new home — never copy-then-delete, never a bare delete, ∵ the vault and the record store live in it. It then removes any `meta.json` Jellyfin left in `configurations`: that file is the live half of the fault, and a server that already has one keeps deleting the folder even after the payloads are gone.

### `PayloadStore`

Owns `<PluginPaths.Home>/payloads/<tool>/<version>/<rid>/`. It cannot live in the plugin directory ∵ Jellyfin replaces that wholesale on update → a payload there is destroyed by the next version bump and re-downloaded for nothing. Nor under `PluginConfigurationsPath` → above.

Keying by **payload version** ¬plugin version is what makes updates right w/ no update check: a new assembly pins a new version, finds no directory, fetches; a release that doesn't move the pin reuses the existing payload. ! That version is the **payload revision**, ¬the upstream tool's — `assy-cli` 2.0 freezes upstream 6.4, recorded separately as `upstream.version`. An installed payload is never re-hashed, so **anything that changes the bytes must change the version** or every existing server keeps the payload it already has, forever. Rebuilding the same upstream tag w/ a new wrapper is exactly that case, and is why the numbering was split. Superseded version directories are pruned only after the new one verifies → a failed fetch leaves the working payload in place. The **tool name is the outermost key** so pruning stays scoped to one tool — without it, installing a new `assy-cli` would sweep away every `seconv` directory as a superseded version, and the two pins move independently.

Staging directories are siblings of the destination → promotion is a rename within one volume. Scratch downloads go to `IApplicationPaths.TempDirectory`, ¬the system temp dir — a container's `/tmp` is frequently a small tmpfs and the archive is hundreds of MB.

### `PayloadFetcher`

Downloads, verifies, installs a pinned payload for whichever tool it's handed. Three load-bearing rules:

- ! **Verify before extracting, never after.** SHA-256 checked against the archive on disk before a single entry is unpacked; archive deleted on mismatch.
- ! **Extraction is path-checked.** Every entry's resolved destination must stay inside the target directory — an archive carrying `../` entries is a write primitive, and the archive is attacker-controlled in exactly the scenario where the hash check already failed. Both formats go through the same check; tar links are skipped outright ∵ a symlink's target isn't path-checkable at extraction time.
- ! **Promotion is atomic.** Extraction lands in staging and is renamed into place only once the binary is confirmed present → a partial unpack is never visible as an install.

Single-flight **per tool** (`SemaphoreSlim(1,1)` keyed by tool name): a startup fetch and any other trigger can't both download the same tool, while the two tools still install concurrently. Retries use bounded backoff and only network-shaped failures retry — a hash mismatch is refused outright.

`payloadcheck/` links these sources and asserts the negatives: wrong hash · traversal entry in either format · archive w/ no binary · pruning one tool leaves the other alone · a rejected download leaves an existing payload intact.

### `PayloadBootstrap`

Hosted service that fetches payloads at startup when missing. Never blocks startup, no configuration. Goes through each runtime's `EnsureReadyAsync` rather than calling `PayloadFetcher` directly → the startup attempt and every later retry share one cooldown.

`assy-cli` is always fetched. `seconv` only when the configuration asks for it (`ConvertImageSubtitles` or `RemoveHearingImpairedTags`, neither on by default) → an install that never touches OCR never pays 40 MB for a tool it won't run. Turning either on later is caught by `EnsureReadyAsync` on the first Convert or Transform stage, and by the config-changed hook, ∵ waiting for the first file that needs it strands the admin on "not downloaded yet" w/ nothing to do about it.

The fetch is deliberately silent apart from the log: the payload is an asset of this plugin's own release, pinned by a hash inside the assembly the user chose to install → installing the plugin **is** the consent. A prompt would ask the user to re-approve a decision already made, and a decline toggle would produce a plugin that cannot do the one thing it exists to do. The log records the start w/ the size, a line per quartile, and the outcome.

### `PayloadRuntime`, and `AssyRuntime` / `SeConvRuntime` on it

`PayloadRuntime` resolves one tool's binary from `PayloadStore` and reports **readiness as a state** — `Ready` · `Fetching` · `Unavailable` w/ a reason. `AssyRuntime` is that base bound to `PayloadManifest.AssyCli` and nothing more; `SeConvRuntime` binds `PayloadManifest.Seconv` and adds the Tesseract half. The state machine is the base class's → both tools get the same reporting discipline, retry and cooldown, w/ their own instance of each.

The state exists so a missing payload is reported **once, ¬once per subtitle**. `FullLibrarySyncTask` checks before discovery and aborts the run w/ a single log line; `LibraryEventHandler` does the same per item. Without that, a sweep w/ no payload writes one `Failed` record per subtitle — thousands of rows describing one problem, burying every real failure exactly when someone is trying to work out what went wrong.

Resolution is re-checked, ¬cached in a `Lazy<T>`: a payload can arrive at any point in a server's lifetime and a cached "missing" would survive the fetch that fixed it. The readiness transition is logged once per change, ¬per query.

`EnsureReadyAsync` is what both run entry points call: it resolves, and when the payload is `Unavailable` **and** the platform has a published asset, retries the install before answering. Without it a boot-time fetch that failed (no network yet, GitHub unreachable) left the plugin inert until a restart, ∵ nothing else ever asked for the bytes again.

`ClaimAttempt` rate-limits that to **one attempt per 15 min**. The limit exists for `LibraryEventHandler`, which runs per added item — without it, importing a season into a server w/ no payload starts a fresh multi-hundred-MB download attempt per episode. `PayloadFetcher.EnsureAsync` is already single-flight → concurrent callers cost nothing; the cooldown is about *repeated failures*, ¬concurrency. A `Fetching` state is left alone: the caller reports "downloading" and moves on rather than waiting.

¬runtime update path, ¬version negotiation, ¬config setting for the executable path. Fetching changes *when the bytes arrive, never which bytes*.

### `AssyArgumentBuilder`

Pure argv construction, no I/O → every flag combination is unit-testable without spawning a process. ! Never produces a concatenated command string — the argv goes to `ProcessStartInfo.ArgumentList`, which is what makes shell injection through a media path structurally impossible.

Three flags are always passed and are ¬user-configurable:

- `--no-color` — colour escapes would corrupt the stderr captured for error messages.
- `--no-prefix` — the plugin owns output naming; `assy-cli` must not decorate the filename.
- `--config-file` — below. Unconditional, and the builder takes the path as a **parameter** rather than reading configuration → there is no code path that omits it.

`-o` is always explicit rather than relying on `assy-cli`'s save modes ∵ Jellyfin's sidecar naming convention is stricter than anything those modes produce.

! **`BuildVad` passes none of the three.** `vad` is handled by the payload's own entry wrapper, which dispatches on `argv[0]` before upstream's parser runs → a global option ahead of the subcommand misses the dispatch, reaches upstream's argparse, and is rejected as an unknown command w/ exit 2. It also takes no config file: nothing it does reads one. `verifycheck` asserts the first two arguments are `vad` and the video path, ∵ that ordering is the whole contract and it is invisible from C#.

### `AssyConfigFile`

Renders `assy-config.json` into the plugin's home and hands the path to every invocation. It exists ∵ of what `assy-cli` does when `--config-file` is **absent**: `_load_user_config(None)` falls through to `load_config()`, which reads the **desktop application's** own settings from the platform user-config directory (`%APPDATA%\AutoSubSync\config.json` on Windows). Engine behaviour would then depend on whether anyone had ever run the GUI on that machine and what they last clicked — state the plugin cannot see, log, or reproduce.

`cmd_sync` merges `DEFAULT_OPTIONS` w/ that file then applies CLI flags on top → the file is a sparse override set: only keys that need to move are written, and a key written at its upstream default is a no-op ∵ options are only emitted as engine flags when they differ from it.

The pinned keys are global. ! The most important is `automatic_save_location`: `cmd_sync` calls `_validate_save_mode` **before** it looks at `-o`, so an inherited `select_destination_folder` w/ no folder set returns `EXIT_USAGE` for every sync — and exit 2 is defined as "the plugin built a bad command line", pointing a maintainer at an argv bug that doesn't exist. The rest turn off upstream behaviour a headless run must not have: tool-name prefixes, backup copies, retained intermediates, skip-lists, update checks, log files.

**¬per-engine keys**, and nothing here is user-configurable — the rendered content is the same every run. That followed from collapsing to one engine. The keys that used to matter most, `alass_check_video_for_subtitles` and `autosubsync_check_video_for_subtitles`, both defaulted **true**, which makes `sync_auto` extract a subtitle track from the video and align against *that* rather than the audio — a direct violation of invariant ① in `CLAUDE.md`. Pinning them false was the fix while those engines were reachable; not dispatching to them is a better one. `ffsubsync` has no such option and always aligns against audio.

! A missing file is **not** an error to `assy-cli` — `_load_user_config` returns `{}` for a nonexistent path and the run proceeds on upstream defaults, exactly the behaviour the file exists to prevent, but silently. → `Ensure` returns `null` on a write failure and `AssyCliRunner` refuses to spawn, reporting exit 2, rather than running unconfigured.

Writes are skipped when the rendered content matches the last successful write **and** the file is still on disk → the common path is a string comparison, a configuration change takes effect on the next invocation w/ nothing subscribing to a change event, and a file deleted underneath the plugin is rewritten rather than missed.

! There is deliberately **no setting that names a config file path**. An earlier `AssyConfigFilePath` did exactly that and was recorded in `AUDIT.md` as a settable path handed to a child process; retired when this component landed.

### `AssyCliRunner`

Spawns the binary and parses its output. `assy-cli` reserves **stdout** for machine-readable results and sends all logging to **stderr** → captured separately: stdout parsed as JSON, stderr tail-capped at 4 KB and **logged, ¬stored on the record**. `RunEngineAsync` returns a fixed sentence instead — `Failed: the sync engine timed out.` or `Failed: the sync engine did not complete.` The panel groups its reason lists by message text, so a stderr dump grouped as one row per title and buried the counts; the log keeps the detail, at `Warning` so the default level still carries it.

`ParseLastJsonObject` scans stdout backwards for the last per-pair result. `sync` emits exactly one object; `batch` emits NDJSON whose final line is a `{"summary": {...}}` envelope, which the parser skips ∵ it would deserialize into an `AssyResult` w/ `Ok` defaulted false and read as a failure. A line must carry an `ok` property to be accepted.

`VadAsync` is the same spawn machinery pointed at the `vad` subcommand, w/ `expectJson: false` — its stdout is a detector reading, ¬an `AssyResult`, so the sync parser is never asked to read it. `AssyVadOnsets` parses it instead. It resolves the payload the same way and reports the same `Unavailable` status when there is none; the caller treats that as "no reading" rather than a failure → *`SyncVerifier`*.

`EngineAlignment.From` reads the engine's `score:`, `offset seconds:` and `framerate scale factor:` off the **full** stderr *before* the 4000-char tail is cut — the engine prints them well above it. Score takes the **maximum** across matches, ¬the last: a framerate search prints one score per candidate, and the gate only ever refuses → reading low is what costs a good sync. Offset and rate take the last, which is the applied figure. Use → *`SyncVerifier`*.

The child runs w/ an **allowlisted environment**, ¬the server's. `ProcessStartInfo.Environment` is cleared and repopulated w/ only what a frozen CPython needs: `HOME`, `TMPDIR`/`TEMP`, `LANG`/`LC_*`, the Windows essentials, and a `PATH` w/ Jellyfin's ffmpeg directory prepended (from `IMediaEncoder.EncoderPath`) → the bundle carries no second copy and the two can't disagree about codec support. Anything else the Jellyfin process holds — tokens, credentials — is invisible to the subprocess. ! A virtualenv would **not** achieve this: a venv only touches `PATH`, `VIRTUAL_ENV` and `sys.prefix`, and gives no environment-variable isolation at all. The allowlist is deliberately conservative; extend it if the built payload turns out to need something.

! **The child is pinned to one BLAS thread per sync** — `OMP_NUM_THREADS`, `OPENBLAS_NUM_THREADS`, `MKL_NUM_THREADS`, `NUMEXPR_NUM_THREADS`, `VECLIB_MAXIMUM_THREADS` all set to `1` **after** the pass-through loop so an inherited value cannot win. ffsubsync's correlation runs on numpy, whose backend sizes its thread pool from the core count — and `NUMBER_OF_PROCESSORS` is on the pass-through list, so on Windows it read the host's. One permit then cost far more than one core while `MaxConcurrentSyncs` promises the user half their cores. Concurrency is this plugin's parallelism axis and `AdaptiveConcurrency` measures the result → a slower single sync is paid back by the ramp climbing inside a budget that now means what it says. See H4.

Timeout uses a linked `CancellationTokenSource`; the two cases it merges are handled differently, both killing the process tree first. A **timeout** is an engine failure → exit 1, an ordinary failed attempt. An **external cancellation** rethrows.

! The rethrow is what makes cancelling the scheduled task actually stop it. Returning a result instead — which this did until it was measured — converts the cancellation into a failed attempt and every layer above carries on: `RunEngineAsync` reports failure, `ProcessAsync` returns a record, `FullLibrarySyncTask` takes the next target. Each starts another child process that is immediately killed, and each failed attempt counts against the record → a cancelled run can push a subtitle toward being permanently skipped. `SeConvRunner` and `FfmpegProcess` always had the rethrow; `AssyCliRunner` was the one that didn't.

`SyncOrchestrator.ProcessAsync` records the cancellation on the record then rethrows for the same reason. `FullLibrarySyncTask` catches it only to flush the store — the subtitles already written are on disk and the records are the only thing stopping the next run redoing that work — and rethrows so Jellyfin marks the task cancelled rather than completed. `LibraryEventHandler` swallows it, ∵ there the token is the server shutting down.

### `SeConvRuntime`

Resolves the OCR toolchain: two things w/ two different owners.

`seconv` (Subtitle Edit's headless converter) is the plugin's, fetched and resolved by the same `PayloadRuntime` machinery. It differs in that the plugin ships no build of it — the pin points at Subtitle Edit's own release asset, verified against a compiled-in SHA-256. Where no asset exists for the platform, the Convert stage fails cleanly w/ "the OCR converter is not installed for `<platform>`" rather than mis-reporting the track as unsupported for its format.

**Tesseract is the administrator's.** No official Windows build, no redistributable to pin a hash against, language data installed system-wide → the plugin locates it: every `PATH` entry first, then a fixed probe list (`C:\Program Files\Tesseract-OCR`, `C:\Program Files (x86)\Tesseract-OCR`, `/usr/bin`, `/usr/local/bin`, `/snap/bin`, `/opt/homebrew/bin`). ! **¬configuration setting for this path** — a settable executable path on an elevated endpoint is arbitrary code execution w/ extra steps.

Readiness is three-way, ¬boolean, ∵ "no converter" and "no Tesseract" need different instructions from the admin. Logged once, on first query.

! **There are two readiness questions, and conflating them was a defect.** `GetConverterStatus` asks only whether `seconv` resolved; `GetOcrStatus` builds on it and additionally demands Tesseract — hence both `EnsureConverterReadyAsync` and `EnsureOcrReadyAsync`. Only `OcrAsync` needs the pair: it turns a bitmap into text. `RemoveHearingImpairedAsync` is `--remove-text-for-hi`, text in and text out, and needs no OCR engine at all. While both went through `EnsureOcrReadyAsync`, a server without Tesseract silently skipped HI stripping on every track — the stage recorded `Skipped` w/ a message about OCR, a condition the user could not connect to the setting they had turned on. `SeConvRunner.RunAsync` takes the resolved `SeConvStatus` as a **parameter** rather than fetching one → the caller's choice of question gates the spawn.

! **A download in flight ≠ "unsupported".** Both readiness calls return `ToolUnavailable?` — null when usable, else a message + an `IsTransient` flag set from `PayloadReadiness.Fetching`. `SyncOrchestrator.ConvertAsync` leaves a transient target `Pending` instead of `Unsupported` → the next sweep picks it up. Only reachable once the library scan went parallel: `ClaimAttempt` allows one fetch per 15 min, so of several workers arriving at an image track together, one downloads the 40 MB converter and the rest read `Fetching`. Sequentially the first target awaited the fetch and every later one found it ready. `Unsupported` was never terminal — the next scan retried — but it put a wrong reason in front of the admin for a file that was fine. See H3.

### `SeConvRunner`

Spawns `seconv` for `OcrAsync` and `RemoveHearingImpairedAsync`. Process handling mirrors `AssyCliRunner` exactly — `ArgumentList` only, the same allowlisted environment (plus Tesseract's directory ahead of ffmpeg's on `PATH`), the same `PerSyncTimeoutMinutes`, the same kill-the-tree path, the same stderr tail.

! **`seconv`'s exit code is worthless here.** Measured: a missing OCR engine, an unreadable track, and a format it cannot decode **all** exit 0, print "Converted 0 file(s)", and write either nothing or a seven-byte BOM. → the runner ignores the exit code entirely and asks `SubtitleContent.HasCues` whether the output holds cues, deleting it when it doesn't. Every failure mode found during testing is caught by that one check. The stderr tail is **logged, ¬returned as the message**, for the same reason as `AssyCliRunner`; a failed process start is likewise `Failed: the OCR tool could not be started.` rather than the exception text.

! Second way the exit code lies: if the `--outputfilename` path **already exists**, `seconv` leaves it untouched, writes nothing, exits 0 → a caller reusing an output path gets the previous run's content and a success. `SyncOrchestrator.ScratchPath` mints a fresh GUID per call, which is what puts that out of reach — ¬a stylistic choice.

**OCR language goes through `TesseractLanguage.Resolve`, ¬`LanguageCodes.Normalize`.** `seconv` hands the string **verbatim** to Tesseract's `-l` — measured against a real Tesseract, ¬assumed — so it has to be a **tessdata file name**, and tessdata is ISO 639-2/T only where no script splits the language. Three outcomes, and the caller acts on which one it gets:

- **A name** → passed as `--ocr-language`. `nob`/`nno` → `nor`, `kur` → `kmr`, `tgl` → `fil`: one model covers the pair and the ISO code names no file at all.
- **Null** → the flag is omitted entirely and Tesseract reads the track as English. Both an untagged track and the ISO placeholders (`und`, `mis`, `mul`, `zxx`) land here; naming a placeholder asks for a model that cannot exist, where omitting it at least reads a Latin track correctly.
- **Anything unlisted passes through unchanged.** ! This is a **blacklist, ¬an allowlist**. Enumerating what tessdata has is ≈120 names over an unbounded input domain, and a stale entry there **refuses work that would have succeeded**; an unlisted code reaching Tesseract merely fails as it always did.

! **Five tessdata models are chosen by script, and ISO 639-2 carries no script** → `Resolve` reads the **raw tag** first, before `Normalize` drops the subtag one line later: `zh-Hant` → `chi_tra`, `zh-Hans` → `chi_sim`, `sr-Latn` → `srp_latn`, `az-Cyrl` → `aze_cyrl`, `uz-Cyrl` → `uzb_cyrl`. `ForFilename` preserves the same subtag for the sidecar name; those two are the only places it is not thrown away.

- ! **A table, ¬a rule.** The *unsuffixed* model is **Cyrillic** for Serbian and **Latin** for Azerbaijani and Uzbek → nothing can be derived from the pattern, and `sr-Latn` read as `srp` returns fluent-looking nonsense rather than an error.
- ! **A bare `zh` takes `chi_sim`.** `zho` names no model at all, and Simplified is the commoner script by a wide margin → guessing beats refusing, and the guess is visible in the output where a refusal is visible only on the panel.

`--fix-common-errors` is always on: Subtitle Edit's own post-OCR cleanup, measured strictly better on every sample.

! **Colour isolation is on by default and it ruins VobSub.** `seconv` binarises the bitmaps off an isolated colour; on real VobSub sources it picks the wrong one and Tesseract reads the result as noise — measured on two structurally unrelated films, one of which also lost 78% of its images outright. `OcrAsync` passes `--no-vobsub-isolate-colors` whenever `IsVobSub(inputPath, codec)` holds: `.idx`/`.sub` names a sidecar, `dvd_subtitle`/`dvdsub` names an extracted track, which is why `ISeConvRunner.OcrAsync` carries the codec at all. ! **PGS is the exact inverse and is deliberately left alone.** Measured on both `supsample` styles: isolation **on** reads near-perfectly, **off** mangles words and drops them entirely (`When I was lying there` → `When | lying)`). Adding `--no-pgs-isolate-colors` *for symmetry* would degrade every Blu-Ray track the plugin reads, which is why the gate is `IsVobSub` and ¬a general "image subtitle" test.

! **The output is read back before it is trusted.** `--fix-common-errors` rewrites unresolved glyphs into plausible characters, so bad OCR looks like prose and the timings are *correct* regardless — they come from the index. Every other gate in the plugin judges timing → none of them can see this. `OcrReadability` is the only thing that can.

#### `OcrReadability`

Refuses OCR output nobody could read, from the shape of the text alone: mean word length **< 3.5** **and** short-word share (≤2 letters) **> 35%**, judged only above **200 words**.

- ! **Both bounds, ¬either.** The worst real subtitle measures 3.93 against a 3.5 floor — close enough that a language of short words could trip the mean bound on its own and lose a perfectly good track. Both measured noise reads fail *both* bounds by a wide margin, so requiring agreement costs nothing.
- ! **Under 200 words nothing is judged.** A CJK track carries no spaced Latin words at all and a forced track is a handful of captions; refusing either is the one unrecoverable error here, ∵ a refused track leaves the user nothing.
- Punctuation is stripped before a word is measured — a stray mark is not what makes a word long or short.
- Failure is a Convert-stage `FailStage`, ¬a crash: the record carries "the OCR tool could not read this track well enough to use."

`ocrcheck` holds the thresholds against nine subtitles whose quality is known — five real sidecars, the two isolation-on reads, and the same two streams read correctly. The separation it asserts is what makes the constants measured instead of guessed.

---

## Data/

### `SyncStore`

JSON persistence for `SyncRecord`, one file under `PluginPaths.Home`. Atomic writes (temp file then rename), a backup copy before each write, restore from backup on parse failure, stale temp cleanup on construction.

Record identity is `(ItemId, TargetKey)`, ¬`Id`. `UpsertLocked` matches on that pair and preserves the existing `Id` and `CreatedUtc`.

**Writes are coalesced.** Mutations set a dirty flag; a 5-second timer flushes, `Flush()` forces a write at the end of a batch, `Dispose()` flushes on shutdown. `Flush` logs and swallows write failures — never propagates. Worst case on an unclean shutdown is losing five seconds of bookkeeping → a re-sync, nothing else.

**Accessors return clones.** The lock protects the list; cloning protects the records. ! `SyncRecord.Clone` is a `MemberwiseClone` **plus a hand-copied `Stages` list** — a shared `List<T>` across clones would let a caller mutate a record already in the store. Every other field is a value type or a string.

**Scale**: one record per subtitle *track*, ¬per item. A 5,000-item library averaging 3 tracks ≈ 15,000 records — a few MB, loaded once at startup. `ISyncStore` is the seam if that stops being acceptable; a SQLite implementation would touch nothing else.

### `BackupVault`

Pre-overwrite copies of user subtitles under `<PluginPaths.Home>/backups/`, one folder per record: `<recordId:N>/<original filename>`.

! **Backups never live beside the media.** The media folder is the one directory Jellyfin scans for sidecars → a backup there is one naming change away from appearing as a duplicate subtitle track, and it lands plugin state where the user's own tooling operates, on volumes frequently read-only or quota'd. The record ID keeps two identically-named subtitles from different libraries apart; the original filename is preserved so a vault folder stays readable without the database.

! `Store` **never overwrites an existing backup** — it returns the existing path, or null on failure. Every method swallows and logs; a backup failure must not abort a sweep. → callers that may store twice under one record must pass a **label** (`SubtitleDeduplicator` uses `duplicate`), else the second call is a no-op returning someone else's bytes while the gate passes. `Discard` is called by the prune pass, without which the vault accumulates backups no record points at. `GetTotalBytes` exists so the config page can report the vault's size, ∵ it sits on the config volume rather than the media volume.

### `FileFingerprint`

Two strategies for two jobs:

- `TryComputeFull` — full SHA-256, for subtitle files. Tens of KB; free.
- `TryComputePartial` — `size + first 64KB + last 64KB`, for media. A full read of a 40 GB remux on every sweep would cost more than the sync it exists to avoid. Survives a move or rename while still catching a genuine content change. Borrowed from upstream's own `processed_items_manager.py`.

---

## Models/

`SubtitleTarget` = one unit of work: a single subtitle track on a single item. `Key` is the stable store identity — `ext:{relative path}` for sidecars, `emb:{streamIndex}:{codec}` for embedded.

`SyncRecord` = the persisted outcome. Its most important fields are the fingerprints:

- **External** targets are current only when the subtitle hash **and** the video hash both match.
- **Embedded** targets are current when the video hash matches; `SourceSha256` stays null.

`IsStillCurrent` is evaluated **before any extraction** → an unchanged embedded track costs a partial video hash rather than a full ffmpeg pass. This is what makes the second full scan cheap: the first pass is unavoidably expensive, every pass after it is O(new or changed subtitles). `IsStillCurrent` and `IsExhausted` share one fingerprint comparison and differ only in which statuses they accept — succeeded work skipped as current, failed work skipped as unchanged-since-it-failed → exactly one definition of "this target changed", and both release on the same signal.

**Settings are retroactive, on two different mechanisms.** A stored outcome is reusable only if the settings that produced it still hold:

- **Thresholds are re-decided from stored numbers.** `RejectedOffsetMs` holds how far off the speech the audio check found a refused result, `SkippedMovementMs` what `MinimumMovementMs` skipped, `AlignedAtMs` where the audio found a subtitle it left alone → `ToleranceWouldNowAccept`, `ToleranceWouldNowSync` and `MinimumWouldNowSync` tell exactly which records a widened window now admits, without re-running anything. Widening the tolerance, or turning verification off, releases precisely the records it used to exclude and no others.
  - ! **Those stored numbers are signed, and the hooks judge them w/ `SyncVerifier.IsAligned`, ¬a magnitude.** The bound is centred on the authored lead → a hook comparing `|stored|` against it admits readings the live check then refuses, and the record reopens on every sweep for ever. The hook and the verdict must be the same rule, spelled once.
  - ! **`AlignedAtMs` is cleared before the engine runs.** It records a subtitle the audio left alone; a target that reached the engine is by definition not that, and a stale value left behind is a number `ToleranceWouldNowSync` reopens against for ever.
  - ! **`MinimumWouldNowSync` reads `SkippedMovementMs` alone, ¬`AppliedOffsetMs` behind it.** The two describe different runs: `SkippedMovementMs` is written by the minimum's own exit, `AppliedOffsetMs` by a sync that succeeded. A record demoted out of `Synced` keeps the second → the fallback read a *successful* offset as a *skipped* movement, and every such row reopened, re-decoded and re-landed in the same state on every scan for ever → **U2**. ! The hook can ¬fire legitimately in any case: `MinimumMovementMs` is a `const`, so there is no "lowering the minimum" for it to react to. It is kept ∵ the const may become a setting; the fallback is ¬.
- **Everything else is a stamp.** `PluginConfiguration.OutcomeStamp()` composes the settings that change what gets written — dry run, HI stripping, OCR, write mode, encoding, marker — and `SafeUpsert` records it on every outcome. A record stamped differently is stale and runs again.
  - **`CheckRevision` rides in that stamp, ahead of the settings.** A constant, bumped by hand when the audio check's own rule changes. Without it a tightened check re-judges nothing: every stored record was stamped under settings that still hold, so nothing reopens and the whole library keeps verdicts the shipping code would no longer reach. It is the one lever that makes a **code** change retroactive the way a setting change already is. ! Bumping it re-processes every record on the next full scan — cheap per subtitle, but it is a full library pass, and the release that bumps it says so in its changelog.

**`RefusedByAudio` records which kind of failure it was**, ∵ a refusal and a tool failure are not fixed alike and the status panel has to tell them apart. `Fail` and `FailStage` set it as `kind == SubtitleStageKind.Verify` — the Verify stage *is* the audio check, and every refusing call site passes it.

- ! **Written on every failure, ¬only the refusals.** A flag set only when refusing would survive into a later run that failed elsewhere and mislabel it.
- ! **`bool?`, ¬`bool`.** Null means a row written before the field existed, and only those fall back to reading the stages. A plain `bool` cannot tell "not a refusal" from "never asked".
- ! **Every path that writes a `Failed` record writes the flag** — the three in `SyncOrchestrator` set it from the stage kind, and `Adopt` copies the twin's verbatim beside `RejectedOffsetMs`. An adopted row is exactly as old as the twin it took its verdict from.
- ! **`Adopt` stamps its stage from the flag, ¬from the default.** It runs no audio check of its own, so the kind cannot come from a call site; it reads `SyncOutcome.IsAudioRefusal` back off the record it just filled in. Taking `SafeUpsert`'s `Sync` default put **50** adopted refusals on the *Synchronization* row of the stage table, under a heading naming a step that rejected nothing → J8.
- ! **Both reopen paths clear it.** `ReopenFailed` (the retry button) and `Remeasure` (a measurement-version bump) each blank the flag and the stages w/ the status. A reopened record is `Pending` → guaranteed to run again and re-stamp, which is why clearing stages is safe here and ¬at the start of a run.
- ! **`RequireAudioConfirmation` gates exactly one of them.** It is read at a single site — the `Inconclusive` post-sync verdict — and the message it raises is `SyncOutcome.NoVerdictRefusal`, held as a named constant beside the grouping logic so the one setting-gated line is findable from the panel side. Every other refusal — `Misaligned`, the rate guard, the score floor, the unverified-shift bound — stands whatever the setting says. → the checkbox's own description on the config page is what tells the user which listed line unticking releases; a note against the whole *rejected by audio check* category would be false for the rest of it.
- ! **Stage outcomes outlive the run that wrote them.** `ProcessAsync` loads the stored record and `RecordStage` overwrites a single kind, so one record can carry a failed Verify from one run and a failed Convert from another. This is why the flag exists and why the pipeline table's failure columns sum past the failed-record count. Clearing `Stages` per run was rejected — both short-circuit paths return without stamping, so every target skipped on an unchanged fingerprint would lose its stages and never regain them.

! Throttling settings are deliberately **absent** from the stamp: `MaxConcurrentSyncs` and `PerSyncTimeoutMinutes` change how work is scheduled, ¬what it produces, and including them would invalidate an entire library over a concurrency tweak. The offset bounds are absent for the mirror reason — they are decided precisely from stored numbers, so stamping them would re-sync records whose outcome could not change. A **null stamp reads as current** → records written before stamping keep their outcomes and upgrading doesn't trigger a full re-sync.

`SyncRecord.Stages` records which pipeline steps ran, in pipeline order — `Acquire`, `Convert`, `Sync`, `Transform`, `Deduplicate`. v1 was single-stage → a record written before this gets a synthesized `Sync` stage at load, outcome derived from `Status` (`Unsupported` and `DryRun` become `Skipped`, ¬`Failed` — the track was never attempted). The migration runs once per user, silently, idempotent. A `Pending` record gets nothing ∵ it never completed a step.

`StampStage` runs inside `SafeUpsert` rather than at each of the orchestrator's exit paths → no outcome can reach the store without its stage. It maps `record.Status` onto **one** stage kind, which `SafeUpsert` takes as a parameter defaulting to `Sync`. A stage failing before the engine ever runs — OCR w/ no converter installed — passes its own kind instead → the record doesn't claim a `Sync` failure for work that never started. Stages that ran and handed control onward record themselves through `RecordStage`, which is why a successful Convert and a failed Sync coexist on one record w/ different tools attached.

`SubtitleProvenance` records whether the plugin retimed a user's file or created its own. It lives **on the record** rather than being inferred at rollback time: the two cases demand opposite verbs, and a filename cannot distinguish them once `MarkerSuffix` has changed or an overwrite has left the user's original name in place. It carries `JsonStringEnumConverter` to match `SyncStatus` and `SubtitleOrigin` — it was persisting as a bare integer, making the one field that decides restore-vs-delete the only one unreadable when inspecting `records.json` by hand. The converter reads numeric values too → older records still load, asserted in `storecheck/`.

`AssyResult` mirrors `assy-cli`'s JSON field names exactly. ! Do not rename without re-checking upstream's `main/cli.py`.

---

## Subtitles/

### `SubtitleDiscoveryService`

Turns an item into the list of tracks worth acting on, via `IMediaSourceManager.GetMediaStreams(item.Id)` split on `MediaStream.IsExternal`.

**One target per subtitle stream** that passes the language gate. An earlier design collapsed streams into slots of (language, forced, HI) keeping only the best-ranked source; withdrawn ∵ anime routinely ships a full English track **and** an English signs-and-songs track, both non-forced and both tagged `eng`, and slot collapsing threw one away without logging a decision — same for commentary tracks and for the second of two English sidecars.

**One suppression survived that withdrawal: a bitmap whose slot a readable track already serves.** `SuppressOcrCoveredByText` marks a `RequiresOcr` candidate when another candidate of the same slot has `RequiresOcr` false and no `UnsupportedReason`. OCR is the one stage that costs minutes per track and produces the worst output, so converting a track a text subtitle already covers buys nothing.

! **Marked, ¬removed — and the same for `SuppressCoveredEmbedded`.** Both wrote `candidates.RemoveAll`, so the track had no target, no record, and appeared in **no** count anywhere: turning OCR *on* made image tracks vanish from the panel that were visible as `Unsupported` while it was off → K3. Each now sets `UnsupportedReason` instead, which routes through `ProcessAsync`'s existing unsupported path. `SafeUpsert` stamps the stage as `Convert` when `RequiresOcr` → it lands in the OCR row's `SKIPPED` column, which until then could never be anything but zero → K4. No card, column or row was added for either.

! **Both also set `SubtitleTarget.SetAside`, and that is what keeps them off the *unsupported* card.** `ProcessAsync` reads it to choose `SyncStatus.SetAside` over `SyncStatus.Unsupported`; the card and the *Unsupported tracks* reason list both filter on `Unsupported` alone → a suppressed track is reported **only** as a skip on its stage row. The flag is on the target rather than inferred from the message: matching a display string is how the two would drift apart, and `UnsupportedReason` stays the single "this target is not going to run" marker every other reader already tests (`Group`'s K12 guard, the `covered` filter, `TextCovers`). ! The status → outcome map lives in **one** place, `SyncOutcome.StageFor`, called by `StampStage` and by `SyncStore.Migrate`. It was two switches until `SetAside` had to be added to both; the default arm is `Failed`, so a status missing from the `Skipped` arm files a track that never ran a step under `FAILED` on its stage row → L1. Neither caller can reach it w/ `Pending` or `DryRun` — both return before it — so that default still means *a real failure*, ¬*anything unlisted*.

! **A suppressed target must never reach `SubtitleDeduplicator.Group`.** It has no output, so `ToCandidate` returns null and **poisons its slot**, silently disabling deduplication for that language. `Group` skips any target carrying an `UnsupportedReason` → K12.

! **A `Retired` row is the second such target, and it is skipped for the same reason** → Q2. Its file is missing ∵ this deduplicator deleted it; `ToCandidate` cannot tell that absence from an unknown one and poisons the slot on **every later scan**. The poison rule exists for a candidate whose state cannot be established — a removal the plugin performed itself is the opposite of that. ! The two guards are ¬interchangeable: a suppressed target has no record to read, a retired one has a record saying exactly what happened to its file.

- ! **Slot, ¬language.** Keying on language alone is exactly what sank the general design: signs-and-songs and hearing-impaired tracks carry the language of the full track and are ¬substitutes for it. `SubtitleSlot` being (language, forced, HI) is what makes this rule safe where rank-wide collapsing was not.
- ! **An unlabelled track covers nothing.** Two tracks that name no language need not be the same language, so the rule short-circuits on an empty `LanguageKey`.
- ! **A titled bitmap is never dropped.** The slot cannot separate a full track from a signs track when both are tagged `eng` and neither is flagged forced — the case recorded above as the reason slot collapsing was withdrawn. A title is the only mark such a track carries, so carrying one exempts it. The cost is a skipped skip whenever a bitmap is titled "English".
- ! **A VobSub stream sharing its index is never dropped.** One `MediaStream` covers a whole `.idx`, so `IsForced`, `IsHearingImpaired` and `Title` are identical across every stream in it and only `Language` is per-stream. A DVD index holding a full **and** a forced English stream puts both in one slot, and neither can be told apart. `SharesAnIndex` exempts them; an index declaring a single stream for the language still drops.
- **Ties are kept.** Only a track needing OCR is ever removed, so two text tracks of one slot both survive and `AssignVariants` names them apart.
- Always on, w/ no setting of its own — it can only fire while `ConvertImageSubtitles` is on, ∵ nothing else sets `RequiresOcr`. `ExternalWriteMode` was considered as a gate and rejected: it decides where a synced output lands, ¬whether a track is processed.
- ! `DropCoveredEmbedded` (behind `ProcessEmbeddedWhenExternalExists` being **off**, its default) still keys on **language alone** and counts an external bitmap as covering an embedded text track. W/ both on, an external `.idx` beside an embedded SRT drops the SRT and OCRs the bitmap. Left as it is — the setting does what its name says, and which source wins is a preference. An always-on rule cannot afford that trap, which is why the two are separate.

Three rules here are easy to get wrong:

- ! **A track whose language will not normalize is always eligible**, whatever the allow-list says. `PassesLanguageFilter` short-circuits on `Normalize(language) is null`. Untagged tracks are disproportionately the signs-and-songs ones → filtering them drops exactly what the feature exists to catch. **Ordering matters: the allow-list check must not run first.**
- ! **Same-language tracks need a variant token or they overwrite each other.** Two `eng` tracks build the same sidecar name, and `ResolveCollision` hands back an existing path when it is already plugin output → the second write lands on the first. `AssignVariants` sets `SubtitleTarget.Variant` **only** for groups of ≥2, keeping single-track items on the filenames they already have; setting it unconditionally would orphan every file written so far.
- **Rank is ordering, ¬filtering.** `SubtitleSourceRank` sorts external text → embedded text → external image → embedded image, so cheap work finishes before any OCR begins and an interrupted run has done the most valuable part. The only filtering is `DropOcrCoveredByText` above, and it reads the slot rather than the rank.

**Rejections split in two, and the distinction decides whether a record is written.**

**Capability rejections** produce a target carrying an `UnsupportedReason` → the orchestrator turns it into a `SyncStatus.Unsupported` record before any other work, so the config page reports both a count and the reasons:

- Unsupported sidecar extensions — no engine reads them.
- **Image-based tracks, only while OCR is off.** Embedded PGS/VobSub/DVB fail `IsExtractableCodec`; a sidecar is caught by `ImageSidecarLabel`, which names `.sup` as PGS and resolves the ambiguous `.sub` by looking for a sibling `.idx`. ! That test runs **before** the extension allow-list and the cue check, both of which would otherwise reject a bitmap sidecar as unreadable while the identical embedded track OCRs fine. `MarkImageTrack` decides meaning: w/ `ConvertImageSubtitles` on it sets `RequiresOcr` and the track proceeds to Convert; w/ it off it sets an `UnsupportedReason` naming the setting that would fix it. ! The two must stay mutually exclusive — a target neither routed to OCR nor marked unsupported hands a bitmap straight to an alignment engine.
- A format the engine doesn't read — decided later in the orchestrator via `SyncEngine.Supports`. An `Unsupported` record rather than a guaranteed failure.

! **A VobSub pair is one track, and Jellyfin may name either half.** `ResolveSidecarPath` runs before the target is built and maps an `.idx` stream path onto the `.sub` beside it → `SubtitlePath` and `Key` always name the payload half. Without it an `.idx`-named track missed `ImageSidecarLabel`, fell through to the extension allow-list, and was recorded as "the sync engine does not read .idx subtitles" — a message reading like an engine limitation while the real effect was that `RequiresOcr` never got set, so enabling OCR, the remedy the user was pointed at, did nothing. An `.idx` w/ no `.sub` beside it stays unresolved on purpose: an index w/ no bitmaps is genuinely unreadable, and reporting it unsupported is correct.

! **One candidate per resolved path *and VobSub stream*.** `Discover` keeps a `seen` set ∵ Jellyfin can offer the same sidecar twice, and ∵ the `.idx` and `.sub` of one pair can both arrive as streams. Two candidates for one file share a `Key`, resolve to the same record, and were what let `SubtitleDeduplicator` delete a file as its own duplicate → M1. The deduplicator keeps its own guards; this closes the source rather than the symptom. Embedded targets have no `SubtitlePath` and are never collapsed by it. ! The key is `path + "\0" + stream` — the separator must stay a character no path can hold, and it must stay an **escape** in source: writing the raw byte makes the file binary to `grep` and `git diff`, so the change is invisible in review.

#### A multi-language VobSub is several tracks wearing one filename

One `.idx` can declare any number of language streams — "Gravity 2013 1080p BluRay multi-subs" declares **24**, 21,123 bitmaps in total. `seconv` has no way to convert one of them: `--ocr-language` picks the *tesseract model*, `--track-number` addresses MKV streams, and both were measured against this file still enumerating all 21,123. Handed the pair it converts **every language into one file** → 24 interleaved scripts, and at ≈6.8 images/s it runs ≈52 min and dies on `PerSyncTimeoutMinutes` first. → Z4.

`VobSubIndex` reads the `id:` blocks, and discovery emits **one target per declared stream**, keyed by `ExternalStreamKey`. English alone is 1,003 images, ≈2.5 min, inside the existing budget.

- ! **The language gate must run on what the index declares, ¬on `stream.Language`.** Jellyfin surfaces the pair as *one* stream carrying one language, so filtering there drops all 24 unseen — a specific allow-list would silently find nothing while a blank one worked. `BuildCandidates` therefore skips the upfront gate for a multi-stream index and applies `PassesLanguageFilter` per declared stream, where the existing rules already mean what is wanted: blank allow-list → every language, unnormalizable code → processed anyway.
- ! **A single-stream index keeps `ExternalKey`.** `DeclaredVobSubStreams` returns empty below two streams → the key is unchanged and the store records already written stay addressable. Only multi-stream files take the `#index` suffix, and those were delivering nothing before.
- **The split index is copied byte for byte**, ¬rewritten. Neither the block's own `index:` nor the header's `langidx:` changes what the converter reads; measured against a mid-list stream (`id: ru, index: 19`) both verbatim and reindexed yielding the same 873 cues.

- ! **The idempotency fingerprint must carry the stream.** `FileFingerprint.TryComputeSource` suffixes a **partial** hash w/ `#<stream>` where the target names one, and returns the full hash where it does not. Two reasons, both load-bearing: the payload is identical for all 24 streams, so a hash over it alone made them indistinguishable and `SettledTwin` adopted one stream's refusal for every other language w/out running it → B1; and `TryComputeFull` is reached ≈4× per target from `IsExhausted`, `IsStillCurrent` and `CaptureFingerprint`, which per-stream is ≈11.5 GB hashed off the share per scan of one film → B2. ! Both call sites go through the one helper — a fingerprint written one way and compared another never matches. ! A null hash stays null; suffixing one yields a fingerprint that matches everything.
- ! **An already-aligned OCR track still gets placed.** The verdict is *don't sync*, ¬*don't convert* — the OCR text lives only in scratch, so returning early dropped it and left `OutputPath` on the bitmap, which `SubtitleDeduplicator` cannot profile → the whole language slot went undeduplicated → B3. The aligned path now runs the same tail as the synced one.

**`VobSubStaging` exists ∵ the converter resolves the payload by filename beside the index and takes no flag for it.** A split index is unreadable without a `.sub` next to it, and the payload here is 120 MB against a 1 kB index.

- Staged under a deterministic folder — SHA-256 of path + length + mtime — so the streams of one file share **one** copy rather than staging 120 MB each. As separate queue items 24 streams would otherwise move ≈2.9 GB per scan.
- ! **Copy aside then move.** Two streams of one file stage concurrently; a half-written payload must never be the one another stream opens.
- Each stream still needs a payload *named for its own index* → **hard link, then symbolic link, then copy**. ! The BCL has no hard-link API (`File.CreateHardLink` does ¬exist) → `CreateHardLinkW` by `DllImport`, ¬`LibraryImport`, ∵ the generated marshaller demands `AllowUnsafeBlocks` for the whole project. On Windows a *symbolic* link needs a privilege a service account rarely holds, so symlink-first silently fell through to the copy and restored the 2.9 GB case the shared payload exists to avoid; NTFS hard links need no privilege. `vobsubcheck` writes a byte to the payload and reads it back through the pair — a copy fails that.
- **The copy is async and takes the cancellation token**, ∵ it moves up to 120 MB off the media share inside `ConvertAsync`.
- ! **`Sweep()` must stay wired.** Nothing refcounts these; `FullLibrarySyncTask` drops folders older than six hours at scan start. An unswept staging is a 120 MB-per-film disk leak. `Stage` stamps the folder's mtime on every call → the sweep can only reach a folder six hours after its last use, and a use is followed by one conversion inside one timeout.

**Scope rejections** return null and are never recorded: the language allowlist, the external/embedded toggles, an external file w/ no cues, and the plugin's own output (`SubtitleNaming.IsPluginOutput`). These are deliberate user choices → recording them would put a row against every foreign-language track in the library. An external sidecar is cue-checked at discovery via `SubtitleContent.HasCues`, the same check `FfmpegSubtitleExtractor` applies to what it extracts; a file w/ no cues is **not** a capability rejection — nothing is wrong w/ the format, there is simply nothing to sync — so it's dropped silently.

`ProcessEmbeddedWhenExternalExists` guards the one rule that drops a track for a reason outside the track itself, and it is **off by default** → the rule runs. While it is off, `DropCoveredEmbedded` removes every embedded candidate whose normalized language matches a *processable* external candidate — an external already carrying an `UnsupportedReason` covers nothing, so a VobSub sidecar doesn't suppress a good embedded text track. The cost is real and stated in the UI: a Signs & Songs track normally carries the same language tag as the full subtitle → this setting discards exactly the tracks the "every eligible track" rule exists to protect.

`SyncStatus.Skipped`, `SyncStatus.Unsupported` and `SyncStatus.SetAside` are three distinct things: `Skipped` = processed, result discarded as a no-op; `Unsupported` = never processable; `SetAside` = processable, and a setting chose not to. One status covering any two would make the count unexplainable — which is what *unsupported* covering the last of them did, and it read to the user as the plugin claiming it could not handle a track it simply declined to.

### `LanguageCodes`

Reduces any language code to one canonical ISO 639-2/T form so the allowlist can be compared against what a container actually carries. **Both sides are normalized** → the user's entry and the stream's tag can be in different forms and still match. Three separate problems, only one of them the user's fault:

- **Two- vs three-letter.** Jellyfin usually reports 639-2, users usually type 639-1. `en` and `eng` must be one filter.
- **Bibliographic vs terminological.** ISO 639-2 has two codes for ≈20 languages and ffmpeg emits the `/B` form: `ger` ¬`deu`, `fre` ¬`fra`, `chi` ¬`zho`. A user filtering `deu` silently matches nothing on files tagged `ger` — the failure that looks like a plugin bug, ∵ both codes are correct.
- **Country codes.** `jp`, `cn`, `kr`, `dk`, `se`, `cz`, `ua` are TLDs, ¬languages (Japanese is `ja`/`jpn`). Mapped anyway: the user's intent is never ambiguous, and the alternative is a filter that silently matches nothing.

The two-letter table is explicit rather than derived from `CultureInfo` → behaviour doesn't change under `InvariantGlobalization`, which some container images set; `CultureInfo` is consulted only as a fallback for two-letter codes outside the table. Region qualifiers are dropped — `pt-BR` and `pt` are one filter, ∵ a subtitle language filter has no use for a region.

**Sidecar naming uses `ForFilename`, which mirrors Jellyfin's own rule.** Verified against `Emby.Naming/ExternalFiles/ExternalPathParser.cs` and `LocalizationManager.FindLanguageInfo` at v10.11.11: the parser resolves a filename token against display name, name, both three-letter codes, and the two-letter code, then stores **the full culture name if it contains a hyphen, and the 639-2/T three-letter code otherwise**. Two consequences:

- Writing `en` and writing `eng` produce the same stored language → normalizing costs nothing. What it buys is certainty: an embedded track's language comes from the container tag via ffprobe, unnormalized, and ! **a token Jellyfin cannot resolve does not become an empty language — it becomes part of the title**, leaving the sidecar showing as Undefined. Emitting a code the parser is guaranteed to resolve removes that failure.
- ! **A hyphenated locale must survive intact.** `zh-Hans` and `zh-Hant` are Simplified and Traditional Chinese; reducing either to `zho` throws away a distinction Jellyfin would have kept. `ForFilename` passes anything containing a hyphen through untouched and normalizes the rest.

The delimiter set is `['.']` only → a hyphen inside a token is never split, which is what makes passing the locale through safe rather than merely hopeful. Flag vocabulary checked at the same time: `MediaForcedFlags` = `foreign, forced`, `MediaHearingImpairedFlags` = `cc, hi, sdh` → the `forced` and `sdh` segments `SubtitleNaming` writes are both recognized.

### `SubtitleNaming`

Builds and recognizes Jellyfin external-subtitle filenames, parsed as `{video basename}[.title][.language][.flags].{ext}` where flags include `forced`, `default`, `sdh`, `hi`, `cc`. Getting this wrong is the most likely cause of "the sync ran but nothing shows up" → it is a dependency-free unit w/ its own tests.

- Always derived from the *file* name, never the item's display name — Jellyfin matches by stem.
- `forced` and `sdh` carry forward from the source stream.
- The marker segment (default `autosubsync`) is always appended → discovery can recognize the plugin's own output.
- Collisions get `.2` … `.10`, then fail. ! Never clobber a file the plugin doesn't own.
- `Sanitize` strips `< > : " / \ | ? *` and trims dots; `MarkerSuffix` sanitization permits only letters, digits, `-`, `_`, defaulting to `autosubsync` → path traversal through either is blocked.

### `FfmpegSubtitleExtractor`

Extracts embedded tracks w/ the ffmpeg Jellyfin already ships → no new native dependency. Uses `-map 0:{index}` w/ the **absolute** container stream index from `MediaStream.Index`, avoiding the off-by-one that relative `0:s:{n}` indexing invites. ASS/SSA copied as-is to preserve styling; everything else normalized to SRT.

! **An extraction w/ no cues is a failure**, tested by `SubtitleContent.HasCues`. Neither obvious signal works: ffmpeg exits 0 on an empty track, and a copied ASS track holding no dialogue still carries ≈1 KB of `[Script Info]` and `[V4+ Styles]` headers → it passes any size check. Only looking for a `Dialogue:` line settles it. Syncing an empty track wastes minutes and produces a useless sidecar.

### `ImageSubtitleExtractor`

Lifts an embedded bitmap track into something `seconv` will read: a **single-track MKV** holding only that subtitle stream.

! The single-track shape is the whole point. `seconv` selects a track w/ `--track-number`, which counts Matroska tracks from 1 and is **not** `MediaStream.Index`; the two disagree for any file w/ video and audio ahead of the subtitles, and the failure is silent — it OCRs the wrong track, or nothing. A container w/ exactly one track needs no selection → the ambiguity is removed rather than mapped.

**DVB is the one codec needing a re-encode.** Handed a DVB track, `seconv` exits 0 and writes a seven-byte file; `ffprobe` confirms the packets are there, and the same stream transcoded to `dvdsub` OCRs every cue exactly. → `NeedsTranscode` sends `dvb_subtitle`/`dvbsub` through `-c:s dvdsub` and everything else through `-c:s copy`. Bitmap-to-bitmap is the only subtitle transcode ffmpeg permits (it refuses text-to-bitmap outright) and costs nothing here. PGS and VobSub are copied. External bitmap sidecars skip the extractor entirely — `seconv` reads an `.idx`/`.sub` pair and a bare `.sup` directly, so `ConvertAsync` hands it the library file as-is.

### `FfmpegProcess`

Both extractors spawn ffmpeg through one helper, giving them what the sync engines already had: the `PerSyncTimeoutMinutes` deadline, a kill-the-tree path, and a **drained stdout**. All three matter — extraction runs inside the target's `SyncQueue` slot, so an ffmpeg stalled on an unresponsive mount held that slot forever and the queue lost a worker permanently; cancellation previously abandoned the child rather than killing it, leaving an orphan ffmpeg per cancelled sync; and stdout was redirected but never read, which deadlocks a child that fills the pipe — harmless at `-loglevel error`, and a trap for anyone who raises it.

### `SubtitleContent`

Pulls cue text out of a subtitle file w/ the least parsing that answers the question, for two callers: the emptiness checks above, and `SdhDetector`.

Per-extension: `.ass`/`.ssa` look for `Dialogue:` and split the ten comma-separated fields (the tenth holds the text and may itself contain commas); `.srt`/`.vtt` accumulate blank-line-delimited blocks; `.sub` is MicroDVD `{start}{end}text`. ! **An unrecognized extension reports that it has cues.** The check exists to catch a known-empty file, ¬to gate formats — a new container extension must not make the plugin silently discard real subtitles.

Two traps the block parser handles, both proven in `subcheck`: a numeric line is the block index **only when it precedes the timing line**, ∵ a cue whose entire text is a year would otherwise be deleted; and WebVTT `NOTE`/`STYLE`/`REGION` blocks look exactly like cue blocks apart from their first line.

Reads are bounded at `MaxLinesRead` (400,000) and `MaxLinesScanned` (4,000) w/ `IOException`/`UnauthorizedAccessException` swallowed per line. Note the bound is on **line count, ¬bytes** — a file w/ no line break is one allocation of its whole length (S2 in the twenty-first pass).

### `SdhDetector`

Decides whether a subtitle actually carries hearing-impaired annotations, ∵ the container's `IsHearingImpaired` flag is frequently absent on files that plainly are SDH.

! This is not a labelling nicety. seconv's `--remove-text-for-hi` was measured against a crafted file and it strips **parentheses from ordinary dialogue too** — `He said (and I quote) it was fine.` comes back as `He said it was fine.` Pointed at a track that is not SDH, the tool quietly damages real lines. Detection is what keeps it aimed at the right tracks.

A cue counts as marked if it contains a bracketed span `[...]`/`(...)` holding ≥1 Latin letter, or begins w/ an all-uppercase speaker label (`MAN:`, `WOMAN #2:`, `>> NARRATOR:`). **Uppercase is load-bearing** — allowing lowercase matches every mid-sentence colon in the language, and it's the same rule the tool applies through `removeTextBeforeColonOnlyIfUppercase`. The one exception is a lowercase `l`, which a subtitle OCR'd from a bitmap routinely produces where the source read `I` — `ROBlN:` and `PENGUlN:` are real labels off a real disc, and rejecting them costs a genuine SDH track its verdict. ! Music notes are deliberately **not** a marker: the tool deletes a cue of bare `♪` symbols but keeps anything w/ text between the notes, ∵ lyrics can't be told from a sound description → a lyric-heavy track must not be pushed toward an SDH verdict by its songs.

The verdict needs **≥5 marked cues and ≥2% of the file**. Both bounds are needed: the ratio alone fires on a 30-cue signs track carrying four translator notes, the count alone fires on a full-length film w/ a dozen parenthetical asides. A false positive costs the user real dialogue and a false negative costs an unstripped tag → an uncertain verdict is no. Regexes carry explicit 200 ms timeouts w/ bounded quantifiers.

#### Where the 2% comes from

Placed by running the detector over **938 real subtitle tracks** — every sidecar `.srt` in a 989-title movie library — and reading where the populations separate. The great majority score exactly 0.00%, so the interesting region is narrow:

| ratio | track | what the marks actually are |
| --- | --- | --- |
| 1.04% | Midnight Diner | translator notes — `(Plamo = plastic model)` |
| 1.10% | Amélie | on-screen sign translations — `[KEYS CUT WHILE YOU WAIT]` |
| 1.10% | Last Hurrah for Chivalry | on-screen sign translations |
| 1.14% | Percy Jackson | speaker labels, no sound effects |
| 1.42% | The Martian | HUD readouts — `[ OXYGEN LEVEL, CRITICAL ]` |
| 1.76% | Double Down | genuine SDH — `[Gunshot]`, `[Applause]` |
| 2.33% | Working Girl | genuine SDH — `[Telephone Rings]`, `[Clears Throat]` |
| 2.78% | Mary Poppins `.SDH` | genuine SDH — `(BLOWS BOS'N'S WHISTLE)` |
| 4.86% | Independence Day | genuine SDH — `[Rumbling]` |
| 6%+ | 208 further tracks | all genuine SDH, spot-checked |

The threshold sits in the gap between 1.42% and 2.33%. Above it nothing in the library is damaged; below it sit the four cases worst to strip — sign and HUD translations are real content wearing SDH punctuation, and deleting them leaves the viewer w/ untranslated signs and a film that no longer reads. The cost is `Double Down` at 1.76%, a real SDH track that keeps its tags — the intended direction of the trade: a missed tag is an annoyance, a garbled track is a defect. The earlier 8% bar missed eight genuine SDH tracks, including every one above and the OCR'd Batman track at 7.07% that first exposed the problem.

**A bracketed span only counts when it contains a Latin letter.** Arabic subtitles conventionally wrap proper nouns in parentheses → `(تشارلي)` and `(وينستون)` are Charlie and Winston, not sound effects, and an ordinary Arabic track measured 5.71% w/ every character name queued for deletion. Across the same 938 tracks the lookahead changes exactly **one** verdict — that Arabic track — and flips no other; eleven tracks shift a few points without crossing. This does not make the detector script-aware: a subtitle in a non-Latin script whose annotations are also non-Latin won't be recognised as SDH at all. That is the safe direction to fail — an unstripped tag rather than a deleted name.

#### What the strip actually does to a real track

The threshold decides which tracks reach seconv; this is what seconv does once they get there. Measured on twelve tracks chosen so that between them they carry every annotation form the 938-track sweep turned up — lowercase brackets, all-caps parentheses, spaced brackets, brackets used as a speaker label, caps labels, compound labels, inline mid-cue annotations, lyric tracks, and two OCR'd tracks where `l` stands in for `I`. Together 15,411 cues. The audit is **word-level, ¬cue-level**: for every input cue it computes what a correct strip should have left (the cue w/ bracketed spans and leading uppercase label removed) and compares the multiset of words against the output's. Anything in the first and not the second is dialogue the strip destroyed.

**Nothing was destroyed on any of the twelve, and nothing was invented.**

| track | cues | untouched | edited | removed | words lost |
| --- | --- | --- | --- | --- | --- |
| The Intern | 2651 | 2485 | 31 | 135 | 0 |
| Working Girl | 1973 | 1927 | 12 | 34 | 0 |
| Mary Poppins `.SDH` | 1941 | 1887 | 28 | 26 | 0 |
| Irrational Man | 1659 | 1435 | 171 | 53 | 0 |
| Secret of the Incas `HI` | 1578 | 1400 | 29 | 149 | 0 |
| Uncle Buck | 1429 | 1357 | 23 | 49 | 0 |
| The Outlaw Josey Wales | 1223 | 1119 | 68 | 36 | 0 |
| Fun and Fancy Free | 981 | 653 | 232 | 96 | 0 |
| Batman: The Movie (OCR) | 933 | 872 | 23 | 38 | 0 |
| Fateful Findings `.sdh` | 571 | 553 | 3 | 15 | 0 |
| Mary Poppins Songs `.SDH` | 527 | 502 | 25 | 0 | 0 |
| Merry Madagascar | 395 | 313 | 47 | 35 | 0 |

A cue that is nothing but annotation is deleted outright; a cue mixing the two keeps the dialogue. `Hi. [laughs]` → `Hi.` · `- [all laughing] / - Whoo!` → `Whoo!` · `[Alex] Goodbyes can be bittersweet.` → `Goodbyes can be bittersweet.` Interjections survive (`Whoo-hoo!`, `Aww.`, `Oh!` are dialogue). Formatting survives the edit — `<i>ABE: Where to begin?</i>` → `<i>Where to begin?</i>`. Lyrics survive intact, which is what the notes rule exists to protect: the one music-note change in the whole set is 24 cues of bare `♪♪` in The Intern, instrumental markers w/ no words, and a lyric line keeps its notes.

Two behaviours worth knowing, neither a bug to fix here:

- **The tool's speaker-label rule is stricter than the detector's.** It removes `BATMAN:` and the compound `GRANDMA & LAURA LEE [SINGING]:`, but ¬`ROBlN:` w/ the OCR `l`, and ¬a mixed-case `Jason:`. The detector deliberately counts the OCR form and the tool deliberately won't delete it → the asymmetry runs in the safe direction, more sensitive detection without more destructive removal, worst case a surviving tag.
- **Removing a label re-capitalizes what follows.** On an OCR'd track `BUCK: l'm getting mad.` → `L'm getting mad.` rather than `I'm`. Twice in 15,411 cues, both in Uncle Buck, only where the source already had the OCR damage.

`fetch-seconv.ps1` puts the pinned seconv on disk to re-run any of this.

### `SubtitleOffsetProbe`

Answers, after a sync: what did the engine do to the timings? `Measure` splits the answer in two ∵ the halves mean different things:

- **`ConstantMs`** — how far the timings moved at the input's first cue. The offset in the ordinary sense; what `MinimumMovementMs` tests.
- **`RateRatio`** — how the span from first cue to last changed. A framerate correction shows up here and nowhere else.

! **Cue identity, never cue position.** Both values come from a least-squares fit of `delta(t) = intercept + slope·t` across cues paired by their *text*: each cue keyed on its letters and digits, keys shorter than 8 chars or occurring twice on either side dropped, and the fit re-run once without the worst tenth of the residuals. `ConstantMs` is that line at the input's first cue; `RateRatio` is `1 + slope`.

Comparing first cue to first cue assumes the engine preserves the first cue, and it does not: subtitles from common tooling carry a zero-duration marker cue at t≈0 holding the source framerate — `25.000`, `***`, a bare musical note — and `ffsubsync` drops it. The old measurement compared that marker against the first real line of dialogue and reported the gap as a shift that never happened: `Atlantis: Milo's Return` was rejected at 76628 ms against a 60000 ms limit on all four of its identical sidecars — four of the six offset-limit rejections in the entire store. The pattern was in 18 of 1161 source subtitles. The mirror case is reachable too, a real sync skipped as already-in-sync, ∵ the same number feeds `MinimumMovementMs`.

`Endpoints` keeps the old first-and-last measurement and runs when either file is unreadable, when fewer than `MinimumPairs` cues pair, or when the matched span is under `MinimumSpanMs`. Nothing that previously produced a number now produces null.

`SyncRecord.MeasurementVersion` makes the change retroactive: `SyncStore.Load` re-opens any `Failed` record carrying a `RejectedOffsetMs` set below `CurrentMeasurementVersion`, ∵ a rejection measured by a rule that no longer exists is not evidence about the current one. ! Bump the constant whenever the measurement changes.

! **Why the two must not be collapsed into one number.** An earlier revision took the larger of the displacement at each end and tested that against a single maximum. It correctly caught a rate blowup, but rejected every legitimate framerate correction: a PAL-sourced subtitle against film-rate video needs a 4.271% stretch (`25 / 23.976`), which displaces the end of a feature by minutes → w/ a two-minute maximum, anything longer than ≈47 minutes was refused by construction, and a framerate mismatch is the most common desync there is. Three real rejections are recorded in the eleventh pass.

The rate has its own bound, `MaximumRateDrift` at **30%**. A ratio is only computed when the input spans ≥`MinimumSpanMs` (one minute); below that the sample is too short for a ratio to mean anything and only the constant bound applies.

! **The bound has to clear the NTSC family, not just the PAL one.** It shipped at 25% against a list stopping at `30/25` = 20%, missing the film-to-broadcast conversions entirely. Frame rates are exact rationals — 23.976 = 24000/1001, 29.97 = 30000/1001 → `23.976 → 30` is 25.125%, and both `24 → 30` and `23.976 → 29.97` are exactly 25.000%. The test is `> MaximumRateDrift`, so an exact 1.25 passed, but the ratio is computed from integer-ms spans and the measured value landed either side of the line at random: one legitimate conversion refused outright, two decided by rounding. 30% clears all twenty pairs drawn from {23.976, 24, 25, 29.97, 30} and still rejects both adversarial shapes in the eleventh pass's boundary table. See H1; `check-rate-bound.mjs` guards it.

The two are complementary rather than redundant — a wrong-audio latch shows as a large constant shift, a rate blowup as an implausible ratio, and neither hides inside the other. The minimum-offset skip tests `max(ConstantMs, DriftMs)` → a pure rate correction that leaves the first cue alone is still recognised as real work. An unparseable timestamp measures as null, leaving that bound untested and keeping the result.

---

## Services/

### `LibraryScopeResolver`

Decides which items the plugin may touch: the library allowlist, plus a real file on disk (excludes stubs, `.strm`, unmounted storage).

! **An empty allowlist means no items, ¬all items.** A plugin that writes into media folders is opted into; a fresh install must not start rewriting subtitles across every library on its first nightly run. `GetItemsInScope` returns early rather than enumerating and filtering → the unconfigured case is free.

`GetVirtualFolders()` is fetched **once per sweep** and threaded through. Per item it turns a 15,000-item scan into 15,000 enumerations of library configuration.

`IsUnder` does the library-root test w/ a separator boundary → `/media/movies` doesn't match `/media/movies-4k`. It compares `OrdinalIgnoreCase` unconditionally, unlike `SyncOrchestrator.IsWithin` which picks by OS → S1 in the twenty-first pass.

### `SyncQueue`

The single concurrency gate; every sync passes through it. ffsubsync decodes and VAD-analyzes an entire audio track — minutes of full-core work per file — and running an unbounded number of those next to a live transcode makes the server unusable.

`MaxConcurrentSyncs` defaults to `0` = automatic. A non-zero value is an instruction and is honoured exactly; automatic hands the decision to `AdaptiveConcurrency`, bounded above by `PluginConfiguration.AutoConcurrencyFor` = half the cores, floored at 1, capped at 8. That is a **ceiling, ¬a target** — the ramp starts at one and only climbs while throughput improves, so a storage-bound server never reaches it. `Environment.ProcessorCount` honours cgroup quotas → a two-core container on a 32-core host resolves to 1.

The formula was previously 1 at ≤4 cores. That predates the scan being parallel, when the only caller that could saturate the gate was the library event handler; w/ the scan offering work the special case contradicted the setting's own description and denied a quad-core box the second slot the ramp would have had to earn anyway.

! **The limit is enforced by holding permits back, ¬by rebuilding the semaphore.** One `SemaphoreSlim(8, 8)` lives for the process, and a limit of *n* is expressed by keeping `8 - n` permits out of circulation as ballast. Rebuilding was correct while the limit only moved on a settings change; w/ adaptation it moves every few syncs, and a fresh semaphore starts fully available while the old one is still held by in-flight work — briefly admitting more than the limit, every time it changed. Shrinking is best-effort by design: a permit in use is reclaimed when it comes back, ¬by interrupting a sync.

### `AdaptiveConcurrency`

Picks the automatic level by measuring rather than predicting: hill-climbs by collecting six samples at the current level, computing throughput, stepping in whatever direction improved, reversing when a step makes things worse, settling on the better neighbour.

**Why measuring beats a formula.** Each sync reads the *entire* video file — audio and video packets are interleaved, so ffmpeg demuxes the whole container to reach the audio — then spends CPU on VAD and correlation. Which half binds depends on the media and the storage, and they pull opposite ways: CPU-bound → more slots buy throughput up to the core count; disk- or network-bound → more slots buy nothing and on spinning disks lose ground to seek thrash. A core-count formula answers the wrong question on a NAS, and disk speed can't be probed usefully from inside a plugin — library paths may be NVMe, spinning rust, SMB or a cloud mount, a benchmark measures the config volume rather than the media volume, and the page cache flatters the result anyway.

The signal is **wall time under the semaphore, normalized to ms per GB of reference video**. Slot occupancy rather than the engine's self-reported time, ∵ extraction and placement hold a slot too and occupancy is what determines throughput; per GB ∵ a 40 GB remux and a 700 MB episode are otherwise incomparable.

! **Throughput is `meanObservedConcurrency / meanMsPerGigabyte`, ¬`level / meanMsPerGigabyte`.** The level is a permit, and a caller that never offers two jobs at once never spends it → `SyncQueue` reports the in-flight count at admission and the numerator is what actually ran. Using the level made every raise look like a proportional win whenever the workload was sequential: mean ms/GB doesn't move, throughput reads `2/M` against `1/M`, clears the 10% margin, and the ramp climbs to the ceiling on evidence that does not exist. The scheduled scan awaited each sync in turn and was exactly that workload. W/ realized concurrency in the numerator the same run reads flat, and *a flat result settles low* does the rest. The scan is parallel now, but a manual sync and a lone new item still are that workload → the numerator has to stay honest for them. `simulate-concurrency.mjs` pins it w/ two cases: `Sequential caller`, whose `achieved` returns 1 whatever the level, band `[1, 1]`; and `Caller capped at two`, which offers two on a higher ceiling and must not track that ceiling.

Three properties make it safe rather than merely clever:

- **It cannot exceed the formula's answer.** The ceiling is still `AutoConcurrencyFor` → adaptation only ever discovers it should use *less* than the old default would have taken.
- **A flat result settles low.** If an extra slot leaves throughput within 10%, the slot bought nothing and the lower level wins. Load without benefit is the thing the setting exists to prevent.
- **Samples are attributable or discarded.** A sample whose level changed mid-run is dropped, and only runs that completed normally report at all — a cancelled or failed sync says nothing about throughput.

It re-probes after 150 settled samples, stepping toward whichever end has room, ∵ a box idle when the level was chosen may be transcoding three streams an hour later. Simulated against four storage profiles it settles where it should: at the ceiling when throughput scales w/ slots, at 1 when bandwidth is saturated or seeks thrash.

### `TargetLocks`

One lease per `(ItemId, TargetKey)`, refcounted so the map drops an entry when its last holder leaves. `ProcessAsync` takes it **before it reads the record**.

Three producers reach `ProcessAsync` — the scheduled task, the library event handler, the API — and nothing else stops two of them holding one target at once. `ItemChangeGate` cannot arbitrate: the task deliberately doesn't consult the gate, and the handler's check runs before the task's `Commit`. The durable gate is `IsStillCurrent`, but that was evaluated against a record read at `ProcessAsync` entry, *before* the `SyncQueue` wait → at eight permits the snapshot could be 90 s stale by the time the pipeline ran. A production log has `Dinosaur (2000)` synced at 09:35:16 and the engine started on it again at 09:36:44 — an 88-second gap that is exactly the queue latency. Holding the lease across the queue wait is what makes the read current for as long as it's used.

! **The lease is always taken before the queue permit**, never the other way, and a worker holds at most one → nothing can cycle, and a duplicate blocked on a lease isn't occupying a concurrency slot. `Lease.Dispose` signals the semaphore before dropping the reference, ∵ dropping the last one disposes it.

### `SyncOrchestrator`

The per-target pipeline (order → *Pipeline overview*). Every path ends in a `SyncRecord` being written.

! **Why `-o` points at scratch.** `assy-cli` writes wherever `-o` says → pointing it at the media folder means a crashed or timed-out run leaves a partial file exactly where Jellyfin will index it. The result is moved into place only after a successful, non-cancelled exit. Placement order for overwrite mode is **backup → move → record**, so a failure between steps still leaves the backup recoverable.

Scratch lives under `IApplicationPaths.TempDirectory` (a container's `/tmp` is frequently a small tmpfs). Each attempt gets its own GUID path there, deleted on failure and again on cancellation before rethrowing. ! The engine reports the file it wrote; that path is honoured **only if it resolves inside the scratch directory** → a misbehaving or unexpected CLI response can never nominate a file elsewhere for the placer to move into the library.

**The sync attempt.** `RunEngineAsync` runs `ffsubsync` once and accepts the result only if it exits `ok` **and** leaves a file on disk. An engine claiming success without writing anything is a failure, ∵ the alternative is a `Synced` record pointing at nothing.

**A finished sync is not thrown away over a print.** The frozen CLI can complete the alignment, write `-o` in full, then die inside its own status write: `_emit_json` encodes the result through the console codepage, and a media path containing a character cp1252 cannot spell raises `UnicodeEncodeError` after all the work is done. The stream carries a truncated `{"ok": true,` and the process exits non-zero → both halves of the success gate fail and the completed file used to be deleted. Ten wasted syncs in one day of field logs, every one a title w/ a non-ASCII filename.

! The environment is **not** the lever: `PYTHONUTF8` and `PYTHONIOENCODING` were tried and measured ineffective ∵ a PyInstaller freeze starts under an isolated interpreter configuration that ignores every `PYTHON*` variable — the same frame appears w/ and without them. Forcing `StandardOutputEncoding` to UTF-8 is worse than useless, ∵ the child still writes ANSI whenever it can.

So the salvage is on the plugin's side: when the run did not time out, produced **no** parseable JSON envelope at all, and left a file at the scratch path parsing to within 5% of the input's cue count, that file is accepted and the exit code recorded as it was. Four conditions keep it honest:

- **No envelope, ¬a failed one.** A handled failure reports `{"ok": false}`, which parses; that path still fails as before. This fires only when the CLI died without saying anything.
- **The path is the plugin's own.** Nothing is read out of the broken JSON — the accepted path is the `-o` the plugin handed the engine.
- **The cue count is the integrity check.** A write cut short loses cues against its input; measured on the reproduction (`Mr. Inbetween S02E03`, filename carrying U+FF1F), 455 cues in and 455 out is accepted and the same file truncated to 90% counts 410 and is refused. Only the last few percent of a write could slip through.
- **Nothing downstream is relaxed.** The rate bound, the minimum-movement check and the audio check judge a salvaged file exactly as a reported one — and the plugin never needed the envelope anyway: `SubtitleOffsetProbe` measures the applied shift from the two files and the audio check verifies the result independently.

**One run, and there is no retry budget.** There used to be a `MaxAttempts` setting capping engine runs and an `AttemptCount` accumulating across scans. Both are gone: a cap was a bug wearing a setting's clothes, silently disabling fallback below its length and doing nothing above it. There is now one engine → the question doesn't arise.

What remains is `IsExhausted`, meaning only "this failed and nothing about it has changed since". Identical inputs fail identically → re-running the engine over the same bytes is pure cost. The gate is released **by change, ¬by time or a counter** → repairing a subtitle or replacing the video makes a failed target eligible again on the next sweep.

! **The fingerprint is captured before the first stage that can fail**, ¬after the pipeline succeeds. Captured late, an OCR failure returned w/ `VideoPartialHash` still null and `IsExhausted` structurally unable to match → a bitmap track that timed out after twenty minutes was queued to do it again on every sweep, holding a permit each time. It never needed the converted path: an embedded target settles on the video hash alone, and an external target always has a source path.

! **The fingerprint is taken from the source, never the conversion.** `CaptureFingerprint` receives `target.SubtitlePath ?? inputPath` → an OCR'd external track is fingerprinted against the `.sub`/`.idx` the user has, ¬the scratch SRT deleted at the end of the run. Fingerprint the conversion instead and `IsStillCurrent` can never match again, re-OCRing the whole library every sweep.

**`SettledTwin` adopts a twin's outcome instead of re-running the engine.** Another record on the same item w/ the same source hash, video hash and settings stamp already measured what this one would. `Atlantis: Milo's Return` carries four byte-identical sidecars and paid four engine runs to reach four identical rejections; `SubtitleDeduplicator` cannot collapse them ∵ `ToCandidate` requires `Synced` or `Skipped` and all four are `Failed`. ! Adoption is restricted to outcomes that wrote nothing **and** came from a measurement — a rejected offset or a below-minimum skip. A tool failure or timeout can be transient and deserves its retry, and a `Synced` outcome is never adopted ∵ that would claim a sidecar this target never wrote. ! An adopted row carries the twin's verdict w/ **no `Verify` stage of its own** — nothing measured it — so the stage it *does* stamp is chosen from the adopted flag, above.

**A source that is gone is not a failure.** Between `IsStillCurrent` and any extraction, `RunPipelineAsync` checks an external target's sidecar still exists. Without it a sidecar deleted since the last scan — by the user, or by the deduplicator — reaches the engine, which cannot open it, and the record lands on `Failed`. ! That failure can never be suppressed by `IsExhausted`, ∵ its fingerprint comparison needs to read the file that is missing → the target is retried on every scan forever. The check records `Skipped` instead, which is stable.

**Re-fingerprinting after an in-place write.** In overwrite mode the synced file replaces the exact file the fingerprint was taken from → the stored hash would never match again and every scan would re-sync the same subtitle against its own previous output. `RunPipelineAsync` re-reads the fingerprint from the placed file whenever placement reports `Retimed`. Side-by-side placement leaves the source untouched and needs no correction.

**The Convert stage.** `ConvertAsync` runs only for targets discovery marked `RequiresOcr`. It sources an image file — the sidecar directly for external VobSub, `ImageSubtitleExtractor` for an embedded track — hands it to `SeConvRunner.OcrAsync`, and uses the resulting SRT as the sync input. A failure here ends the run: the record carries the OCR message and a `Convert` stage, and no `Sync` stage is stamped ∵ the engine never ran. An unavailable toolchain records `Unsupported` rather than `Failed` → `IsExhausted` never locks the target out over a condition the user has to fix on the server.

**The Verify stage, and the two refusals underneath it.** A `Misaligned` verdict deletes the produced file and records the miss — the **drift** where the check measured one past `DriftWithinMs`, otherwise the fitted shift. Both are stored **signed**: the retroactivity hook reads the number back against a bound centred on the authored lead, and a magnitude carries no information about which side of it the reading sits on.

Beneath that sits a guard for the case the check never measured: `verdict.DriftMs` is null on every `Inconclusive` verdict and on any title too short to plan `DriftWindows` windows, so a rescale the engine applied would otherwise go unexamined. `SubtitleOffsetProbe`'s own drift figure is held to the same `DriftWithinMs`. ! The refusal carries **two** messages, chosen on `verdict.Windows < DriftWindows`: a title too short to plan six windows can never be measured, while one that planned them and reached no fit is a different refusal w/ a different remedy. The panel groups reasons by message text → collapsing them into one sentence tells the user the wrong thing about half the rows in the bucket.

**The guard has one release path, and it is a fall-through.** Where the verdict is `Aligned` *and* the check carried a `CoarseDriftMs` inside `CoarseDriftWithinMs` (→ `SyncVerifier.ReleasedByCoarseDrift`), the rescale is released rather than refused. The result then continues down the same path every other accepted sync takes — the `Inconclusive` shift backstop, `RequireAudioConfirmation`, the engine-score gate, the transform, placement — ∵ releasing means *not returning* from the guard, ¬branching around it. A separate release branch would bypass all of them. Nothing else in the block moves: a title the coarse reading cannot release is refused w/ the same two messages and the same `RejectedOffsetMs`.

- **It cannot weaken `RequireAudioConfirmation`.** That setting gates exactly one thing, the `Inconclusive` verdict, and this fires only on `Aligned` → an unconfirmed title still dies at its own guard. The release removes no test and loosens no bound; it **adds** a test to a class of titles that faces none today, and refuses whenever that test cannot be run. Under the setting it strictly increases the evidence required before a short title is written.
- ! **The release logs at Information**, naming the item, the key, the engine's stretch, the coarse drift and the window count. The refusal it replaces logs a warning → without a line of its own the new path is invisible in a field log, and *which* rule released a title is exactly what the next investigation needs.
- **The measurement is in `SyncVerifier`, the policy here.** It cannot be computed at this call site: the verdict arrives from `ScoreAsync` (which holds the sample) **or** `VerifyAsync` (which samples internally and returns none) → a guard computing it would behave differently depending on which branch ran. Both reach `Score`, so both carry the reading.
- **The detector can supply the coarse reading the silence lacked.** `ScoreAsync` consults the second pass on two shapes now: a first pass that reached **no verdict**, and an **`Aligned`** one on a plan the release condition could have read and did not — `CoarseDriftMs is null` w/ `Plan.Count / 2 >= 2` (`Drift`'s own guard, spelled the same way) and `Plan.Count < DriftWindows`. Measured over 141 four-window titles: **20 → 23** released, **0** false accepts at ±800, +1500 and +2500 ms, smallest |coarse| on an injected title **425 ms** against the 300 bound. The three recovered were `Aligned` and short of onsets, ¬short of alignment — 56→81 and 99→111 onsets, reading −175, −75 and +75 ms.
  - ! **A detector may supply a reading, never a verdict.** Where the first pass said `Aligned`, only a second that is *also* `Aligned` **and** carries a coarse value is taken; anything else leaves the first standing and the title fails closed. Dropping the `Aligned` requirement from the trigger lets the detector overturn a **`Misaligned`** silence reading — the mutation was run and the title flipped to `Aligned`. That is the whole safety argument, and it is now covered by *a refused four-window title is never handed to the detector*; the pre-existing refusal case used a 16-window plan and could not reach it.
  - ! **The pattern's `Verdict: Aligned` on the second result is defensive, ¬load-bearing.** `Score` computes the coarse fit only on an `Aligned` verdict (T1), so a `Misaligned` result cannot carry one. It is kept as an explicit statement of intent; no case can distinguish it while that holds.
  - The cost lands only on a post-sync verification — the pre-sync check calls `Score` statically and never reaches the second pass — and only on a short-plan title the silence placed but could not halve: **16 of 141** in the sweep population.
- ! **It shipped w/ no retroactivity lever, then gained one.** `CheckRevision` was **¬** bumped for this guard: a bump would have re-scanned the whole library to reach one bucket, where *Retry failed subtitles* reopens the refusals alone and the user chooses when. **U1 then bumped it to `check3` for an unrelated reason, and this picks it up for free** — anticipated at the time, and confirmed as one decision rather than two. → the first full scan after `check3` retries every stored audio refusal, and the ones this guard would now release are released.

**The Transform stage** runs **after** sync, on the premise that the aligner does better w/ more cues than fewer — the annotations are removed once they have served that purpose. `TransformAsync` gates the tool on `SdhDetector`, which is the load-bearing part → running `--remove-text-for-hi` unconditionally would corrupt every non-SDH track in the library. A failed strip returns the synced file unchanged and records a `Failed` `Transform` stage — losing a good sync over a cosmetic pass is the worse outcome. On success it clears `target.IsHearingImpaired` before placement so the sidecar name drops its `sdh` token: the name has to follow the content, ∵ a file called `.sdh.srt` w/ no annotations tells every downstream client the wrong thing.

**Scratch cleanup is list-based.** One run can create an extracted MKV, an OCR'd SRT, a synced SRT and a stripped SRT. `RunPipelineAsync` tracks every one in a list and deletes the lot in `finally`, including the pre-transform file once superseded. The earlier single-`temporaryInput` variable leaked three of the four.

**Dry run** returns before any filesystem work, and there is currently exactly one entry point to the pipeline. ! Note this also means **OCR never runs in dry run** — the Convert stage writes scratch files, and dry run is a filesystem lock, not a logging mode. It blocks writes to the *media library*; the plugin's own record store is still written, including the DryRun records themselves.

### `SyncVerifier`

Answers, from the video's own audio and nothing else, whether a subtitle's cues sit on the speech. The only component that can disagree w/ the sync engine, consulted **twice per target**: before the engine, to decide whether a sync is needed at all; after it, to decide whether the result may be written.

**Why it exists.** The old guard was `MaximumOffsetMs`, a bound on how far a result was allowed to move — the wrong question. `ffsubsync` mis-latched a correctly-timed Bambi II subtitle and dragged it 1490 ms early, well inside a one-minute bound, therefore accepted, therefore written over a subtitle that had been right. Movement size says nothing about whether the destination is correct. Only the audio does.

**How it measures.** `silencedetect` at −30 dB w/ a 0.35 s minimum reports where silence ends — the cheapest available proxy for a speech onset. Cue starts are swept against those onsets from −4 s to +4 s in 25 ms steps; a cue counts as a hit when an onset falls within 250 ms of it, bucketed to 50 ms. The shift w/ the most hits wins. Four things about that fit are load-bearing:

- ! **The answer is the middle of the plateau, ¬its edge.** Every shift inside the 250 ms match tolerance scores identically → taking the first strictly-better shift biases every measurement by exactly −250 ms, in the direction that makes a good subtitle look early.
- ! **The hit floor is relative.** Onsets exist only inside the sampled windows, ≈a tenth of a feature → most cues cannot match at any shift. An absolute floor of 25 hits rejected Bambi II at 21 of 46 reachable cues — a 45% peak. The floor is the larger of **12** and a quarter of the cues the windows actually reach.
- ! **A flat sweep is not an answer.** The peak must beat the sweep's own mean by **1.4×**, or the result is inconclusive. Without it, noise produces a confident number and correct subtitles get refused.
- ! **It must also beat its best rival.** The mean is not enough alone, ∵ a noise sweep is a field of near-equal local maxima and the winner among them still clears 1.4× the mean. The winning shift is compared against the best shift **more than a second away**, and must beat it by **1.25×**. Measured on real media: unmeasurable titles score 1.04–1.10× there while scoring 1.33–1.50× against the mean, and a genuinely misaligned subtitle scores 1.41–1.92×. That gap is the whole separation; the mean-only test does not have one.

**The drift test.** A rate error hides from a single shift — the subtitle sits on the speech at the start and is minutes out at the end, and no one shift fits the film. The windows are split in half, each half fitted separately, and a disagreement past `DriftWithinMs` refuses the result whether or not the global fit landed. ! Checked **first**, ∵ the case where the global fit returns null is exactly the case a rate error produces. Two conditions bound it, both paid for:

- **Six windows, or no drift verdict.** Each half must be a measurement in its own right, and at the four-window floor a half is two windows of noise arguing w/ two more. Every one of the 26 drift refusals in the 1.2.4.0 field logs came from a four-window title, and the ones re-measured are unmeasurable — given a 1500 ms displacement of their own subtitle, the fit cannot find it.
- **The halves are judged on a gentler rival bar, 1.1×.** A stretched subtitle is smeared across its own half of the error by construction → the peak of a half-fit is broad even when the rate error is real. The whole-film bar applied to halves refuses every rate error there is — measured at 1.24× on the early half of a 0.04% stretch, which the 1.25× bar rejects by a hair.

**The coarse reading, and why *Six windows, or no drift verdict* still stands.** On a plan of fewer than `DriftWindows` windows the same half-against-half fit is still run — two windows a side of a four-window plan — and carried on the result as `CoarseDriftMs`. It is a **release condition and nothing else**: it produces no verdict, refuses nothing, and is never compared against `DriftWithinMs`. The bullet above is unweakened by it, ∵ that evidence says a 2-a-side reading cannot be trusted to **call** a rate error, and nothing here calls one. Read the two failure directions separately: a spuriously **large** reading fails the release condition → the title is refused exactly as today, and Simpsons S01E10, which reads −3125 ms at four windows, behaves identically before and after. A spuriously **small** reading on a genuinely stretched file is the only new risk, and it is what the injected-error sweep measures — one survivor in 40 correct titles at +800 ms, none at −800, +1500, +2500 or +5000. The 26 drift refusals in the 1.2.4.0 field logs were four-window titles **refused** by a 2-a-side reading; using the same reading only to release inverts which direction its weakness costs.

! **Gated on the windows the plan asks for, ¬the windows the read returned.** `CoarseDriftMs` is taken when `sample.Plan.Count < DriftWindows`; the judged `DriftMs` keeps `sample.Windows >= DriftWindows` as before. It is also taken **only on an `Aligned` verdict**, built into the returned result at the one site that carries it — so the value cannot reach a branch even by accident, and the fit is ¬run for the `Inconclusive` and `Misaligned` short titles that could never use it. That is the difference between roughly doubling the cost of scoring a short title and adding ≈8%: `Drift` runs two half-width sweeps over the same cue list, so it is nearly as expensive as the whole-track fit, and 111 of 141 four-window titles in the sweep reach a verdict that can never read it. Gated on windows *read*, a six-window plan that yielded only four would fit **three** windows a side over sparse onsets and carry that — a different and unmeasured population. A four-window plan always reads all four (`SampleAsync` returns null below `min(MinimumWindows, count)`), so the coarse reading is only ever 2 + 2. Under a one-window plan `Drift` returns null below two windows a side → nothing is carried and the guard refuses, unchanged.

**Either detector can supply it.** Where the silence pass reached `Inconclusive` and voice detection settles the title as `Aligned`, the second `Score` runs over the detected onsets and its coarse reading is the one carried → a release can rest on webrtcvad onsets. The gates it passes are the sweep's (`MinimumHits`, the share floor, `PeakRatio`, `RivalRatio`), ¬the detector's, so the second pass is held to the same bar as the first. Measured w/ the second pass live: every VAD-settled `Aligned` under an injected stretch was still refused ∵ the drift condition failed, and recovery on real engine output was identical w/ and without it.

**300 ms, ¬500, ∵ the baseline is wider.** A drift reading measures the separation between the two halves' centres. Three a side of six windows sit **0.60** of the runtime apart; two a side of four sit **0.667** apart → the same end-to-end error reads *larger*. `DriftWithinMs = 500` over ⅗ tolerates 833 ms end to end; `CoarseDriftWithinMs = 300` over ⅔ tolerates **450 ms** → a short title is held to a **stricter** end-to-end bound than every longer title already is. Swept over the release counts, 300 costs no recall on correct files (25 of 40, the same as 350/400/500) and drops the +800 leak from 3 to 1. ! A five-window plan is unreachable — `PlanWindows` emits 1, 4, or ≥6 — so "two a side" is always exactly 2 + 2 and the baseline is exactly ⅔. No untested intermediate case exists.

**Sampling, ¬reading.** Windows are planned across the *cue* span, never the container → titles and end credits stay out of the sample. Under ten minutes of cues the whole track is read in one pass (seeking around it costs more than reading it). Above that, 4–16 windows of 90 s spread evenly = 11–25% of a feature, measuring in 2–6 s. ! `-ss` and `-t` go **ahead of** `-i`, or ffmpeg decodes everything up to the window.

! **The downmix belongs inside the filter graph.** `-ac 1` is an output option, applied downstream of `silencedetect`, which then reads the source layout → on 5.1 silence is reported only where every channel is quiet, so a continuous music bed in the surrounds hides every dialogue pause and the whole measurement goes flat. It is `aformat=channel_layouts=mono` in the graph instead. Selecting the dialogue channel w/ `pan=mono|c0=FC` was tried and removed: on a stereo source ffmpeg fills the missing channel w/ zeros, exits 0, and returns one silence spanning the window → a "did it fail?" fallback never fires and every stereo title measures off near-silence; on the one 5.1 title available it also produced *fewer* usable onsets than the downmix.

**Both checks fail open.** Inconclusive → sync it, and keep what comes back. A verifier that refused what it could not measure would block whole classes of title — an action film w/ a continuous score can genuinely produce no peak at any shift, sampled or whole-track.

**Which titles are unmeasurable, and why it is not a tuning problem.** Star Trek TNG, Mad Men, Community, Monty Python and The Simpsons all return inconclusive, and the reason is the **mix** rather than the sample: their inter-line floor — engine hum, score, room tone, laugh bed — never falls below −30 dB, so the onsets found sit on music cuts instead of line starts and correlate w/ the cues at no shift. Two remedies measured and rejected: tripling sampled coverage (windows every 2 min instead of 6, 10 windows over a 22-minute episode) changes nothing; raising the threshold to −22 dB w/ a 0.25 s minimum is worse in both directions — it doesn't rescue The Simpsons and it destroys TNG S02E02, which measures correctly at −30 dB. The honest test is to displace a subtitle by a known 1500 ms and ask for it back; these titles cannot return it, and inconclusive is the truthful answer.

**What does not measure them, and was tried.** ① An adaptive silence bar (the title's own mean level +12 dB, averaged over all windows) combined w/ a correlation of the whole speech envelope against the whole subtitle envelope, replacing cue starts matched against silence ends. Kept in `verifycheck --correlate`, ¬shipped: Twin Peaks (the strongest title available to the shipping check) and The Simpsons (unmeasurable at all) score the same to three decimals on every contrast statistic — r 0.106 vs 0.104, peak-over-rival 1.27 vs 1.28. No threshold admits one and excludes the other. It is also biased against the onset fit by roughly the width of `AlignedWithinMs` — cue boxes are shown early and hang past the end of the line, so peak envelope overlap sits 475 ms earlier than the onset answer on Twin Peaks and 700 ms on TNG — and the bias is a property of the subtitler's timing style, ¬of the sync. See V9 for the table and for why a refuse-only variant is worse rather than safer. ② Reading onsets as energy *transients* instead of silence boundaries: a laugh bed and an orchestral score live in the speech band and are loud, so a voice beginning over them is not a distinguishable step. That is `verifycheck --flux`, measured across 18 parameter settings on The Simpsons without ever producing a verdict → V10, ¬shipped.

**The voice-detection second pass.** What those titles need is **spectral voice activity detection rather than level detection**, ∵ their inter-line floor never falls below the bar at any setting → no threshold isolates speech in them. `ScoreAsync` supplies it: where the silence fit reached `Inconclusive`, webrtcvad reads the **same planned windows**, and the sweep is re-run against the onsets it returns. On the five-title set it settles Monty Python's Flying Circus — 94 onsets where `silencedetect` found noise, a firm `Misaligned +775 ms` — and reaches no verdict on Mad Men or The Simpsons, which stay refused.

- ! **Only `Inconclusive` consults it.** A `Misaligned` reading of the silence ends the check where it stands. Both detectors are onset detectors reading one mix; letting a second opinion overturn a refusal turns two agreeing gates into whichever one is looser, and the refusal is the safe direction.
- ! **The fallback is post-sync only.** The pre-sync decision is still `SyncVerifier.Score` — synchronous, silence alone. A pre-sync `Aligned` from voice detection would *skip* files the engine fixes today, so the second pass could subtract writes rather than only add verdicts; post-sync it can only refuse a result or confirm one.
- **The verdicts are read under the same gates.** `MinimumHits`, the share floor, `PeakRatio` and `RivalRatio` are the constants of the sweep, ¬of the detector → the second pass is refused as readily as the first. A second `Inconclusive` returns the **first** result, so the numbers logged are the ones the shipping gates produced.
- **Always on, no setting.** It costs a payload invocation on titles that already failed to measure, and only on those. `Only sync when the audio checks and voice detection are conclusive` continues to decide what an inconclusive verdict *means*.

**How the detector is reached.** `ISpeechOnsetSource` is a one-method seam — windows in, onsets out — so `SyncVerifier` never sees the payload and the harnesses can drive it w/ a fake. `AssyVadOnsets` implements it over `IAssyCliRunner.VadAsync`, which is `assy-cli vad <video> --ffmpeg <path> --window <start>:<length> … --json`. It accepts a reading only on `ok: true` w/ a non-empty onset array and `windowsRead > 0`; anything else — a missing payload, a non-zero exit, unparseable stdout — returns null and the first verdict stands unchanged. ! The `vad` subcommand is **local to the payload's entry wrapper**, dispatched on `argv[0]` before upstream's argparse sees it → `BuildVad` emits no global options ahead of it. Putting `--no-color`/`--config-file` first misses the dispatch and upstream rejects `vad` w/ exit 2.

**The engine's own score is the fallback where the check is blind.** `ffsubsync` runs a real VAD and prints what it thinks of the alignment it chose — `score:`, `offset seconds:`, `framerate scale factor:` — on stderr of every run. `EngineAlignment` reads those three off the full stderr before the tail is cut, and `SyncOrchestrator` divides the score by the seconds of subtitle actually **on screen** to make two titles comparable. A subtitle that genuinely aligns w/ its video measures **41.7–161.3**; one that cannot possibly align — another episode's subtitle over this video — measures **9.5–10.4**. `MinimumEngineScore` is **40** — the lowest honest reading, ¬a midpoint between the two populations. It shipped at 20 (half the lowest true reading, twice the highest false one) and moved up ∵ the field closed the gap the bench had left open: a Mythbusters run measured **23.1** and **11.9** on pairings the engine could not possibly align, against **92.5** honest, and 20 admits the 23.1. An unmeasurable title must clear what a genuine alignment scores, ¬merely beat what an impossible one does. Two limits define its use, both load-bearing:

- ! **Consulted only when the audio check returned `Inconclusive`, and it can only refuse.** Above the bar nothing changes and the fail-open behaviour stands. It adds a verdict where there was none and takes none away.
- ! **A high score is never a warrant.** Futurama S01E04 scores 49.5 — mid-range, comfortably "confident" — while applying a 1.043 PAL stretch to an NTSC DVDRip that the audio check refuses and `MaximumRateDrift` refuses independently. The score is the engine agreeing w/ itself → it can never overturn a `Misaligned`. This is exactly the failure the independent check exists to catch, and why the check is not replaced by the engine's opinion of its own work.

The score is also unavailable to the **pre-sync** decision, which is the other thing the audio check does that this cannot: knowing whether a subtitle needs syncing at all, before paying for a sync.

Together those recover The Simpsons, Monty Python, Mad Men and TNG S02E02 — each returns a known 1500 ms displacement to within one 50 ms step — while Bambi II and Twin Peaks keep the answers they already had, at the highest confidence of the set. What remains unfixed is **sampling**: 4×90 s of a 22-minute episode yields ≈85 s of detected speech, where one contiguous six-minute read of the same episode yields 126–262 s and measures where the sampled version cannot. ! That thin yield no longer *also* costs the title its verdict — the hit floor is taken against the onset supply rather than the cue count, below — but the sample is still the thinner measurement.

**One sample, two questions.** `SampleAsync` reads the audio once per target and both checks score against it. The window plan comes from the pre-sync cue span, and onsets are absolute times in the video → the same sample remains valid for the post-sync cues; a result that moved far enough to invalidate it is a result the fit will refuse anyway.

**The bound is centred on the lead subtitles are authored with.** What the check observes is one number that is three things summed: `observed gap = authored display lead + real sync error + detector lag`. Only the middle term is a defect, so a raw `|gap| < bound` charges every subtitle for the other two. Measured across 49 titles the check already calls aligned, drawn under two independent seeds that agree (162 and 170 ms), the population sits at a **median +170 ms** w/ p95 314 and 44 positive readings against 5 negative — a convention, ¬noise around zero. The check therefore judges `|shift − TypicalLeadMs| <= AlignedWithinMs`, w/ `TypicalLeadMs = 170` and `AlignedWithinMs = 200`.

| bound | refuses raw `\|gap\|` | refuses **centred** |
| --- | --- | --- |
| 100 ms | 82% of known-good | 35% |
| **200 ms** | 39% | **8%** |
| 300 ms | 6% | 0% |

- ! **The lead is a population constant, ¬per-file.** A constant authored lead and a constant real offset are the same signal in audio; nothing recovers the per-file value at any precision. 170 ms is what the population supplies.
- ! **Detector lag is inside it and cannot be detected away.** webrtcvad reads the same population at a median of −224 ms against `silencedetect`'s −225 — a 1 ms difference — so the excess over the ≤125 ms that published spotting practice permits is common-mode across detectors. Centring cancels it; a better detector does not.
- ! **`DriftWithinMs` is a second, raw constant, and the split is load-bearing.** Drift is `late − early`, a **difference** between two fitted shifts → the lead appears in both halves and cancels. Centring it would bias every drift reading by −170 ms. The same applies to the stretch guard in `SyncOrchestrator`, which judges a rate. Positions are centred; differences are raw at 500 ms.
- **Measured against the founding constraint.** 15 known-good titles under the centred bound: **1 (7%) newly sent** to the engine, **1 fixed, 0 broken, 14 untouched** — against ~8% predicted from the independent floor sample. The one that moved (`The Spanish Inquisition`, source −150 ms → centred 320) came back at +250 ms, a genuine correction the 500 ms bound left alone.

**Why the thresholds are compiled in.** A user-facing setting here has no upside — lowering it refuses correct syncs, raising it admits wrong ones. ∵ the retroactivity checks compare stored numbers against the constants, shipping different values re-opens exactly the records they change. ! Those checks must use the **same** centred test as the live verdict; left raw, they reopen records the check then re-skips, forever.

**Why the hit floor is bounded by the onset supply.** `MinimumHitShare` asks for a quarter of the cues the windows can reach. What a title can actually *supply* is onsets, and on a continuously-scored mix the two differ by an order of magnitude: Chappelle's Show yields **42 onsets against 127 reachable cues**, so a 25% bar of the cue count is a bar no subtitle can clear however well it is aligned. It hit 20 of those 42 — a 48% strike rate, about as strong as this method gets — and read as 16%. The floor is therefore taken against `min(reachable, onsets)`.

! This is **¬**a relaxation of the gates that separate signal from noise. `PeakRatio` and `RivalRatio` are untouched, and `MinimumHits=12` is the absolute backstop underneath: Kids Next Door peaks at **11** hits and stays refused, which is the whole reason the constant exists. Measured on the six titles a field log said were unmeasurable — two rescued (both to `Aligned` within 150 ms, → *skipped, already aligned*, ¬a write), four unchanged, and the five-title calibration set byte-identical on every verdict, shift, hit count and floor.

! **More onsets is ¬the same fix and does ¬work.** Dropping `silencedetect`'s `d` from 0.35 to 0.10 on Chappelle triples the onsets (42 → 139) and drops `/rival` from 1.43 to **1.24**, through the gate. The onsets gained are micro-gaps, ¬line starts — the same wall V9 and V10 hit from two other directions.

Gate constants: `MinimumHits=12` · `MinimumHitShare=0.25` · `PeakRatio=1.4` · `RivalRatio=1.25` · `RivalGapMs=1000` · `HalfRivalRatio=1.1` · `DriftWindows=6` · `TypicalLeadMs=170` · `AlignedWithinMs=200` · `DriftWithinMs=500` · `CoarseDriftWithinMs=300` · `MinimumWindows=4` · `MaximumWindows=16` · `WindowSeconds=90`.

**The refusal path carries its own numbers.** `VerificationResult` reports `Hits`, `Floor` and `Onsets` alongside `Strength` on every verdict incl. `Inconclusive` → the log line says which of the three gates refused a title. Before this it reported a flat `peak 0.00x` on every inconclusive result whichever gate fired, ∵ `Nothing()` discarded the strength `BestShift` had already measured, and the field logs could not attribute their own largest failure bucket.

### `SubtitlePlacer`

Owns the move from scratch into the library, and is the only place that decides what a result *is*. Overwrite mode applies to **external subtitles only** — an embedded track has no file to overwrite, so it always becomes a new sidecar regardless of `ExternalWriteMode`.

! **An OCR'd source is never overwritten.** The Convert stage's output is text and the source is a bitmap → an in-place write would destroy an `.idx`/`.sub` pair the plugin cannot regenerate and replace it w/ a file of a different format under the wrong extension. `Place` excludes `RequiresOcr` from overwrite mode outright; `placecheck/` asserts it.

! **Nor is a result whose format changed.** The Transform stage always emits SubRip → stripping an `.ass` subtitle yields `.srt` content, and overwriting would leave SubRip bytes in a file still named `.ass`, which Jellyfin then fails to parse. `SameFormat` compares extensions and falls back to a sidecar when they differ → the user keeps a readable original and gains a readable result. Same failure as the OCR guard, reached from the other end of the pipeline; `placecheck` covers both directions.

Overwriting stores the backup **first** and abandons the whole placement if that fails, leaving the user's file exactly as it was. `BackupVault.Store` returns an existing backup rather than replacing it → repeated syncs never overwrite the copy of the true original w/ a copy of the plugin's own output.

`Place` holds a lock across collision resolution **and** the move. `SubtitleNaming.ResolveCollision` picks a free name by testing `File.Exists`, and two targets on one video sharing a language and flags — two embedded tracks, typically — can be in flight together under `SyncQueue`. Without the lock both find the same name free and move onto it, leaving two records pointing at one file.

### `SubtitleDeduplicator`

Runs once per item, after every target for that item has been through the orchestrator. Both call sites already loop per item → the join point existed.

! **It runs after the sync stage, and that ordering is the whole design.** Before syncing, two files holding the same words are ambiguous — one is correctly timed and one is not, and nothing available here can tell which. After syncing they are both correct → identical text means genuinely redundant. This is also why a group is abandoned if *any* member failed to sync: the guard is `SyncStatus` being `Synced` or `Skipped` for every candidate in the slot, ¬a timing comparison.

Candidates are grouped by `SubtitleSlot`, the same key `AssignVariants` uses → forced, SDH and per-language tracks can never collapse into one another. The keeper is the file the **user** chose: `Provenance != Created` sorts first, then **`CreationTimeUtc` ascending**, then size, then an unnumbered name, then path ordinal for determinism. The plugin's own extraction is the one that loses, leaving the user's filename untouched.

The age tiebreak exists ∵ an automatic downloader — Jellyfin's own *Download missing subtitles* task w/ OpenSubtitles behind it — keeps adding same-language files beside ones already working. Between two files the user did not create, the one that was **already there** is the one they have been watching w/; size decides nothing about quality and let a newer arrival displace it purely for being longer. ! **Creation, ¬modification.** The plugin retimes in place, so `LastWriteTimeUtc` is stamped by this plugin's own work and would rank the file it most recently synced as the newest.

! **`CreationTimeUtc` is ¬uniformly available.** Linux hosts report a birth time only where the filesystem carries one; without it .NET returns a fallback that sorts *first*, so an unavailable time wins the tiebreak. Uniformly unavailable is harmless — every candidate ties and the chain falls through to size, the previous behaviour. **Mixed** is the bad case, and a media tree on one filesystem does not produce it. Copying a sidecar into the library restamps it, so this ranks *arrival in the library*, ¬the age of the subtitle.

Removal reuses the contract `SubtitlePlacer.Overwrite` runs on — `BackupVault.Store` gates the delete, and a `null` backup abandons it. Two details of that gate are load-bearing and were both wrong in the first cut:

- ! **The copy is stored under a `duplicate` label.** `Store` returns an existing destination rather than overwriting one, and a `Retimed` record already holds a vault entry under the removed file's own name — the user's pre-overwrite original. Without the label the call was a no-op returning the *old* backup → the gate passed while nothing was copied. `record.BackupPath` is still only assigned when null, so the pre-sync original stays the thing rollback restores; the labelled copy exists to make the gate mean what it says.
- ! **Provenance is promoted only from `Retimed`.** `Superseded` tells `RollbackService` to restore → promoting a `Created` record made rollback copy the plugin's own output back into the library, permanently, since the next rollback would restore it again. That was the *common* path, ∵ `ChooseKeeper` preferentially removes plugin files. A `Created` record now keeps its provenance and rollback deletes, a clean no-op against a file already gone.

Both outcomes are recorded as a `Deduplicate` stage, including the dry-run case → duplicate counts reach the config page instead of only the log.

Matching lives in `SubtitleSimilarity` and is deliberately **two-axis**: **content** (cue text, markup and punctuation stripped) and **formatting** (style declarations and per-cue styling). Both must clear **0.85**. Content alone would collapse an `.ass` onto a differently-styled `.ass`, or a plain subtitle onto a fully italicised one, discarding styling the survivor cannot express. Files whose extensions differ score zero on both axes and are never compared further.

! Formatting is scored as the **worse** of declarations and usage, never a blend. A 60-cue ASS file emits one `style=` token and 180 per-cue tokens → a completely different style definition scored 99.4% when pooled. Measured by `dedupecheck`, not theorised.

#### Why word bigrams, and why 0.85

Content is Sørensen–Dice over **word bigrams taken across cue boundaries** — not whole cues, not single words. Both alternatives measured and both fail:

| Pair | whole cues | single words | **bigrams** |
| --- | --- | --- | --- |
| the same subtitle, cues re-split in two | 0.0 % | 100.0 % | **100.0 %** |
| an OCR'd copy, 1% of characters misread | 80.3 % | 97.6 % | **95.3 %** |
| a wholly different translation | 0.0 % | 90.2 % | **9.8 %** |

Cue-level matching collapses to zero the moment a release re-splits its cues — a silent false negative, the duplicate stays and nothing is logged. Single words separate nothing: two unrelated subtitles in one language share most of a vocabulary, and a different translation scores 90%. Bigrams survive re-splitting and still put unrelated text near zero.

The threshold sits at the widest available separation in the `dedupecheck` fixtures:

| | content |
| --- | --- |
| an OCR'd copy, 2% of characters misread | 90.9 % |
| a re-release rewording 10% of its cues | 89.0 % |
| ← **0.85** → | |
| a re-release rewording 20% of its cues | 78.1 % |
| a bad scan, 5% of characters misread | 77.9 % |

That gap is the largest in the fixture set and 0.85 is its midpoint, ≈4 points of margin each side. Anything tighter (0.95 was the first candidate) rejects the plugin's own OCR output against a clean text copy of the same subtitle — a duplicate that genuinely should collapse. 0.89 lands *on* a measurement rather than between two.

An OCR-specific relaxation was considered and rejected: the plugin knows which files it OCR'd from the `Convert` stage, so the threshold could drop when one side came from OCR. It buys nothing — a scan bad enough to fall under 0.85 lands at 77.9% and a different edition lands at 78.1%, so below that line the metric cannot tell them apart at all and the relaxed band is either empty or wrong.

! The threshold is a **constant, ¬a setting**. A user-lowered similarity dial is a data-loss dial, and the failure it produces is silent.

`MinimumCues` (10) is checked against each file's **cue count** before any bigram is built — forced tracks are a handful of cues and would otherwise match anything they were cut from. `SubtitleProfile.Read` does the parsing (format key, cue count, three token tallies) so a file is read once per group rather than once per comparison: `ReadCues` and `ReadFormatting` each walk the file separately, so scoring a pair directly from paths costs four passes and a group of *n* used to cost `4(n-1)` w/ the keeper re-parsed every time.

### `RecordReconciler`

Enforces *The status panel invariant* in `CLAUDE.md`: **the UI may lag, it may never lie.** Discovery is the authority on what exists; `Reconcile(itemId, targets)` runs immediately after it and settles that item's rows against what it just offered. `SyncRecord.Stale` is the result, and `GetStatus` filters on it.

! **A `Retired` row is skipped outright.** The plugin deleted that file itself, so discovery can never offer it; falling through would restamp it `Stale` and hide the removal its stage records.

! **A retired row returns only when its file is back on disk**, ¬when a target merely names it. Jellyfin goes on advertising a sidecar the plugin deleted until that item's metadata is refreshed → the offered set contains the removed path, the un-retire branch matches it, and the row rejoins the cards w/ nothing behind it; the pass after the refresh then finds it unoffered and marks it `Stale`, taking the removal off the stage table for good. `File.Exists` on `OutputPath` is what separates a file a user put back from one only the metadata remembers. Field evidence: 23 removals were hidden this way in a single scan, w/ the deduplication stamp and the row's `UpdatedUtc` minutes apart and no stage written between them. `SyncOrchestrator.StampStage` carries the same guard for the second route — a phantom target that reaches the pipeline settles as an outcome, and un-retiring there would lose the removal just as completely.

A row is **live** when the offered set contains its `TargetKey` **or** an offered target's `SubtitlePath` equals its `OutputPath`. ! The second half is not redundant. `TargetKey` is derived from the sidecar's path, and `SubtitleDeduplicator.Canonicalize` renames the survivor without moving its key → on key alone, deduplication's own tidy-up reports the surviving subtitle as missing the very next run.

A row that is no longer live is **retained and uncounted** when it names a `BackupPath` or an `OutputPath`, and **removed** only when it names neither. That split is the invariant's, ¬a convenience: a `BackupPath` row is the sole pointer into the vault and a `Created` row is the only proof rollback has that the plugin wrote that file. What is left over — a failed, unsupported or pending row that wrote nothing and backed nothing up — has no restorable state at all, and those are exactly the rows that were inflating the cards.

Three call sites, all per item, all after the deduplicator: the full scan, `LibraryEventHandler`, and `SyncItem`. A deleted sidecar therefore stops counting on the next refresh of *that* item rather than on the next nightly sweep.

`MarkOutOfScope(visited)` is the full scan's alone, and runs **only on a sweep that reached the end** — a cancelled scan has visited an arbitrary prefix and would mark the remainder stale. It covers the one case per-item reconciliation cannot see: a library the user disabled, whose items are never visited and whose rows nothing would otherwise touch. Those are marked, never dropped, ∵ re-enabling the library is one checkbox and the history should come back with it.

Deduplication does not wait for the next pass to be honest about itself: `SubtitleDeduplicator.Remove` sets `Stale` on the row whose file it just deleted, in the same run. That is what closes the *written, then removed* gap — 18 of 41 writes in one field scan — where a `Synced` card counted files that no longer existed for a full day.

! `ReopenFailed` skips stale rows. Reopening one queues work against a target discovery does not offer → it can only fail again, and it would re-inflate the very count the retry button exists to clear.

### `RollbackService`

Undoes everything the plugin did to the library. One record, one verb, chosen by `SubtitleProvenance` and nothing else:

- **`Retimed`** → restore `BackupPath` over `OutputPath`. ! Never delete: the file is the user's.
- **`Created`** → delete `OutputPath`, but **only if the filename also carries the current marker suffix**. The record alone is not sufficient proof of ownership, and this is the only `File.Delete` in the plugin that touches a library path.

! `Retimed` is the enum's **zero value on purpose** — a record deserialized from before the field existed defaults to the branch that *restores*; if it has no backup it reports `Skipped` and leaves the file alone. The wrong default here deletes user data silently.

Records are removed only for outcomes that actually happened. A failed restore keeps both its row and its backup, ∵ the row is the only pointer into the vault and losing it strands the backup permanently. A delete refused for a marker mismatch counts as a failure for the same reason — changing `MarkerSuffix` after a sync would otherwise drop the record and leave the file.

**Rollback runs even in dry run.** Dry run locks writes *into* the library; rollback only writes a file the user already owned or removes one the plugin made. A user who re-enabled dry run before deciding to undo would otherwise be stuck, and a dry-run record has no output and no backup anyway.

The pass is single-flight and the endpoint refuses while `SyncQueue.InFlight` is non-zero. That guard is not a lock — an `ItemAdded` event can still start a sync immediately after the check — but the failure it leaves behind is a re-synced subtitle, ¬a lost one.

---

## EventHandlers/`LibraryEventHandler`

Subscribes to `ItemAdded` and `ItemUpdated` so a new item or a freshly downloaded subtitle doesn't wait for the nightly sweep. A Bazarr download surfaces as `ItemUpdated`.

**Debounced per item at 30 s.** Jellyfin fires `ItemUpdated` repeatedly during a scan, and a naive handler would queue the same file a dozen times for one logical change. The debounce map is swept past 5,000 entries so it can't grow without bound over a long uptime.

**Then gated on whether anything actually changed.** The debounce only collapses a burst; a refresh arriving a minute later still got the full treatment. `ItemChangeGate.HasWorkToDo` runs immediately after the scope test and before discovery, dropping the event when the item looks exactly as it did when the plugin last finished w/ it. ! Both entry points call `Commit` once the item is fully processed — the handler after its deduplication pass, the task after its own — which is what makes the whole thing work: the commit happens **after** the writes, so the refresh those writes provoke compares equal and dies at the gate.

Work is dispatched fire-and-forget ∵ blocking a library event handler stalls the scan for everyone. Known limitation: during a full scan this can park many tasks on the semaphore. Replacing it w/ a bounded channel and a single consumer is Phase 6 work.

## Services/`ItemChangeGate`

Answers one question ahead of any filesystem work: is this library change worth opening the item for? It exists ∵ the plugin was feeding itself — `RefreshItemAfterSync` queues a metadata refresh after every write, Jellyfin answers w/ `ItemUpdated`, and the handler treated that as a fresh change → a full scan generated a second, unbounded wave of event-driven syncs chasing its own tail, outside the scheduled task's control.

**The signature is stats, ¬hashes.** Per item: the gate stamp, then the video path and every external subtitle path Jellyfin knows about, each w/ size and last-write time. Paths come from `IMediaSourceManager.GetMediaStreams`, a database read. The per-target guard in `SyncOrchestrator` already decides correctness and pays a full SHA-256 of the subtitle plus a 128 KB partial hash of the video to do it — the right price once per genuine change, the wrong price on every metadata refresh of a library sitting on SMB.

! **The stamp is wider than `OutcomeStamp`.** It adds the offset bounds and the discovery flags — `ProcessExternalSubtitles`, `ProcessEmbeddedSubtitles`, `ProcessEmbeddedWhenExternalExists`, `DeduplicateSubtitles`, the language allow list. ! That one enters the stamp **negated**, so the 1.4.0.0 rename leaves every stored stamp byte-identical and no library is retroactively reopened by a rename alone. `OutcomeStamp` deliberately excludes the bounds ∵ a record decides those precisely from its stored numbers, but the gate sits **in front of** that decision → a setting left out here means the item never reaches the code that honours it, and the retroactivity guarantee silently stops at the event path. Throttling settings stay out of both.

**In memory and deliberately not persisted.** A restart drops every signature and the next pass re-checks everything — the same work the nightly scan does anyway. That keeps a bug here bounded at "redundant scan" and structurally incapable of causing a missed sync; the durable correctness gate remains `IsStillCurrent`/`IsExhausted`, which read the store. The map clears wholesale past 20,000 items rather than being swept, for the same reason. ! That bound is only real ∵ `TargetLocks` holds the target while the store is read — until it did, the redundant scan could become a redundant *sync*.

**The full scan commits but never consults.** `FullLibrarySyncTask` calls `Commit` per item so the refreshes it triggers are absorbed, and `Forget` for each record it prunes, but it does not gate itself on volatile in-memory state — a scan the user asked for looks at the store and the disk. Only the event path reads the gate. `AutoSubSyncController.SyncItem` commits for the same reason the task does. ! All three writing entry points must, or the one that doesn't leaves the loop open behind itself.

The signature is stored as a SHA-256 digest rather than the string it was built from: nothing ever reads it back — the only operation is equality — and at the map's own 20,000-item bound the raw strings ran to ≈14 MB.

## Tasks/`FullLibrarySyncTask`

The primary driver. Sweeps in-scope items, discovers targets, processes each, reports progress.

**Items run in parallel; each item's own targets run in order.** `Parallel.ForEachAsync` drives the sweep at `ResolveMaxConcurrentSyncs()` clamped to `SyncQueue.HardMax`, and progress comes from an `Interlocked` counter rather than a loop index.

! The degree of parallelism here is **not** the concurrency limit and is not enforcement. `SyncQueue` is still the only gate and is what `MaxConcurrentSyncs` and `AdaptiveConcurrency` act on. This number is the *ceiling that gate could ever admit*: offering fewer items would cap the gate below its own limit, and offering more would only park extra threads on the semaphore while they hold pre-queue work like fingerprint hashing. Deliberately **¬**`HardMax` unconditionally, for that second reason.

The sweep was strictly sequential until 2026-08-14, which meant the concurrency setting governed only library-event syncs and the manual endpoint — the nightly scan, the one workload where throughput matters, ignored it entirely. It also starved `AdaptiveConcurrency` of any sample that could distinguish one level from another → G1.

Per-item ordering is kept ∵ `SubtitleDeduplicator.ProcessItem` compares an item's outputs against each other and needs every one settled first. Items are independent: they touch different files, and `SyncStore` serializes every mutation behind its own lock. Each item is committed to `ItemChangeGate` once its targets and dedup pass are done.

Also **prunes**: records whose `ItemId` no longer resolves **and** whose video file is gone from disk are removed, along w/ their backups. Without this the store only ever grows — media gets deleted, but the rows describing its subtitles stay forever, and the whole file is loaded at startup.

! **Both signals are required, and the record and the backup are removed together.** The second signal exists ∵ an unmounted share, a moved mount point, or a library removed and re-added all make Jellyfin drop items for media that still exists → on the missing-item signal alone the plugin would delete the only copy of a subtitle it had overwritten. Removing the record while keeping the backup is not a safe middle ground and was the earlier behaviour: the record is the only index into the vault → a backup that outlives its record is unreachable forever and rollback can never restore that file. Either both go or neither does.

Rollback and "clear database" are deliberately **¬**tasks — things a user does after looking at the result, not things that should fire on a timer.

## Configuration

`PluginConfiguration.Normalize()` runs from `Plugin.UpdateConfiguration` → applies on every save regardless of client. It clamps the numerics, sanitizes `MarkerSuffix` to a non-empty alphanumeric string, trims the list fields.

! **Every collection property is an array, and that is not a style choice.** Jellyfin persists plugin configuration as XML, and `XmlSerializer` does **not** call the setter for a collection property — it calls the *getter* and `Add`s each deserialized element into whatever is already there. A `List<T>` w/ a non-empty initializer therefore appends its own defaults to the stored value on every load: the retired engine chain, saved as `ffsubsync, alass`, came back as `ffsubsync, alass, ffsubsync, alass` and grew again each restart. Arrays are built separately and assigned through the setter → the initializer is a genuine default a stored value replaces. `EnabledLibraryIds` and `LanguageAllowList` are both arrays for this reason. **Do not convert either back to `List<T>`.**

! **A retired setting's element must be ignored, ¬rejected.** `SyncToolChain`, `AlassSplitPenalty` and `AssyConfigFilePath` are gone from the type while existing servers still have them in stored XML. `XmlSerializer` drops an element w/ no matching property, and Jellyfin's `LoadConfiguration` catches *any* deserialization exception and saves defaults over the user's file → a property removal that did throw would wipe every setting beside it. `configcheck` writes the 1.1.0.0 shape and reads it w/ the current type to prove it doesn't.

**Overwriting always backs up, and there is no setting for it.** `SubtitlePlacer.Overwrite` calls `BackupVault.Store` unconditionally and returns `null` when it fails → a subtitle the plugin could not back up is never replaced. A `KeepBackups` flag used to exist, defaulting on, w/ the config page spelling out that turning it off while overwriting left rollback able to delete but not restore. Removed: the argument for keeping it — an admin might want no plugin state on the config volume — does not survive contact w/ the consequence, which is destroying a file the user may have hand-timed w/ no way back. Side-by-side mode already serves anyone who wants the original left alone. The property is **deleted rather than pinned to `true`** ∵ Jellyfin's config deserializer ignores unknown keys → a stored `"KeepBackups": false` from an older install is dropped on load.

**The two dependent options are phrased inclusively, and default off.** `ProcessEmbeddedWhenExternalExists` and `RunOcrWhenTextExists` each sit under a parent checkbox and grey out when it is off. They read as *do the extra thing* rather than *skip it* ∵ the exclusive phrasing forced the useful default to be a **ticked** box that was also greyed — a control showing "on" while doing nothing, which is the single most confusing state a dependent setting can be in. Inclusive + unchecked says the same thing and looks inert, which it is.

! **The inversion is a rename, and a rename loses the stored element.** `SkipEmbeddedWhenExternalExists` shipped through 1.3.0.0; `XmlSerializer` binds by element name, so the new property would read as its own default and silently flip the setting on anyone who had chosen it. `[XmlElement("SkipEmbeddedWhenExternalExists")] bool? LegacySkip…` keeps reading the old element and `AdoptLegacySettings()` folds it — **on load only**. Folding on save would overwrite the choice the user just made w/ the value they had before. It nulls the legacy field as it folds, so it is idempotent and the element stops being written the first time the page saves. `configcheck` proves both directions plus the double-fold. `RunOcrWhenTextExists` needs none of this — it never shipped.

`OutputEncoding` is free text, blank-checked only, passed as `--encoding <value>`. No injection is possible (`ArgumentList`), but a bad value is exit 2 on every sync → S6 in the twenty-first pass.

## Api/`AutoSubSyncController`

Four endpoints, all covered by a **class-level** `[Authorize(Policy = Policies.RequiresElevation)]` → a new endpoint inherits it.

`SyncItem` discovers targets synchronously and dispatches the work, returning `202 Accepted`. `RollbackAll` refuses w/ `409` while syncs are in flight, else returns the counts from `RollbackService`.

! **No endpoint accepts a filesystem path from the caller.** Item IDs must resolve through `ILibraryManager` and every path is derived server-side from the resolved item — that is what keeps sync and rollback from being usable as an arbitrary-file-write primitive.

### `Status` and what it deliberately does not report

`GetStatus` reads `GetAll().Where(r => !r.Stale)` **once** and derives **two** lists from it, one filter apart: the cards and both reason lists take `!Retired`, the stage table does not. Nothing else diverges. See *The status panel invariant* in `CLAUDE.md`: any split beyond that one is how `FAILED` came to disagree w/ *failed*.

**`Retired` is the flag for a row the plugin closed itself** — a duplicate `SubtitleDeduplicator.Remove` deleted, and nothing else today. It leaves the cards ∵ the file is gone from the library; it stays on the stage table ∵ the removal happened and the table is a record of work. `Stale` now means only *discovery no longer offers this*. ! Before the split, `Remove` set `Stale` and the panel discarded **546** removals over three days while its *Duplicate removal* row showed 188 — which were the survivor **renames**, the only dedupe outcome that did ¬set the flag → K1, K2. `Canonicalize` no longer stamps a stage at all; it still saves the record, ∵ `RenamedFromPath`/`OutputPath` are set there and rollback needs them.

! **Both reopen paths skip a retired row**, and `Reconcile` skips one whose removed file has ¬come back — restamping it `Stale` would hide its stage again, and reopening it queues work that can never run. A retired row whose **`OutputPath` is offered again** has had its file put back by hand and rejoins the cards; rollback needs no such path ∵ it removes the rows it undoes. ! Skipping unconditionally strands a restored duplicate off the panel forever → K14.

! **That test is the `OutputPath` alone, ¬the `offered` flag the rest of the method uses** → Q1. K14 read a key match as evidence the file was back, which holds only where the key names the file: `ExternalKey` is a path, `EmbeddedKey` is `emb:<stream>:<codec>` and names a stream **inside the video**. An extracted sidecar removed as a duplicate therefore had its target offered on every scan for ever after, so the row un-retired and rejoined the cards as `Synced` w/ nothing behind it — and `IsStillCurrent` matches the unchanged video, so it never re-ran and never restamped. ! The embedded case is the *common* removal, ¬a corner: `ChooseKeeper` sorts `IsPluginFile` last, so the plugin's own extraction is the copy that loses.

**`StampStage` clears `Retired` before it stamps**, ∵ a settled outcome describes something live and the row is no longer a closed removal. ! It sits **after** the `Pending`/`DryRun` early return, so a dry run and a cancellation — both provisional — cannot reopen a row whose file is gone. Ordering makes it safe: all three call sites run every target through the orchestrator, **then** deduplicate, **then** reconcile, so a removal always lands behind the outcome that cleared the flag. Without it, a retired row that genuinely re-syncs — a settings change reopens it — writes a live file no card counts.

**`RetireRemovedDuplicates` applies the split backwards, on load**, so the history returns rather than the row restarting from zero — 546 removals in the field store this was written against. ! It matches on the stage **message**, ¬the kind: `Canonicalize` stamped the same `Deduplicate/Succeeded` on the survivor it renamed, and that row records no removal. The constant lives on `SyncStore` and `Remove` builds its message from it, so the two cannot drift; dry run's *would be removed* does ¬match ∵ it opens on a different word. Idempotent — it clears `Stale`, so a second load matches nothing.

Shaped by one rule: ! **never render a number that cannot move.** A counter wired to nothing reads as a real zero, and a permanent zero next to a real one is worse than no row at all.

! **`SetAside` is on no card, and `Total` therefore exceeds the cards below it.** Every other status has a tile and the set summed to *tracks seen*; a suppressed track now appears only in its stage row's `SKIPPED`. That gap is deliberate and was the user's call over the alternative — a *set aside* card, which would have kept the sum. ! **The rows are still counted in `Total`** ∵ discovery did offer the track and the panel describes the library as it is; dropping them from `Total` as well would make *tracks seen* disagree w/ the store. → a `SetAside` row is reachable **only** through the stage table, so anything that stops stamping its stage makes it invisible outright, which is the K3 shape the suppression fix exists to prevent. Nothing else may join it off the cards without the same explicit decision.

**No load-time migration moves the stored rows.** A row left `Unsupported` by an earlier version restamps on its next pass — the suppression check is the **first** thing `ProcessAsync` does, ahead of `IsExhausted` (K13) — so the old count is lag, ¬staleness, and the invariant permits exactly that. `RetireRemovedDuplicates` needed a backwards pass ∵ its rows were `Stale` and discovery would never offer them again; these are offered every scan.

`SummarizeStages` walks a **fixed pipeline description** rather than the stages present in the store, emitting a row only where the corresponding setting is on: `Convert` absent unless `ConvertImageSubtitles`, `Transform` unless `RemoveHearingImpairedTags`, `Deduplicate` unless `DeduplicateSubtitles`; `Sync` unconditional. `Acquire` is not in the description at all — nothing records it, ∵ subtitle acquisition is Phase 8 and unbuilt.

**The refusal split lives on the cards and the reason lists, ¬on the stage table.** `SyncOutcome.IsAudioRefusal` decides both: it separates the *failed* card from *rejected by audio check*, and it separates `FailureReasons` from `RefusalReasons`, which render as two blocks. ! A single list over both statuses is what shipped first, and its total matched neither card — a `failed 1` tile above a list summing to 191. Both lists go through one `Reasons(...)` helper so a change to the grouping cannot reach one and miss the other.

**The stage table has no `Rejected` column, and refusals are ¬counted anywhere in it.** The column existed 1.4.1.0–1.4.2.0 and was removed: `Fail` and `FailStage` set `RefusedByAudio` from the stage kind, so only a `Verify` failure can ever be a refusal → the cell was structurally unfillable on the other four rows and printed `0`, which reads as *the audio check ran here and refused nothing* about a step it does not run. Rendering `—` there was the first fix and is what the column then was: five rows of table width for one row of data already carried by the *rejected by audio check* card and the *Rejected by audio check* reason block.

! **`Failed` still excludes them, on the `Verify` row.** A refusal is ¬a failure — that distinction is the whole of 1.4.1.0 and folding the counts into `Failed` to save them would undo it, on the one row where the difference is visible. The lookup therefore stays paired `(Record, Stage)`: a stage outcome alone cannot tell the two apart. The table reports what each **step** did; *how many* the audio check refused is a question the cards answer → K5, J8 one level down.

**Neither a `Pending` nor a `DryRun` record stamps a stage.** `StampStage` returns early for both, and `Migrate` `continue`s on both before it reaches `StageFor`. ! `DryRun` mapped to `Skipped` on the default `Sync` kind, so while dry run was on — its state on a fresh install — untried subtitles filled the *Synchronization* row's `SKIPPED` beside genuinely already-in-sync ones → K6. Deduplication's dry-run *would be removed* stage is written by `MarkStage` on a different path and is unaffected.

**Both reason lists are bounded at `ReasonLimit = 100`**, high enough never to cut a real list — the field store's 331 refusals produce four rows — and kept only as a valve for the J3 case, where a message stops collapsing into its group and renders one row per subtitle. ! The earlier bound was **8**, low enough to truncate a live list silently so it totalled less than the card above it, which is the D1/E3 shape twice fixed → K9. `UnsupportedReasons` was unbounded and now shares the limit.

**The reason lines are stripped of their status prefix for display only.** A stored message opens w/ `Rejected:`, `Failed:`, `Skipped:` or `Unsupported:`; `WithoutStatusPrefix` removes that word and re-capitalizes what follows, ∵ the heading over the list already names the outcome and repeating it on every line costs the width the reason needs. ! The **stored** message keeps its prefix — the log lines and `LogOutcome` read it, and it is what the store groups by.

**The timing column is a mean, ¬a total.** It was a lifetime sum of `ElapsedMs` per kind, and the store is never truncated except by "clear database" → the number only ever grew and answered no question anyone asks. A mean per timed run answers a real one: OCR at 21 s a track against sync at 5 s a track is where the sweep's wall time is going. `AverageMs` divides by the stages that **carry a timing** rather than by all of them, ∵ a `Skipped` transform records zero and would drag the mean toward a duration nothing took. `Deduplicate` times nothing → empty denominator, `AverageMs` is `null`, and the page renders a dash rather than `0s`.

**The median shift is a median of magnitudes.** `MedianAppliedOffset` takes `Math.Abs` before ordering: signed, an early subtitle cancels a late one, and a library that corrected every track can report ≈0, which reads as nothing having moved → K8. `formatOffset` likewise tests `Math.abs(ms) < 1000`, ∵ signed, −1200 fell under the threshold and rendered as milliseconds where +1200 rendered as seconds → K7.

**The panel carries no as-of stamp.** `LastRecordUpdateUtc` was computed and serialized w/ **no reader** → K10; a reader was added, then both were removed — the panel refreshes on its own timer and a line saying when it last did adds nothing a user acts on. ! K10's shape is what to watch for, ¬its subject: a field on `\Status` that nothing renders is dead weight the next reader mistakes for a feature. Delete both halves or neither. The *waiting* card was labelled *not yet run*, which is false for a row parked after a transient failure → K11.

`SummarizeDependencies` follows the same rule for tools: the sync engine is always listed ∵ nothing works without it; the subtitle converter when `ConvertImageSubtitles` **or** `RemoveHearingImpairedTags` is on; Tesseract only when `ConvertImageSubtitles` is — a server w/ no Tesseract is not misconfigured if it was never asked to read a bitmap.

Every message comes straight from `PayloadRuntime`, which is why each names its tool **and version** — `assy-cli 6.4 is ready.`, `seconv 5.1.0 is ready.` A row saying "the subtitle converter is ready" would be the only line on the panel that could not be matched against the pin in `payload.lock.json`, the first thing anyone debugging a payload wants. The same message becomes `Downloading seconv 5.1.0.` mid-fetch, `The seconv 5.1.0 payload has not been downloaded yet.` before first use, and a platform message on a RID w/ no published asset. Tesseract is the exception, having no pin — its line reports the **resolved directory**, ∵ which of several installs got picked is what an admin actually needs to know.

**Polling, ¬pushing.** The config page re-arms a single `setTimeout` from inside the response handler — 2 s while `InFlight > 0`, 20 s otherwise — rather than running a `setInterval`. An interval fires whether or not the previous request came back, so a slow response stacks requests; re-arming on completion cannot. `visibilitychange` and `pagehide` stop the timer → a backgrounded dashboard tab costs nothing. The busy interval is 2 s rather than 1 s ∵ `Status` calls `ISyncStore.GetAll`, which deep-clones every record under the same lock the sync workers write through — on a large library the most expensive thing the page can ask for, at exactly the moment the store is busiest.

There is deliberately **no records-table endpoint**. A per-track table for a library-sized workload is a lot of UI surface for something the Jellyfin log already reports, and it drags in paging, sorting, filtering and per-row actions. `/Status` summary counts cover the question users actually ask — did it work, and how much is left.

`/Status` also reports `AssyRuntime`'s readiness, and the config page shows that line **even when there are no records at all**. The two conditions coincide exactly when it matters: a server whose payload never downloaded has nothing to sync and therefore no rows, so a panel keyed on row count alone stayed hidden on the one screen the admin was looking at for an explanation.

**Per-stage counts** come from the same records grouped by `SubtitleStage.Kind` — succeeded, skipped, rejected, failed, mean elapsed. ! Each stage is carried **paired to the record that wrote it**, ∵ a stage outcome alone cannot tell a refusal from a failure and `IsAudioRefusal` reads the record. Without the pairing the column counted 50 `Sync` and 369 `Verify` refusals as failures — 419, the whole *rejected* card — under a heading saying `FAILED`, which is the split the panel invariant forbids. The page renders **every** row the description emits, greying out one nothing has reached (`stageRow`'s `isIdle`), ∵ a step that has never run is itself worth seeing. This is what the stage records were recorded for: "how much of this run was OCR, and how much of it worked" is a different question from "how many subtitles synced", and rolling them together hid the expensive half.

! **`Sync` is rendered, and the row disagrees w/ the headline cards on purpose.** An earlier design omitted it — two numbers for one thing, disagreeing by design, was judged worse than one. Kept now ∵ the cards count **records by their final status** while the row counts **the last outcome of a step**, and the second is the only place a per-step failure is visible at all. The two answer different questions and neither is redundant.

! **The stage columns cannot be added to the card totals.** Two reasons, both structural:

- **A stage outlives the run that wrote it.** `ProcessAsync` loads the stored record and `RecordStage` overwrites a single kind, so a target refused at `Verify` on one run and failed at `Convert` on another is counted in both rows. Field data: 67 + 486 failed stages against 544 failed records.
- **`Fail`'s default kind is `Sync`** → the `Sync` row absorbs every failure that names no stage, incl. "no subtitle file could be produced" and a placement failure. It is *failed at or before the sync step*, ¬the engine's own failure rate.

Clearing `Stages` per run was **considered and rejected**: both short-circuit paths return without stamping, so every target skipped on an unchanged fingerprint (1,720 of ~2,180 in one field scan) would lose its stages and never regain them, collapsing the table to the few hundred that ran that night. The table is honest about *what*, ¬about *when*.
