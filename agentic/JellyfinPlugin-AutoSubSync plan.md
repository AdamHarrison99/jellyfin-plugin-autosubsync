# Jellyfin Plugin: AutoSubSync

Automatic subtitle synchronization for a Jellyfin library, driven by
[AutoSubSync](https://github.com/denizsafak/AutoSubSync)'s headless CLI (`assy-cli`).
Handles both external sidecar subtitles and subtitles embedded in the media container.

---

## Context

Out-of-sync subtitles are the single most common quality complaint in a self-hosted library.
They come from three places:

1. **Downloaded sidecars** (OpenSubtitles, Bazarr, Subliminal) timed against a *different* release
   of the same title — different framerate, different edit, different intro/ad breaks.
2. **Embedded tracks** in a remux that were themselves lifted from a mismatched source.
3. **Re-encodes** where the video was retimed but the subtitle was copied verbatim.

Jellyfin has no built-in resync. The manual fix is to open AutoSubSync's GUI, drag in the video
and the subtitle, pick an engine, and save — per file. For a library of thousands of items that
doesn't scale.

`assy-cli` exists precisely for this: it's the Qt-free, headless entry point to the same sync core
the GUI uses, with `--json` machine-readable output and structured exit codes. This plugin is the
glue: it discovers subtitle work inside a Jellyfin library, hands each unit of work to `assy-cli`,
places the result where Jellyfin will pick it up, and remembers what it has already done so it
never redoes the same file twice.

### What this plugin does NOT do

Permanent non-goals:

- It does **not** reimplement subtitle synchronization. All alignment is delegated to `assy-cli`.
- It does **not** rewrite media containers. Embedded tracks are extracted and re-emitted as
  sidecars; the original file is never modified. (Remux-in-place is a discussed option — see
  *Embedded Subtitle Strategy*.)

Out of scope for **v1**, planned for later — see *Roadmap: the staged pipeline*:

- OCR of image-based tracks — PGS, VobSub, DVB (Phase 8).
- Stripping SDH annotations (Phase 9).
- Downloading subtitles for media that has none (Phase 10).

Each of those *manufactures* a subtitle rather than retiming one that exists, which is a real
change in what the plugin is. `RM-SCOPE` in the roadmap is where that is argued.

---

## Upstream: what `assy-cli` actually gives us

Verified against `main/cli.py` and `main/constants.py` at
[denizsafak/AutoSubSync](https://github.com/denizsafak/AutoSubSync).

### Install

```bash
pip install assy          # or: uv tool install assy
pip install assy[torch]   # adds Silero VAD support for ffsubsync
```

Entry points: `assy` (GUI) and `assy-cli` (headless). A `Dockerfile.cli` and a
`docker compose --profile cli` service are published in the same repo.

### Subcommands

| Subcommand | Purpose |
|---|---|
| `sync <reference> <subtitle>` | Align one subtitle to a video **or to another subtitle** |
| `shift <subtitle> <ms>` | Shift timings by a fixed millisecond offset |
| `batch` | Many pairs via `--folder`, `--video-dir`+`--subtitle-dir`, or repeated `--pair V S` |
| `config get\|set\|unset\|list\|path` | Manage the persistent user config JSON |
| `version` | Print version |

### Flags this plugin relies on

Global: `--config-file`, `--log-level`, `-q/--quiet`, `-v/--verbose`, `--no-color`

`sync`: `-o/--output`, `-t/--tool {ffsubsync,alass,autosubsync}`, `--save-mode`, `--save-folder`,
`--encoding`, `--prefix`/`--no-prefix`, `--suffix`, `--json`

`batch`: everything above plus `--pair V S` (repeatable), `-r/--recursive`, `--output-dir`,
`--skip-processed`/`--no-skip-processed`, `--mark-processed`/`--no-mark-processed`,
`--continue-on-error`, `--json`

**The plugin always passes `-o` explicitly** and never relies on `--save-mode`. The plugin owns
output placement, because Jellyfin's sidecar naming convention is what determines whether the
result is picked up as English vs. forced vs. SDH — `assy-cli`'s save modes don't know about that.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | At least one sync failed |
| `2` | Usage or configuration error |
| `130` | SIGINT |

### JSON output shape

`sync --json` emits exactly one object on **stdout** (logs go to **stderr**):

```json
{
  "ok": true,
  "input": "/media/subs.srt",
  "reference": "/media/video.mkv",
  "output": "/media/synced.srt",
  "tool": "ffsubsync",
  "message": "",
  "returncode": 0,
  "elapsed_ms": 18342,
  "cancelled": false
}
```

`batch --json` emits **NDJSON**: one object per pair (with an extra `"skipped": bool`), then a
final `{"summary": {"total":N,"ok":N,"failed":N,"skipped":N,"aborted":bool}}`. The parser must
therefore read stdout line-by-line, not as a single document.

### Engine capabilities (from `constants.py: SYNC_TOOLS`)

| Engine | Formats | Subtitle as reference? | Notes |
|---|---|---|---|
| `ffsubsync` | `.srt .ass .ssa .vtt` | **yes** | VAD on the audio track; slowest, most robust |
| `alass` | `.srt .ass .ssa .sub .idx` | **yes** | Fast; handles ad-break splits; best with a reference sub |
| `autosubsync` | `.srt` only | **no** | ML speech detection; requires a real video |

This table drives `ReferenceSelector` — see *Sync Strategies*.

---

## Deployment: the plugin bundles its own `assy-cli`

Jellyfin ships as a .NET server; `assy-cli` is a Python tool. Neither the official Jellyfin image
nor the LinuxServer image has Python or `assy` preinstalled, and asking every admin to install it
themselves makes the plugin's behaviour depend on whatever version they happened to get.

**The plugin vendors one pinned build of `assy-cli` and uses only that.** It lives at
`assy-cli/<rid>/assy-cli[.exe]` inside the plugin folder — Jellyfin extracts the whole plugin zip,
so shipping more than a DLL is just a matter of putting the files in the archive.
`Cli/AssyRuntime.cs` resolves the path once at startup and restores the executable bit on Unix,
which a zip extraction drops.

Consequences, all deliberate:

- **No `AssyExecutablePath` setting.** There is nothing for the user to point at or get wrong.
- **No runtime update path and no version negotiation.** A change in upstream's CLI contract can
  never break an already-installed plugin.
- **Upgrading `assy-cli` is a development-time action**: rebuild the payload from the target
  upstream tag, bump `AssyRuntime.BundledVersion`, re-verify the JSON contract fixtures, cut a
  plugin release.
- **No toolchain-verification task and no "Test CLI" button.** Both existed to diagnose a
  user-supplied binary. With a bundled one, the only failure mode left is "no payload for this
  platform", which `AssyRuntime` logs once at startup and every sync surfaces as a clear error.

`ProcessStartInfo.ArgumentList` is used throughout — never a concatenated command string — so no
media path can be interpreted as a shell metacharacter.

### Building the vendored payload

`assy` depends on PyQt6, ffsubsync (numpy/scipy), and static-ffmpeg, so a naive freeze is large.
The dev-time build script (`tools/build-assy.ps1`) produces a **CLI-only PyInstaller onedir**
bundle per runtime identifier, with the Qt GUI modules excluded and **ffmpeg deliberately left
out** — see *Packaging Decisions* for why both of those matter.

The plugin points the child process at Jellyfin's own ffmpeg by prepending
`Path.GetDirectoryName(IMediaEncoder.EncoderPath)` to its `PATH`, so the bundle carries no second
copy and the two can never disagree about codec support.

---

## Project Structure

```
Jellyfin.Plugin.AutoSubSync/
├── Jellyfin.Plugin.AutoSubSync.csproj
├── Plugin.cs                              # BasePlugin<PluginConfiguration> entry point
├── PluginServiceRegistrator.cs            # IPluginServiceRegistrator — DI registration
├── build.yaml                             # Plugin metadata (GUID, version, targetAbi)
├── manifest.json                          # Jellyfin plugin repository index
│
├── assy-cli/                              # Vendored, pinned assy-cli payload
│   ├── linux-x64/assy-cli
│   └── win-x64/assy-cli.exe
│
├── Configuration/
│   ├── PluginConfiguration.cs             # Settings model
│   └── configPage.html                    # Admin dashboard UI
│
├── Models/
│   ├── SubtitleTarget.cs                  # One unit of work: item + subtitle + origin
│   ├── SubtitleOrigin.cs                  # Enum: External, Embedded
│   ├── SyncRecord.cs                      # Persisted outcome for one target
│   ├── SyncStatus.cs                      # Enum: Pending, DryRun, Synced, Failed, Skipped, Unsupported
│   ├── AssyResult.cs                      # Deserialized assy-cli --json object
│   └── AssyBatchSummary.cs                # Deserialized batch summary object
│
├── Data/
│   ├── SyncStore.cs                       # JSON persistence + atomic write + backup restore
│   └── BackupVault.cs                     # pre-overwrite copies, outside the media folders
│
├── Cli/
│   ├── AssyRuntime.cs                     # Resolves the bundled binary; pins the version
│   ├── IAssyCliRunner.cs                  # sync / shift
│   ├── AssyCliRunner.cs                   # process spawn, timeout kill, JSON parse
│   └── AssyArgumentBuilder.cs             # pure argv construction (unit-testable)
│
├── Subtitles/
│   ├── ISubtitleExtractor.cs
│   ├── FfmpegSubtitleExtractor.cs         # ffmpeg -map 0:<index> extraction to a temp file
│   ├── SubtitleNaming.cs                  # Jellyfin sidecar name build + collision avoidance
│   ├── SubtitleOffsetProbe.cs             # First-cue timestamp reader for the no-op check
│   └── SubtitleDiscoveryService.cs        # Enumerate embedded + external streams per item
│
├── Services/
│   ├── SyncOrchestrator.cs                # Per-target pipeline: extract -> sync -> place -> record
│   ├── SyncQueue.cs                       # Bounded concurrency gate
│   └── LibraryScopeResolver.cs            # Which libraries/items are in scope
│
├── EventHandlers/
│   └── LibraryEventHandler.cs             # IHostedService — ItemAdded / ItemUpdated hooks
│
├── Tasks/
│   └── FullLibrarySyncTask.cs             # IScheduledTask — sweep everything in scope
│
└── Api/
    └── AutoSubSyncController.cs           # REST endpoints for the config page
```

Rollback and "clear database" are **config-page buttons backed by API endpoints**, not scheduled
tasks. They are things a user does deliberately after looking at the result, not things that
should ever fire on a timer.

---

## Data Model

### SubtitleTarget.cs — one unit of work

```csharp
public class SubtitleTarget
{
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string VideoPath { get; set; } = string.Empty;

    public SubtitleOrigin Origin { get; set; }

    // External only: absolute path of the sidecar file.
    public string? SubtitlePath { get; set; }

    // Embedded only: MediaStream.Index inside the container.
    public int? StreamIndex { get; set; }

    public string? Language { get; set; }     // ISO 639-2, from MediaStream.Language
    public string? Codec { get; set; }        // subrip, ass, pgs, dvdsub, ...
    public bool IsForced { get; set; }
    public bool IsHearingImpaired { get; set; }
    public string? Title { get; set; }

    // Stable identity used as the SyncStore key. Survives library rescans.
    // External: "ext:<relative path>"   Embedded: "emb:<streamIndex>:<codec>"
    public string Key { get; set; } = string.Empty;
}
```

### SyncRecord.cs — persisted outcome

```csharp
public class SyncRecord
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public string TargetKey { get; set; } = string.Empty;   // SubtitleTarget.Key
    public SubtitleOrigin Origin { get; set; }

    public string VideoPath { get; set; } = string.Empty;
    public string? SourceSubtitlePath { get; set; }  // input; null for embedded
    public string? OutputPath { get; set; }          // sidecar this plugin wrote
    public string? BackupPath { get; set; }          // pre-overwrite backup, if any

    public string? ToolUsed { get; set; }            // ffsubsync / alass / autosubsync
    public string? ReferenceUsed { get; set; }       // video path or reference-subtitle path
    public long ElapsedMs { get; set; }
    public int? ReturnCode { get; set; }

    public SyncStatus Status { get; set; }
    public string? Message { get; set; }
    public int AttemptCount { get; set; }

    // Fingerprint of the INPUT SUBTITLE at time of sync.
    public long SourceLength { get; set; }
    public DateTime SourceLastWriteUtc { get; set; }
    public string? SourceSha256 { get; set; }        // full hash; subtitles are small

    // Fingerprint of the VIDEO at time of sync. Both must still match for the target to
    // be considered current — see below.
    public long VideoLength { get; set; }
    public string? VideoPartialHash { get; set; }    // size + first 64KB + last 64KB

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public enum SyncStatus
{
    Pending,   // Discovered, not yet processed
    DryRun,    // Would have synced; dry run mode is on
    Synced,    // assy-cli succeeded and the output is in place
    Failed,    // assy-cli returned non-zero, or extraction/placement failed
    Skipped,     // Processed, but the result was discarded as a no-op
    Unsupported  // Cannot be processed at all (image codec, unreadable format)
}
```

### Provenance — how the output came to exist

This is **not** a roadmap concern. v1 already produces outputs of two different kinds, and
rollback has to tell them apart:

```csharp
public enum SubtitleProvenance
{
    Retimed,     // v1: an existing user file we realigned in place. Rollback RESTORES a backup.
    Extracted,   // v1: pulled out of the container. Rollback DELETES; no original exists.
    Downloaded,  // Phase 10. Rollback DELETES. Not regenerable without spending quota again.
    Ocr,         // Phase 8.  Rollback DELETES.
    Stripped     // Phase 9.  Rollback DELETES. Derived from another subtitle.
}
```

`SyncRecord.Provenance` decides rollback's verb. Exactly one value — `Retimed` — restores; every
other value deletes. Getting this backwards deletes a file the user cannot recover: an
`Extracted` record has no `BackupPath`, so "restore the backup" silently does nothing while the
sidecar stays, and a `Retimed` record treated as a creation deletes the user's own subtitle.

External `Overwrite` mode is the only path that produces `Retimed`. External `SideBySide` produces
a new file the plugin owns, so it is `Extracted`-like in rollback terms — it deletes.

### Stages — one record, several operations

v1 performs exactly one operation per target, so `SyncRecord.Status` is unambiguous. Every planned
feature adds a stage before or after the sync, and "Failed" then stops being answerable: a bad
result could be a bad download, bad OCR, or bad alignment, and they are indistinguishable to a
user staring at one status.

```csharp
public enum SubtitleStage
{
    Acquire,    // Obtain a subtitle that does not exist          (Phase 10)
    Convert,    // Make a non-alignable subtitle alignable — OCR  (Phase 8)
    Sync,       // Align against the video's audio                (v1)
    Transform   // Rewrite content — SDH stripping                (Phase 9)
}

public class StageOutcome
{
    public SubtitleStage Stage { get; set; }
    public SyncStatus Status { get; set; }
    public string? Message { get; set; }
    public string? ToolUsed { get; set; }      // ffsubsync / seconv / provider name
    public long ElapsedMs { get; set; }
    public int? ReturnCode { get; set; }
    public DateTime CompletedUtc { get; set; }
}
```

`SyncRecord` gains `List<StageOutcome> Stages` and `SubtitleProvenance Provenance`. The top-level
`Status` stays, and is defined as **the status of the last stage that ran** — so existing
consumers, including `/Status` and the config page, keep working unchanged.

**Records persist across plugin upgrades, so the migration is load-bearing.** A record written by
v1 has no `Stages` array. `SyncStore` synthesizes one on load — a single `Sync` stage carrying the
record's existing `Status`, `Message`, `ToolUsed`, `ElapsedMs`, and `ReturnCode` — and infers
`Provenance` from `Origin` (`Embedded` → `Extracted`, `External` → `Retimed` when `BackupPath` is
set, `Extracted` otherwise). Doing this at load rather than on write means no upgrade task and no
version field. Write a fixture test with a real v1 `records.json`; this runs exactly once per
user and is unobservable until rollback gets it wrong.

### The idempotency guard: fingerprint both sides

A target is skipped on a later sweep only when **both** the subtitle and the video still match
their recorded fingerprints. Fingerprinting only the subtitle is a real bug, not a theoretical
one: a user who upgrades a movie to a better release while keeping the same sidecar would leave
the subtitle fingerprint unchanged, so the plugin would skip it and leave a subtitle synced to a
video that no longer exists.

- **Subtitle**: full SHA-256. Subtitle files are tens of kilobytes; hashing them is free.
- **Video**: a partial hash of `size + first 64KB + last 64KB`, never a full read. Hashing a 40 GB
  remux on every sweep would cost more than the sync it is trying to avoid. This is the same
  strategy `assy-cli`'s own dedup DB uses, and it has the useful property of surviving a move or
  rename while still catching a genuine content change.

This is also what makes the second full scan cheap: the first pass is unavoidably expensive, but
every pass after it is O(new or changed subtitles).

### Persistence: SyncStore.cs

Same shape as a sibling plugin's `PairStore` — this is a deliberate copy of a pattern that already
works in production:

- JSON `List<SyncRecord>` under `IApplicationPaths.PluginConfigurationsPath/AutoSubSync/records.json`
- `lock`-guarded in-memory list; every mutation writes through
- Atomic save: write `.tmp` → `File.Move(overwrite: true)`; backup copy before each write
- Corrupt-file recovery: parse failure → restore from `records.backup.json`
- Stale `.tmp` cleanup on construction

Operations: `GetAll`, `GetById`, `GetByItemId`, `GetByTargetKey(itemId, key)`, `Upsert`,
`UpsertMany`, `Remove`, `RemoveMany`, `Clear`, plus `GetFailed()` for the retry sweep.

**Expected size**: one record per subtitle track, not per item. A 5,000-item library with an
average of 3 subtitle tracks is ~15,000 records. A flat JSON list stays acceptable at that scale
(a few MB, loaded once at startup) but this is the component to revisit first if it doesn't —
SQLite via `Microsoft.Data.Sqlite` is the escape hatch, and the interface is designed so swapping
the implementation touches nothing else.

---

## Discovery

### SubtitleDiscoveryService

For each in-scope item, enumerate subtitle streams via
`IMediaSourceManager.GetMediaStreams(item.Id)` filtered to `MediaStreamType.Subtitle`, then split
on `MediaStream.IsExternal`:

```
BaseItem (Movie | Episode)
    │
    ├── item.Path missing or not a file? → skip (stub/strm/remote)
    │
    ├── For each MediaStream where Type == Subtitle:
    │     │
    │     ├── IsExternal == true:
    │     │     ├── stream.Path is the sidecar on disk
    │     │     ├── Extension unreadable, or VobSub (.sub with sibling .idx) → Unsupported
    │     │     ├── Filename already carries the plugin's marker suffix → skip (our own output)
    │     │     └── → SubtitleTarget { Origin = External, Key = "ext:<relpath>" }
    │     │
    │     └── IsExternal == false:
    │           ├── Codec is image-based (pgs, dvdsub, dvbsub) → Unsupported (see below)
    │           ├── ExtractEmbedded disabled in config → skip
    │           └── → SubtitleTarget { Origin = Embedded, Key = "emb:<index>:<codec>" }
    │
    └── Emit targets
```

**Image-based subtitles (PGS / VobSub / DVB) are out of scope.** None of the three engines can
align a bitmap track without OCR first. They are recorded once as `Unsupported` with a clear message
so the config page can report *"12 tracks skipped: image-based subtitles are not supported"*
rather than looking like silent failures.

### Scope resolution (LibraryScopeResolver)

`ILibraryManager.GetVirtualFolders()` gives the library list; the config holds a list of enabled
library IDs. Item types: `Movie` and `Episode` only.

**An empty library list means nothing is processed, not everything.** A plugin that writes into
media folders must be opted into, and the default has to be the harmless one — a fresh install
that quietly starts rewriting every subtitle in every library on the first nightly run is the
worst outcome this design can produce. `GetItemsInScope` returns early on an empty list rather
than enumerating the library and filtering, so the no-op case costs nothing.

The only other scope filter is the **language allowlist** — sync tracks whose language is in the
list, empty meaning all. There are no ignore-pattern globs: selecting libraries is the scoping
mechanism, and a second path-matching language on top of it is a config surface to explain, a
regex-injection question to defend, and a way to silently exclude media the user forgot about.

---

## Sync Strategy

**Every subtitle is aligned against the video's own audio**: `assy-cli sync <video> <subtitle>`.

`assy-cli` also supports passing a *subtitle* as the reference instead of a video, which is far
faster (seconds instead of minutes, since no audio decode or VAD pass is needed). That is
deliberately **not** used here. Using an embedded track as the reference for an external one
assumes the embedded track is correctly timed, and there is no way to know that — an embedded
track can be just as desynced as a sidecar, and frequently is in exactly the re-encoded releases
where this plugin is most useful. Aligning to a bad reference produces a confidently wrong result,
which is worse than the original problem because the user has no signal that it happened.

The audio is the only ground truth available, so it is the only reference used.

The cost is honest and needs to be planned around: ffsubsync decodes and VAD-analyzes the full
audio track, which is minutes per file. That is why throttling (see *Throttling and Scheduling*)
and fingerprint-based skipping (see *SyncRecord*) are first-class rather than afterthoughts — the
plugin must be cheap on the second run even though it is expensive on the first.

### Fallback chain

`SyncTool` config is an ordered list, not a single value (default `["ffsubsync", "alass"]`). On a
non-zero exit or an `ok: false` JSON result, the orchestrator advances to the next engine before
recording `Failed`. Each attempt increments `AttemptCount`; after `MaxAttempts` (default 2) the
record is `Failed` and won't be retried until its fingerprint changes or the user clicks retry.

---

## Embedded Subtitle Strategy

This is the part with a real design tradeoff, so it's spelled out explicitly.

### The pipeline

```
Embedded target (item.Path, streamIndex, codec=subrip)
    │
    ├── Extract:  ffmpeg -nostdin -y -i <video> -map 0:<index> -c:s srt <temp>/<guid>.srt
    │              (ffmpeg binary from IMediaEncoder.EncoderPath — Jellyfin already ships one)
    │              ass/ssa are extracted as-is; subrip/mov_text are normalized to srt
    │
    ├── Sync:     assy-cli sync <video> <temp>.srt -o <temp>.synced.srt --json
    │
    ├── Compare:  if the applied offset is under MinimumOffsetMs (default 150ms),
    │             discard — the embedded track was already fine. Record Skipped.
    │             This prevents littering the library with no-op sidecars.
    │
    └── Place:    write <video basename>.<lang>[.forced][.sdh].autosubsync.srt next to the video
                  → IProviderManager.QueueRefresh so Jellyfin indexes the new sidecar
```

### Why sidecars and not remux

Rewriting an MKV to replace a subtitle track means either `mkvmerge -o newfile` (full container
rewrite: minutes of I/O per file, doubles peak disk usage, changes the file's mtime and inode —
which breaks hardlink-based setups, torrent seeding, and any other plugin tracking the file) or
in-place track surgery, which no tool does safely. The blast radius of a bug is the user's media
file. **v1 never touches the original.**

The cost of the sidecar approach is honest and must be documented: **Jellyfin will now show two
subtitle tracks for the same language** — the original embedded one and the synced sidecar. The
plugin mitigates this by:
- Naming the sidecar with a `.autosubsync` marker segment so it's identifiable in the picker
- Optionally appending a title segment (`AutoSubSync`) so the Jellyfin UI labels it distinctly
- A config note explaining that the embedded track can't be hidden by a plugin

A v2 `RemuxMode` is a legitimate follow-up, gated behind an explicit opt-in, `mkvmerge` presence
detection, free-space checks, and write-to-temp-then-atomic-replace. It is not v1.

### External subtitle placement

Two configurable modes:

- **`Overwrite`** (default): back up the original to
  `<name>.srt.autosubsync-backup` (once — never overwrite an existing backup), then write the
  synced result over the original path. Jellyfin keeps the same track; no duplicates appear.
  Fully reversible via the **Roll back everything** button.
- **`SideBySide`**: leave the original untouched, write
  `<name>.autosubsync.srt`. Safer but produces duplicate tracks in the picker.

---

## Sidecar Naming (SubtitleNaming.cs)

Jellyfin parses external subtitle filenames as
`<video basename>[.<title>][.<language>][.<flags>].<ext>` where flags include `forced`, `default`,
`sdh`, `hi`, `cc`. Getting this wrong is the most likely source of "the plugin worked but nothing
shows up" bug reports, so it gets its own tested unit.

Rules:
- Always start from `Path.GetFileNameWithoutExtension(videoPath)` — not the item's display name.
- Language: the 3-letter code from `MediaStream.Language`; omitted when unknown.
- Carry `forced` and `sdh` flags forward from the source stream.
- Always append the `autosubsync` marker segment before the extension so the plugin can recognize
  and skip its own output on the next discovery pass — without this, every scan re-syncs the
  previous output, forever.
- Sanitize: strip `< > : " / \ | ? *` and collapse whitespace.
- Collision: if the target exists and isn't ours, append `.2`, `.3`, … up to 10, then fail the
  target with a clear message rather than clobbering a user file.

Example:
`Blade Runner 2049 (2017).mkv` + English forced embedded track
→ `Blade Runner 2049 (2017).en.forced.autosubsync.srt`

### One output per (video, language, flags)

**This section originally required at most one subtitle file per video, language, and flag-set,
with sources competing for a single slot. That rule is withdrawn — it silently destroyed tracks.**

The case that breaks it is anime, and it is not rare. A release routinely carries a full English
subtitle track *and* an English signs-and-songs track for on-screen text, both non-forced and both
marked `eng`. Under slot competition the second one is discarded without ever being logged as a
decision, and the user simply never sees it. The same happens to a commentary track and to the
second of two English sidecars.

**The rule now is: every track that passes the language gate is processed.** Nothing competes and
nothing is discarded. Concretely:

- Two English tracks both get synced. Neither suppresses the other.
- An image track is OCR'd even when a text subtitle of the same language exists, because the two
  are frequently not the same content — the PGS track may be the signs track.
- A track whose language is absent, `und`, or otherwise unrecognizable is **always** processed,
  whatever the allow-list says. Signs-and-songs tracks are the ones most often left unlabelled,
  so filtering them out removes precisely the track the feature exists to catch.

This costs more OCR than slot competition did, and that cost is accepted. The alternative is a
plugin that quietly drops the track the user cared about.

Source *ranking* survives, demoted from a filter to an ordering: external text, embedded text,
external image, embedded image. Cheap work finishes before any OCR starts, so a run that is
interrupted has done the most valuable work first.

**Naming must now disambiguate.** Two `eng` tracks build the same sidecar name, and
`ResolveCollision` treats existing plugin output as reusable, so the second would overwrite the
first. `SubtitleNaming.BuildSidecarPath` therefore takes a variant token — the track title when
the container supplies one, otherwise `track<index>` — inserted before the marker segment. It is
set **only** when an item actually carries several tracks sharing a language and flag set, so
single-track items keep the filenames they already have and no existing output is orphaned.

Flags stay part of the identity, so `Movie.en.srt`, `Movie.en.forced.srt`, and `Movie.en.sdh.srt`
are three different slots and always were. That is also why SDH stripping needs no new naming
concept: its output drops the `sdh` flag, which already lands it in a different slot from its
source. Jellyfin's own filename grammar does the disambiguation.

`SubtitleNaming` still needs no *provenance* segment. Adding one would orphan every file already
written — the plugin would stop recognizing its own output and re-sync it on every scan — and the
variant token above already separates the only files that genuinely collide. How a subtitle was
produced is not what distinguishes two English tracks from each other.

Provenance still exists on the **record**, where rollback needs it to choose restore-vs-delete. It
just never reaches a filename.

---

## Throttling (SyncQueue)

Subtitle sync is CPU- and I/O-heavy: ffsubsync on a 2-hour film is minutes of full-core work.
Running that unthrottled next to a live transcode makes the server unusable, so every sync passes
through one gate.

- **`MaxConcurrentSyncs`** — default `0`, meaning **automatic**. A `SemaphoreSlim`, rebuilt when
  the setting changes so it takes effect without a server restart. This is the entire throttling
  surface; scheduling *when* work runs is what the scheduled task's own trigger is for, and
  Jellyfin's dashboard already exposes that.

`AutoConcurrencyFor(Environment.ProcessorCount)` resolves automatic: **half of the cores, floored
at 1 and capped at 8.**

This is a ceiling, not a target — `AdaptiveConcurrency` still ramps from one and only climbs
while throughput improves, so a box that is actually storage-bound never reaches it. Each sync
saturates a core for minutes on a machine whose real job is transcoding, and half the cores is
the point where a large server can make real progress overnight while still leaving a transcode
its headroom. The cap at 8 matches the manual maximum.

It originally returned 1 at four cores or fewer. That was written when only the library event
handler could put two syncs in flight at once, and it made the automatic setting contradict its own
description on a quad-core box. The ramp has to earn the second slot regardless, so the special
case bought caution the ramp already provides.

`ProcessorCount` respects cgroup CPU limits on .NET, so a container with a two-core quota on a
32-core host resolves to 1 rather than 8.

Two related mechanisms that are **not** throttling and should not be confused with it:

- **`PerSyncTimeoutMinutes`** (default `20`) — a hung-process guard. Without it a wedged child
  would hold a concurrency slot forever. On timeout the process tree is killed
  (`Process.Kill(entireProcessTree: true)`) and the record is `Failed`.
- **Cancellation** — the `CancellationToken` from `IScheduledTask.ExecuteAsync` propagates to the
  same kill path, so stopping the task in the dashboard actually stops the work.

### One limiter is not enough past v1

`MaxConcurrentSyncs` limits **CPU**. That generalizes to OCR, which is also CPU-bound and must
share the same semaphore rather than get its own — two independent limiters of 1 each still put
two cores under load, which is exactly what the setting exists to prevent.

It does **not** generalize to downloading. That is network-bound and constrained by a provider
quota that resets daily, which a concurrency semaphore cannot express: a limit of 1 does not stop
a sweep from spending an entire OpenSubtitles allowance, it just spends it in single file.

So `SyncQueue` becomes two gates with different shapes, not one setting copied:

| Resource | Gate | Stages |
|---|---|---|
| CPU | `SemaphoreSlim(MaxConcurrentSyncs)` | `Sync`, `Convert` |
| Provider quota | Concurrency **and** a persisted daily counter | `Acquire` |
| — | none needed | `Transform` |

The daily counter has to survive a restart, so it lives in the store, not in memory. A sweep that
hits the cap stops requesting rather than failing each item — thousands of `Failed` records
because a quota ran out is noise that buries real failures.

---

## Trigger Points

1. **`FullLibrarySyncTask`** (`IScheduledTask`, daily default) — the primary driver. Enumerates
   every in-scope item, discovers targets, filters out ones the store says are current, and feeds
   the rest through `SyncQueue`. Reports progress so the dashboard shows a real bar.

2. **`LibraryEventHandler`** (`IHostedService`) — subscribes to `ILibraryManager.ItemAdded` and
   `ItemUpdated`. New items and items whose subtitle streams changed (a Bazarr download fires
   `ItemUpdated`) are queued immediately. Debounced per item — Jellyfin fires `ItemUpdated`
   repeatedly during a scan, and a naive handler would queue the same file a dozen times.

3. **Manual** — the config page's **Run full sync now** button (which starts the scheduled task),
   or `POST /AutoSubSync/SyncItem/{itemId}` for a single item.

`FullLibrarySyncTask` also **prunes** on each run: records whose `ItemId` no longer resolves
through `ILibraryManager` are removed. Without this the store grows monotonically as media is
deleted and never shrinks.

---

## Configuration

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Safety ----
    /// Dry run: discover and log everything, write nothing. ON by default, matching
    /// A sibling plugin's convention — the user reviews the plan, then commits.
    public bool DryRunMode { get; set; } = true;

    // ---- assy-cli execution ----
    // The binary is bundled at a pinned version (see AssyRuntime), so there is no executable
    // path to configure and no update path to manage.
    /// Optional --config-file handed to assy-cli. An advanced escape hatch for engine tuning.
    public string AssyConfigFilePath { get; set; } = string.Empty;

    // ---- Engines ----
    /// Ordered fallback chain. First entry is tried first.
    public List<string> SyncToolChain { get; set; } = new() { "ffsubsync", "alass" };
    public int MaxAttempts { get; set; } = 2;

    // ---- Scope ----
    // ! Empty means NO library is processed. Opt in, never opt out.
    public List<Guid> EnabledLibraryIds { get; set; } = new();
    public List<string> LanguageAllowList { get; set; } = new(); // empty = all
    public bool ProcessExternalSubtitles { get; set; } = true;
    public bool ProcessEmbeddedSubtitles { get; set; } = false;  // opt in: it creates sidecars

    // ---- Output ----
    public ExternalWriteMode ExternalWriteMode { get; set; } = ExternalWriteMode.Overwrite;
    public string OutputEncoding { get; set; } = "same_as_input";
    public string MarkerSuffix { get; set; } = "autosubsync";
    /// Discard results whose applied shift is below this — the sub was already fine.
    public int MinimumOffsetMs { get; set; } = 150;

    // ---- Throttling ----
    /// 0 = automatic, resolved from Environment.ProcessorCount.
    public int MaxConcurrentSyncs { get; set; } = 0;
    /// Hung-process guard rather than a throttle: a wedged child would hold a slot forever.
    public int PerSyncTimeoutMinutes { get; set; } = 20;

    // ---- Behavior ----
    public bool AutoSyncOnItemAdded { get; set; } = true;
    public bool RefreshItemAfterSync { get; set; } = true;
}

public enum ExternalWriteMode { Overwrite, SideBySide }
```

`FullScanIntervalHours` and `AutoSyncOnSubtitleDownload` are deliberately absent. Scan frequency is
the scheduled task's own trigger, which Jellyfin's dashboard already exposes, and a Bazarr download
fires `ItemUpdated` — which `AutoSyncOnItemAdded` already covers. Both would have been a second
control for something the server already controls.

**There is no `KeepBackups` setting, and there must never be one.** It existed, defaulting on, with
the config page warning that turning it off while overwriting left rollback able to delete but not
restore. That was the wrong resolution: a setting whose only effect is to make an irreversible
operation irreversible is not a choice worth offering, and a warning does not make it one. The
combination is now unreachable — `Overwrite` always backs up first, and a failed backup abandons
the write and leaves the original untouched.

Removing the property rather than pinning it to `true` is deliberate. Jellyfin's config
deserializer ignores unknown keys, so a stored `"KeepBackups": false` from an older install is
dropped on load and cannot resurrect the behaviour.

### configPage.html

Standard Jellyfin config page (`ApiClient.getPluginConfiguration` / `updatePluginConfiguration`,
`Dashboard.processPluginConfigurationUpdateResult`), sections:

- **Dry-run banner** — shown while dry run is on
- **Safety** — dry run toggle
- **Scope** — library multi-select populated from `ApiClient.getVirtualFolders()`, external /
  embedded toggles, language allowlist, ignore patterns
- **Engines** — the ordered tool chain, max attempts
- **Output** — write mode, backups, minimum offset
- **Throttling** — concurrency
- **Automation** — sync on add, refresh after sync
- **Actions** — **Run full sync now** (starts the scheduled task). This is ordinary operation, not
  a destructive act, so it does not belong in the danger zone.
- **Danger zone** — **Roll back everything**, **Clear database**, both behind a confirmation

There is deliberately **no records table**. A per-track table for a library-sized workload is a
lot of UI surface for something the Jellyfin log already reports, and it drags in paging,
sorting, filtering, and per-row actions. The `/Status` summary counts cover the question users
actually ask — "did it work, and how much is left".

---

## REST API (AutoSubSyncController)

All endpoints `[Authorize(Policy = Policies.RequiresElevation)]`, route prefix `AutoSubSync`.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/Status` | In-flight count and per-status record totals for the config page |
| `POST` | `/SyncItem/{itemId}` | Synchronize every subtitle track on one item immediately |
| `POST` | `/RollbackAll` | Restore every backup, delete every file the plugin wrote |
| `POST` | `/ClearDatabase` | Drop all records; does not touch the filesystem |

No endpoint accepts a filesystem path from the client. Item IDs must resolve through
`ILibraryManager`, and every path is derived server-side from the resolved item — which is what
keeps sync and rollback from being usable as an arbitrary-file-write primitive.

---

## Error Handling

| Scenario | Handling |
|---|---|
| Bundled `assy-cli` missing or not executable | `AssyRuntime` logs once at startup; each sync fails fast with a clear message. No per-item log spam. |
| `assy-cli` exit `2` (usage/config) | Log the full argv and stderr at ERROR — this is a plugin bug or misconfiguration, not a media problem. Abort the run. |
| `assy-cli` exit `1` (sync failed) | Advance the engine fallback chain; on exhaustion record `Failed` with the JSON `message`. |
| `assy-cli` exit `130` | Treat as cancellation, not failure. Leave the record `Pending`. |
| Malformed / absent JSON on stdout | Record `Failed` with the raw stderr tail (capped) — never throw out of the orchestrator. |
| ffmpeg extraction fails | Record `Failed`; do not fall through to syncing a zero-byte file. |
| Image-based subtitle codec | Record `Unsupported` once with a clear message; never retried. |
| Output path collision | Suffix `.2`…`.10`, then `Failed`. Never clobber a non-plugin file. |
| Subtitle or video changed since last sync | Fingerprint mismatch → target becomes eligible again automatically. |
| Timeout | Kill the process tree, record `Failed`, release the semaphore in a `finally`. |
| Store file corrupted | Backup-on-write + restore-from-backup, same as a sibling plugin's `PairStore`. |
| No bundled payload for this platform | `AssyRuntime` logs once at startup; every sync fails fast with a clear message rather than spawning nothing. |
| Path outside any library root | Refuse. Every path handed to a child process is validated against the resolved library roots first. |
| Two triggers race on one target | `SyncStore` key `(ItemId, TargetKey)` plus a per-key in-flight set makes it idempotent. |
| Item deleted after a sync | `FullLibrarySyncTask`'s prune pass drops records whose `ItemId` no longer resolves. |

### Writing the output safely

`assy-cli` writes wherever `-o` points, so pointing it straight at the media folder means a
crashed or timed-out run can leave a partial file exactly where Jellyfin will index it. The
orchestrator therefore always gives `-o` a **scratch path**, and only after a successful,
non-cancelled result does it move the file into place. Placement order for `Overwrite` mode is:

1. Back up the original into the **backup vault** (once — never overwrite an existing backup).
2. Move the scratch file over the original.
3. Record the outcome.

If step 2 fails the backup is still on disk and rollback can restore it. Scratch files live under
`IApplicationPaths.TempDirectory`, not the system temp directory — in a container `/tmp` is often
a small tmpfs, and a subtitle plus a failed run can fill it.

### The backup vault: never beside the media

**Backups live under `PluginConfigurationsPath/AutoSubSync/backups/`, never next to the file they
were taken from.** A backup written into the media folder is a file in the one directory Jellyfin
scans for sidecars, and the extension is the only thing keeping it from being indexed as a
subtitle track — one naming change away from every user seeing duplicate subtitles they cannot
explain. It also puts plugin state where the user's own tooling lives: Radarr/Sonarr cleanup,
folder-sync jobs, and backup software all see it, and media shares are frequently mounted
read-only or with quotas that make the write fail.

`Data/BackupVault.cs` owns the directory. Layout is one folder per record:

```text
<PluginConfigurationsPath>/AutoSubSync/backups/<recordId:N>/<original filename>
```

The record ID guarantees uniqueness — two libraries can each hold a `Movie.en.srt` — while keeping
the original filename intact, so a vault directory is still intelligible to a human who has lost
the database.

Three consequences that have to be handled rather than discovered:

- **Pruning must delete backups.** `FullLibrarySyncTask` removes records for deleted media; without
  a matching `BackupVault.Discard` the vault grows forever holding backups nothing points at.
- **The vault sits on the config volume, not the media volume.** That volume is often small in
  Docker deployments. Subtitles are tens of kilobytes, so a 15,000-record library lands in the
  low hundreds of megabytes — acceptable, but `GetTotalBytes()` exists so the config page can show
  it rather than letting a user discover it when the volume fills.
- **`Clear database` orphans the vault.** The records are the only index into it. The warning text
  says so, and the vault is at a known path so a user can delete it by hand.

---

## Security Notes

Carrying forward a sibling plugin's pre-release audit discipline, these are the areas that will get
scrutiny before the first release:

- **Command injection** — mitigated structurally by `ProcessStartInfo.ArgumentList`. There is no
  code path that builds a shell command string, so a media path containing shell metacharacters
  is just a string.
- **Path traversal** — every path comes from a resolved Jellyfin `BaseItem`, never from client
  input. Output paths are validated to remain under a known library root.
- **Deletion safety** — the plugin only ever deletes files it wrote (identified by the marker
  suffix *and* a matching `SyncRecord.OutputPath`), or restores backups it created.
- **Child process environment** — the child gets an allowlisted environment rather than inheriting
  the server's, so tokens and credentials held by the Jellyfin process are not visible to it.
- **Elevation** — every API endpoint requires `Policies.RequiresElevation`.
- **Log hygiene** — subtitle content is never logged; only paths and engine messages.

### Dry run means no external side effect, not no write

v1 defines dry run as a **media-filesystem lock**: no write to the library. That definition is
exactly right for v1, where the only side effect is a file, and exactly wrong for Phase 10, whose
main side effect is a *network call that spends a finite daily quota*. A dry run that quietly
burns an OpenSubtitles allowance has broken its promise without writing a byte.

The invariant is therefore stated as: **while dry run is on, no stage performs an action
observable outside this plugin's own record store.** Filesystem writes, network requests to
providers, and any future outbound call are all covered by one rule instead of a list that has to
be remembered.

Two consequences that are easy to get wrong:

- A dry run must still report *what it would have done* in numbers the user can act on — "would
  download 412 subtitles" is the entire point of running it before enabling Phase 10.
- The pre-release audit's dry-run item has to be re-verified against the wider wording, not the
  original one. Tracing filesystem calls alone would pass a build that downloads during a dry run.

---

## Roadmap: the staged pipeline

v1 is a single-stage pipeline. Three planned features each insert a stage around the sync, and
they are specified together because they share a data model, a queue, a naming scheme, and a
rollback contract — designing them one at a time produces three incompatible answers to the same
four questions.

```text
v1:        discover ────────────────────────► sync ─► place
roadmap:   discover ─► convert ─► sync ─► transform ─► place
                       Phase 8    v1      Phase 9
```

Phase 10 (`acquire`) is **withdrawn** — see its spec. Build order for what remains is **8 → 9**,
forced rather than chosen: see `RM-ORDER`.

### RM-SCOPE — decided: the plugin never obtains subtitles, only changes the ones present

v1 retimes subtitles that already exist. The three planned features each *manufacture* a subtitle
that did not exist before — by fetching, by OCR, or by rewriting. Individually each looks like a
small extension of "fix my subtitles". Together they move the plugin from **synchronize
subtitles** to **obtain and manufacture subtitles**, which is a different product with a different
support burden: bad OCR, bad provider matches, and over-aggressive SDH stripping all produce a
file that looks correct and is not, and all three generate bug reports that read like sync
failures.

**The decision: acquisition is out of scope permanently.** The plugin never contacts a subtitle
provider. Downloading belongs to the OpenSubtitles plugin for Jellyfin, which already owns that
problem. Phase 10 is withdrawn.

OCR and SDH stripping remain candidates — both operate on a track the user already has, and
neither needs a credential store or a provider quota. They keep the manufacture-quality risk
above, which is why each is gated on proving its tool first.

### RM-ORDER — the phases are a dependency chain, not a preference

**Phase 9 depends on Phase 8** because SeConv, bundled for OCR, is also what performs the SDH
stripping (`--remove-text-for-hi`). Phase 9 ships no stripper of its own, so it cannot precede the
payload that implements it.

The download feature's most-wanted rule is not "fetch when missing" but *replace unwanted broken
versions — SDH, image-based — with clean `.srt` sidecars*. Read closely, that rule is not a
download feature at all: replacing an image-based track **is** OCR, and replacing an SDH track
**is** stripping. Downloading is only the fallback for when neither can salvage what is there.

So Phase 10 cannot ship at its stated scope until 8 and 9 exist. The alternative is to descope it
to "fetch only when nothing exists at all" and treat the upgrade rule as a fourth phase. Either is
defensible; building 10 first and discovering this halfway through is not.

---

### Phase 8 spec — OCR image-based subtitles (`Convert` stage)

**Goal**: turn PGS (Blu-ray), VobSub (DVD), and DVB bitmap tracks into real text subtitles so they
can be aligned like anything else.

Today this is the one category the plugin refuses outright — `IsExtractableCodec` rejects them and
discovery marks them `Unsupported`. A remux whose only English track is PGS gets nothing at all.
The config page now reports exactly how many tracks that is, which is the number that should
decide whether this phase is worth building.

**This phase forces a change in discovery.** `SubtitleDiscoveryService` currently emits one target
per track, independently. Deciding "do not OCR the PGS track, an English sidecar already exists"
requires grouping candidates by (language, flags) and picking the best source per slot — the
ranking in *Sidecar Naming*. That restructuring is the first step of the phase and the reason it
is affordable at all: on a typical Blu-ray library most image tracks are accompanied by a text
subtitle in the same language and are never OCR'd.

**Tool: SeConv**, Subtitle Edit v5.1.0's headless converter. Prebuilt per-platform binaries
(`SeConv-Linux-x64`, `SeConv-Linux-ARM64`, `SeConv-Windows-x64`, `SeConv-macOS-*`), 37–42 MB each,
needing only the .NET runtime and no display. It reads `.sup`, VobSub `.sub` (auto-detecting the
paired `.idx`), and MKV directly, so PGS and VobSub are covered by one dependency.

```sh
seconv subs.sup subrip --ocr-engine:nocr --ocr-db:Latin.nocr
```

Exit codes `0` success, `1` any error. Relevant flags: `--ocr-engine:`
(`tesseract`, `nocr`, `binaryocr`, `ollama`, `llamacpp`, `paddle`), `--ocr-language:`, `--ocr-db:`,
`--dictionary-folder:`, `--no-vobsub-isolate-colors`, `--time-codes-only`.

**The engine choice is Tesseract, and this reverses what this section originally proposed.** The
original argument was that nOCR needs no language pack — Subtitle Edit bundles `Ocr/Latin.nocr`
(475 KB) and `Ocr/Latin.db` (480 KB) — and that its docs call nOCR "very accurate once trained"
and "best for consistent fonts (like DVD/Blu-ray subtitles)", which is this exact case. Measured,
that did not hold: nOCR's best output on a real DVD track had eight errors in three lines, while
Tesseract's had none. See *Step 22 result*.

The costs that argument was avoiding are real and are now accepted: Tesseract needs a per-language
pack (`eng` is 4.1 MB `tessdata_fast` or 15.4 MB `tessdata_best`), and upstream publishes **no
official Windows binary** — UB-Mannheim ships Inno Setup installers, so a Windows payload has to
be built or extracted rather than downloaded. Against that, Phase 11 already provides per-platform
fetch, hash verification, staging and atomic promote, so a second asset is a lock entry and a
manifest row rather than new machinery.

Tesseract is **CPU-only** in practice. Its OpenCL support is experimental, covers only part of the
pipeline, and upstream advises against building it. Of SeConv's engines only `paddle`, `ollama`
and `llamacpp` can use a GPU, and each needs a Python runtime or a model server. `llamacpp` has
now been measured and rejected — see *Step 22b result*. `paddle` was rejected earlier for needing
a brand-specific build: `paddlepaddle-gpu` is CUDA-only, at 455 MB Windows and 724 MB Linux, and
its latest release trails the CPU wheel by a major version.

It vendors the same way `assy-cli` does: pinned per-RID payload, resolved at startup like
`AssyRuntime`, spawned with `ArgumentList`. The lock and `check-payload.ps1` generalize to a second
tool with little change — the lock grows a second key, not a second file. **Built as described in
*Step 25 result*, with one change: nothing is compiled here.**

**Licence**: Subtitle Edit is GPLv3, as are this plugin and AutoSubSync. No new obligation.

**Alternatives rejected**: [PgsToSrt](https://github.com/Tentacule/PgsToSrt) is clean and small but
PGS-only, so VobSub would need a second tool, and it needs `libtesseract5` present on Linux.
[pgsrip](https://github.com/ratoaq2/pgsrip) adds a second Python runtime alongside `assy-cli` plus
MKVToolNix. Raw Tesseract means owning image extraction, binarization, and line segmentation —
Subtitle Edit's real value is the decades of OCR post-correction layered above the engine, and
reimplementing that is the actual work.

### Step 22 result — measured 2026-08-11, and it changes the phase

Run against SeConv 5.1.0 (`SeConv-Windows-x64`, 42 MB) and two real Blu-ray `.sup` files, with
`--ocr-engine:nocr --ocr-db:Latin.nocr`. Four findings, in order of how much they matter:

**1. nOCR does not prompt, and it does not fail. It silently emits `*` for every glyph it cannot
match, and exits `0` printing "Conversion completed successfully".** Confirmed across four
independent sources. The output is a well-formed SRT with correct, usable timings and no text.
Bitmaps were verified legible by exporting them with `bdnxml`.

**The requirement that follows is not optional: an OCR stage must validate output content, never
exit code.** Reject output whose text is dominated by the placeholder or whose alphanumeric ratio
is implausible, and treat that as an OCR failure rather than writing it into the library. Without
this the plugin writes a subtitle of pure `*` over a user's library and records it as synced.

This requirement is *strengthened* by finding 1a below: whether nOCR succeeds turns on subtle
properties of the input that cannot be predicted before running it, so output validation is the
only available signal.

**1a. Colour isolation is engine-specific, and getting it wrong looks like the engine failing.**
The same DVD VobSub track, same three cues, four runs:

| Engine | Colour isolation | Output |
|---|---|---|
| nOCR | on (default) | `*` — nothing at all |
| nOCR | `--no-vobsub-isolate-colors` | `when I was Iying there in the VA hospitaI,` |
| Tesseract | `--no-vobsub-isolate-colors` | `/ ira ine VA nosoiral,` — garbage |
| Tesseract | on (default) | `When \| was lying there in the VA hospital,` |

Each engine wants the *opposite* preprocessing, and the wrong choice looks exactly like an engine
that cannot read the subtitle. The flag is not a quality tweak; it is part of the engine's
identity. `--no-pgs-isolate-colors` is the PGS/DVB equivalent — **measured in *Step 22d result*,
and it behaves the same way**: with isolation left on, nOCR reads nothing from a clean PGS bitmap.

**2. Tesseract with `--fix-common-errors` produced a perfect result.** Adding Subtitle Edit's OCR
post-correction pass to the Tesseract run fixed the one remaining defect (`|` for `I`) and gave
output identical to the source, on all three cues:

```text
When I was lying there in the VA hospital,
with a big hole blown through the middle of my life,
I started having these dreams of flying.
```

**The recipe that works: `--ocr-engine:tesseract --ocr-language:eng --fix-common-errors`, with
colour isolation left on.** nOCR at its best still emitted `Iying`, `hospitaI`, `middIe` and
`ofmy` — eight errors in three lines, which is a broken subtitle. Tesseract at its best had none.
This is the difference between glyph matching against a fixed database and an LSTM that models
language context, and it is not a difference `--fix-common-errors` can close for nOCR.

Two side effects of `--fix-common-errors` to be aware of before enabling it blindly: it **merged a
two-line cue onto one line**, discarding the source's line breaks, and it **adjusted a cue end
time** by 14 ms (minimum-gap enforcement). Neither matters when the stage runs before `Sync`, but
both would matter if it were ever applied to a finished subtitle.

**1b. The PGS samples that failed were all atypical, so PGS is still unproven.** The four `.sup`
sources tested were: three from a parser project's test corpus, all rendered in an outline-style
font, and one retail-sourced sample that turns out to be a colour test card reading "This text
should be red, not blue" in red with a drop shadow. None is a normal white-on-transparent retail
Blu-ray subtitle. **Superseded by *Step 22d result*:** a synthesized white-on-transparent PGS
sample, solid fill, produced a byte-exact match. The outline stroke was the cause, isolated by
holding text, font and renderer fixed. A retail-disc sample is still unavailable, but it is no
longer the blocking unknown it was here.

**2. The nOCR database is not in the SeConv package.** The claim above that "Subtitle Edit bundles
`Ocr/Latin.nocr`" is true of the *full* Subtitle Edit distribution, not of SeConv. The SeConv zip
contains only `seconv.exe`, two native libraries, and `libse.xml`. `Latin.nocr` (476 KB) must be
shipped separately from the repository's `Ocr/` folder, and the tool errors clearly when it is
absent. The payload is therefore SeConv **plus** the database.

**3. Tesseract is not bundled and is not reachable.** `--ocr-engine:tesseract` fails with
"Tesseract not found on PATH". Bundling SeConv does not provide a Tesseract fallback, so the
"Latin works out of the box, everything else needs an admin-supplied tessdata folder" answer below
is worse than stated — everything else needs an admin-supplied *Tesseract install* as well.

**Tested, and it stands — see *Step 22e result*.** nOCR needs no install and can be exact on
solid-font Latin, but it is 6.9% CER on the one realistic sample, `binaryocr` is worse on every
row, and there is no third install-free engine. Tesseract remains required.

**4. The one thing that did work perfectly is timing.** Cue boundaries came out clean on both
files, and `bdnxml` export took 1.7 s for 432 bitmaps. Whatever OCR engine is chosen, the PGS
parsing underneath it is sound.

**Caveat, stated so it is not overread:** both samples render as outline-style fonts, which is a
hard case for a glyph-matching engine, and both came from a parser project's test corpus rather
than retail discs. This does not establish that nOCR fails on all Blu-ray subtitles. It does
establish *how* it fails when it fails, which is the part that dictates the design.

**Settle before building**:

- **Payload size compounds.** ~40 MB per platform on top of an already large `assy-cli` payload,
  now fetched per-platform rather than bundled, plus the 476 KB nOCR database. Measure the v1
  payload first; this is the main constraint.
- **Non-Latin scripts need Tesseract**, meaning per-language tessdata (~5 MB `tessdata_fast`,
  ~15 MB `tessdata_best`). Bundling every language is impossible and downloading on demand
  contradicts the no-runtime-download stance bundling exists to enforce. Proposed answer: Latin
  works out of the box, everything else needs an admin-supplied tessdata folder.
- ~~nOCR's headless unknown-glyph behaviour is unverified.~~ **Answered — see *Step 22 result*.**
  It emits `*` and reports success.
- **OCR fails in recognizable ways**: `I`/`l`/`1`, `rn`/`m`, lost italics, merged two-line cues.
  A subtitle that is 98% right still reads as broken.
- **VobSub is a file pair.** `.sub` + `.idx` travel together and `.sub` is ambiguous — MicroDVD
  text in some releases, VobSub bitmap in others. v1 already resolves this in `IsVobSub`; reuse
  it rather than re-deriving it.

---

### Step 22b result — vision-model OCR measured, and rejected on speed

The case against `llamacpp` had been argued from hallucination risk **without a test**. It was
tested. The hallucination argument was wrong at 2B, and the engine is rejected anyway, on numbers.

Same 3-cue VobSub track, same known ground truth, `llama-server` already warm so no model-load
time is counted:

| Engine | Accuracy | Per cue | 432 cues, extrapolated | Resident |
|---|---|---|---|---|
| Tesseract + `--fix-common-errors` | 3/3 exact | ~0.19 s | ~82 s | ~30 MB |
| Qwen3-VL-2B Q8_0 | 3/3 exact | ~35 s | ~4.2 h | ~2.5 GB |
| SmolVLM2-500M Q8_0 | 2/3 | ~7.4 s | ~53 min | ~1.1 GB |
| nOCR | 0/3 | ~0.006 s | 2.4 s | ~30 MB |

**Qwen3-VL-2B transcribed all three cues exactly**, including the line break inside cue 2 and the
lowercase `lying` that nOCR rendered `Iying`. No hallucination at that size. The cost is the
problem: llama-server's own per-request timings show 42.4 s, 28.5 s and 34.9 s, of which
**essentially all is image prefill** — each 1920×1080 bitmap becomes 1200–1600 image tokens at
~25 ms/token on CPU, while generation is 9–15 tokens in under a second. Prefill scales with image
dimensions, not with warmth, so it does not improve on a real file.

**Shrinking the model trades away the only reason to use one.** SmolVLM2-500M is 4.8× faster
because it encodes each image to ~368 tokens, and it altered cue 2: `with` → `With`, the line
break dropped, and the trailing comma turned into a full stop. Those are not glyph misreads; the
model tidied the text into a well-formed sentence. The hallucination concern is real — it just
lives at 500M, not at 2B.

Two further disqualifiers on a small server:

- **The GPU path crashed.** `llama-server` ships `ggml-vulkan.dll`, which would have been the
  brand-agnostic acceleration `paddle` could not offer. It failed to allocate a 373 MB device
  buffer, loaded anyway, and **segfaulted (exit 139) on the first image**. CPU-only ran fine. An
  engine whose accelerated path takes the process down is not shippable.
- **Memory.** Even SmolVLM2-500M held 1,084 MB resident — double its weights, once KV cache and
  image buffers are counted — for the whole OCR pass. Tesseract's ~30 MB is not comparable.

**Also worth carrying forward: SeConv reported `Converted 0 file(s)` with exit code 0** when
`llama-server` was absent, and again when it was unreachable. That is the third and fourth time it
has claimed success on total failure. It confirms the requirement already stated above — an OCR
stage must validate output *content*, never the exit code.

---

### Step 22c result — the format matrix, completed 2026-08-11

Every image format the phase claims is now measured end to end, with
`--ocr-engine:tesseract --fix-common-errors` and Tesseract 5.5.3 on `PATH`. The source in every
VobSub and DVB row is the same three-cue track with known ground truth, so accuracy is comparable
across rows.

| Source | Input given to seconv | Result |
|---|---|---|
| VobSub, embedded, subtitle-only MKV | `eng.mkv` | 3/3 exact |
| VobSub, embedded alongside video | `vobsub-emb.mkv` | 3/3 exact |
| VobSub, external pair | `ext.idx` | 3/3 exact |
| VobSub, external pair | `ext.sub` | 3/3 exact |
| PGS `.sup` | `simple.sup` | exact |
| PGS `.sup` | `sup1.sup` | exact |
| PGS `.sup`, 432 cues | `complex.sup` | 432 cues, 70.7 s, **163 ms/cue** |
| **DVB, embedded** | `dvb.mkv` | **empty output, exit 0** |

**DVB does not work, and the failure is seconv's.** The DVB track was produced by transcoding the
known-good VobSub track bitmap-to-bitmap, and `ffprobe` confirms six subtitle packets in it.
seconv reports `Converted 1 file(s)` and writes a 7-byte file containing a BOM and two line
breaks. Transcoding that same DVB stream *back* to VobSub and OCR'ing it returns all three cues
exactly, which proves the bitmaps are intact and readable and that only seconv's DVB path is at
fault.

**The fix is available and cheap, and Phase 8 must include it: transcode DVB to VobSub with
ffmpeg before handing it to seconv.** ffmpeg is already a dependency, the conversion is
bitmap-to-bitmap so no rendering is involved, and it is one invocation:

```sh
ffmpeg -i in.mkv -map 0:v -map 0:s:0 -c:v copy -c:s dvdsub out.mkv
```

Without that step the README's claim of DVB support is false.

Two operational findings that will otherwise be rediscovered:

- **seconv needs `--track-number:` for a Matroska input** whenever it does not pick the track
  itself. Without it, it prints `Converted 0 file(s)` and exits 0. `--track-number` counts
  Matroska tracks from 1, so it is *not* the `MediaStream.Index` the extractor uses for ffmpeg.
- **A missing Tesseract binary is reported as success.** With Tesseract off `PATH`, an MKV input
  yields `Converted 0 file(s)` and exit 0. Only a `.sup` input surfaces the real install message.
  This is the fifth distinct way seconv has now claimed success while producing nothing, and the
  first where the cause is a missing dependency rather than a bad input.

Every one of those failure modes produces a missing or empty file behind a success message, which
is the same conclusion the phase already reached from a different direction: **the `Convert` stage
must validate that the output contains plausible cues, and must never trust the exit code.**
`SubtitleContent.HasCues` is the check that already exists for this.

### Step 22d result — nOCR does succeed, and the outline stroke is why it usually does not

The two samples step 22 left outstanding. **nOCR reached a byte-identical match against ground
truth on all six cues**, which no earlier run had. The engine is not broken; the samples were.

The controlling variable was isolated rather than argued. Two `.sup` files were synthesized from
the *same* text, font (Arial 45 px), renderer and canvas (1920×1080, white, anti-aliased), and
differ only in whether a 3.5 px black stroke is drawn under the fill:

| Sample | Colour isolation | nOCR output |
|---|---|---|
| Solid fill | on (default) | `*` on every cue |
| Solid fill | `--no-pgs-isolate-colors` | 8 errors / 6 cues, **no unmatched glyphs** |
| Solid fill | `--no-pgs-isolate-colors --fix-common-errors` | **byte-identical to ground truth** |
| Outline stroke | on (default) | `*` on every cue |
| Outline stroke | `--no-pgs-isolate-colors` | ~30 `*`, e.g. `When * W8s *y*ng there *n the VA *osp*ta*,` |

Four things follow, in the order they matter:

**1. `--no-pgs-isolate-colors` is mandatory for nOCR on PGS, exactly as `--no-vobsub-isolate-colors`
is on VobSub.** *Step 22 result*'s finding 1a called the PGS half untested; it now behaves
identically. With isolation left on, nOCR reads nothing at all from a clean, unambiguous,
white-on-transparent bitmap. The flag pairs with the engine, not with the source.

**2. The remaining 8 errors were all one class, and `--fix-common-errors` closed every one.** They
were `I`→`l` (×5), a lost space (`of flying`→`offlying`), and `"`→`''` (×2). Digits, commas and
apostrophes were already correct — `It cost 1,250 dollars in 1987.` came back exact before FCE.

**3. But `--fix-common-errors` is only safe above a quality floor, and below it makes things
worse.** Run against the poor VobSub output it did not repair it, it amplified it:

```text
nOCR alone:   *hen ! Was !ying there in the VA hospita!,
nOCR + FCE:   ♪ Hen! Was! Ying there in the VA ho spit a!,
```

FCE read the `!` glyphs as sentence terminators, capitalised behind them, split `hospital` into
three words, and promoted a `*` to a music note. **FCE assumes mostly-correct input.** It belongs
after an engine that clears the floor, never as a rescue for one that does not.

**4. The real external VobSub `.sub`/`.idx` pair confirms the failure mode on the other format.**
Isolation on gave `*` on all three cues; isolation off gave `*hen ! Was !ying there in the VA
hospita!,` — 8 errors in 3 lines. Worth noting that this is the *same subtitle content* as the
embedded MKV copy measured in finding 1a, which produced different errors (`Iying`, `hospitaI`).
The palette differs between the retail pair and the ffmpeg-remuxed copy, and nOCR is sensitive to
it. A result measured on a remuxed sample does not transfer to the original.

**Throughput**: 432 cues in 21.4 s, single-threaded — **49 ms/cue**, versus 163 ms/cue for
Tesseract on the same file (*Step 22c result*). nOCR is 3.3× faster and needs no external install.

**What this changes for the phase.** The engine choice is no longer "Tesseract or nothing". For
Latin, solid-font sources nOCR is a genuine in-process fallback — see *Step 22 result* finding 3,
currently the phase's worst deployment constraint. It is a *fallback*, not a replacement:
Tesseract read the outline samples that nOCR could not, and models language context where nOCR
matches glyphs.

**Do not read this as "Tesseract can be dropped".** That was tested directly and it cannot — see
*Step 22e result*, which also retires the `*`-density mitigation this phase had been relying on.

**Two caveats, stated so this is not overread:**

- **The solid sample is synthesized, not from a retail disc.** It is a clean, correctly-formed PGS
  stream — `ffprobe` parses it and ffmpeg renders it — but a real disc adds compression artefacts
  and studio fonts. What it establishes is a *ceiling*: nOCR can be exact, and the ceiling is
  reachable. It does not establish that retail discs reach it.
- **A retail-disc `.sup` was still not obtained.** That sample remains unavailable. It is no longer
  blocking, because the question it was asked to settle — does nOCR ever succeed — is now answered
  yes with the cause of failure identified.

**One more silent-success mode, the sixth.** seconv's **BDN XML** reader is registered as a text
format, not an image one. Given a valid BDN index it emits the PNG *filenames* as cue text and
exits 0 (`img0001.png`, `img0002.png`, …). BDN XML is therefore not an OCR input path.

### Step 22e result — Tesseract cannot be dropped, and the failure mode got worse

*Step 22d result* showed nOCR reaching a byte-exact match and raised the obvious question: can the
Tesseract requirement be removed entirely? It was tested. **No.** Both install-free engines, four
samples, colour isolation both ways, `--fix-common-errors` throughout:

| Sample | Engine | Isolation | Exact cues | CER |
|---|---|---|---|---|
| `solid.sup`, synthesized | nocr | off | **6/6 — 100%** | **0.0%** |
| `solid.sup` | binaryocr | off | 0/6 | 45.7% |
| `complex.sup`, 432 cues | nocr | off | 105/432 — 24.3% | 6.9% |
| `complex.sup` | binaryocr | off | 10/432 — 2.3% | 27.1% |
| `ext.sub`, retail VobSub | nocr | off | 0/3 | 21.6% |
| `outline.sup` | nocr | off | 0/6 | 18.6% |
| *every sample* | *either* | **on** | 0 | 100% |

CER is character error rate against the reference — exact ground truth everywhere except
`complex.sup`, which is scored against Tesseract's own output from *Step 22c result*.

**1. `binaryocr` is not a second chance.** The other in-process engine, pointed at `Ocr/Latin.db`,
is worse than nOCR on every single row. There is no install-free engine behind nOCR.

**2. The one perfect row is the synthesized one.** On the only realistic multi-hundred-cue sample,
nOCR sits at 6.9% CER — roughly one character in fourteen — and on a real retail VobSub pair at
21.6%. Whether a given source clears the bar cannot be known before running it.

**3. `--fix-common-errors` launders the placeholder into a plausible character, and that is a
worse failure than the one the phase already designed against.** In `complex.sup`'s font nOCR
cannot read lowercase `l`. FCE then reads the resulting `*` as a music cue:

```text
tess: There are many guilds in this kingdom,
nocr: There are many gui♪ds in this kingdom,

tess: nay, one that will continue to create legends well into the future.
nocr: nay, one that wi♪♪ continue to create ♪egends we♪♪ into the future.
```

**243 of 432 cues** carry at least one, plus 127 spurious `<i>` tags. Without FCE it is 369
asterisks across 287 of 432 cues — the same damage, still visible.

This retires the mitigation *Step 22 result* proposed. A `*`-density check catches raw nOCR output
and **does not catch this**: `gui♪ds` reads as a correctly-OCR'd musical cue, passes a cue count,
passes any plausible-text heuristic, and looks like a working subtitle until someone watches it.
**Any output validation must run on the raw OCR result, before `--fix-common-errors`, never after.**

**What this settles.** Tesseract stays required. nOCR is viable only as an **opt-in fallback** for
servers without it, and only with raw-output validation ahead of the correction pass. It is not a
replacement: Tesseract read every sample here correctly. Nothing changes for v1, which ships
Tesseract-only.

### Step 25 result — SeConv is pinned, not built

The step as written called for `tools/build-seconv.ps1`. It was not written, and should not be:
**Subtitle Edit publishes prebuilt per-platform SeConv assets on its own releases, and GitHub
serves a `sha256:` digest for each one.** Pinning those gives exactly the trust property a local
build would — a SHA-256 compiled into the assembly that the download must match — with no build
step, no re-hosting, and no per-platform build machine. `tools/pin-seconv.ps1` resolves a release,
records name, hash, size and archive format per RID, and regenerates the manifest.

`-Download` fetches every asset and recomputes each hash locally rather than trusting the digest;
the lock records which was used as `verifiedLocally`, and v5.1.0 is pinned with all four verified.
`-Check` reports when upstream has moved on and exits 2, which the release gate treats as a
failure — that is what makes "a new version should be brought in when it is released" a gate
rather than an intention.

What this cost, in the order it mattered:

| Change | Why it was forced |
| --- | --- |
| `assy-cli.lock.json` → `payload.lock.json`, keyed by tool | One file, two entries; `acquisition` says whether a tool is `built` or `pinned` |
| `PayloadStore` keyed `<tool>/<version>/<rid>` | Pruning superseded versions must not reach into the other tool's directory |
| `PayloadFetcher` takes a `PayloadTool`, single-flight per tool | Both tools can install at once; neither can install twice |
| `.tar.gz` extraction alongside `.zip` | Upstream ships Linux as `.tar.gz`. Same path check for both; tar links are skipped, not resolved |
| `PayloadRuntime` extracted as a base | `AssyRuntime` and `SeConvRuntime` differ only in which tool they bind and, for seconv, Tesseract |

Measured payload: **40.3 MB** (Windows x64), 39.1 MB (Linux x64), 37.2 MB (Linux ARM64), 39.8 MB
(macOS ARM64) — comparable to the `assy-cli` payload and downloaded only when OCR or HI-removal is
actually enabled, so an install that uses neither pays nothing.

---

### Phase 9 spec — strip SDH annotations (`Transform` stage)

**Goal**: produce a clean subtitle from an SDH one by removing non-dialogue annotations —
`[door creaks]`, `(SIGHS)`, speaker labels like `MAN:` — leaving spoken text. A cue of bare `♪`
symbols goes too, but lyrics between notes stay; see *Step 30 result*.

SDH is frequently the only subtitle available for a release. The plugin already rewrites subtitle
files with backups and rollback, so the safety machinery exists.

**Do not write a stripper. SeConv already does this**, and Phase 8 already bundles it:

```sh
seconv Movie.en.srt subrip --remove-text-for-hi --overwrite
```

Sub-options live in a `--settings` JSON under `removeTextForHearingImpaired`, including
`removeTextBeforeColon` (default true — speaker labels) and `removeInterjections` (default false),
selected with `--settings:profile.json --profile:<name>`. The same pass can run
`--remove-formatting`, `--remove-line-breaks`, and `--merge-same-texts` if wanted.

This is the whole reason to check for an existing implementation first. SDH stripping is
convention-matching, not parsing: bracketed text is not always annotation (`[in French]` is
meaningful context), some releases put real dialogue in parentheses, and uppercase speaker labels
collide with legitimately shouted dialogue. Subtitle Edit has been accumulating those special
cases — including per-language interjection lists — for well over a decade, across a user base
that reports the failures. A regex file written here would be worse on day one and would stay
worse, and it would need a hand-checked corpus to have any confidence at all.

**Design**:

- Operate on a copy, never in place.
- Emit the cleaned file with the `sdh` flag dropped from the name, which places it in a different
  naming slot from its source, so the user keeps both and can compare.
- Composable with sync in either order: strip an already-synced subtitle, or sync then strip.

**Phase 9 now hard-depends on Phase 8**, not just benefits from it: the SeConv payload is the
implementation. The ordering argument is unchanged and reinforced — disc subtitles are frequently
SDH, so an OCR'd PGS track is a prime stripping candidate, and OCR errors *inside* annotation
brackets are harmless when the annotation is about to be deleted.

**Settle before building**:

- **Our config surface must mirror SeConv's, not invent its own.** The settings schema exposes
  `removeTextBeforeColon` and `removeInterjections`; offering per-pattern toggles the tool does
  not support means either lying in the UI or reimplementing the parts it does not expose, which
  is how a wrapper turns back into a stripper.
- **Verify the empty-cue behaviour.** Removing an annotation can leave a cue with no text, which
  must be dropped rather than left blank to flash on screen. Subtitle Edit almost certainly
  handles this; confirm rather than assume, since it is invisible until someone watches.
- **Confirm format coverage.** `.srt` is certain. Check what `--remove-text-for-hi` does to
  `.ass`/`.ssa` override tags before offering it for them.
- **Smoke test, not a corpus.** With a third-party implementation the test is "the flag ran and
  the output is well-formed", not "our regex is right". If a specific release trips it, that is an
  upstream bug report, which is a far better position than owning the heuristic.

#### Step 30 result — `--remove-text-for-hi` measured, and what it forces

Run against a crafted eight-cue file covering every annotation style. Two of the three
"settle before building" items above are answered by it.

| Input cue | Output |
|---|---|
| `[door creaks]` | cue deleted entirely, file renumbered |
| `MAN: Get out of here.` | `Get out of here.` |
| `(SIGHS)` + a dialogue line | annotation gone, dialogue kept |
| `- JOHN: Hello?` / `- WOMAN #2: Over here.` | labels gone, dashes kept |
| `>> NARRATOR: In the beginning.` | `In the beginning.` |
| `♪ music playing ♪` | **unchanged** |
| `♪♪` or `♪` alone | cue deleted |
| `He said (and I quote) it was fine.` | **`He said it was fine.`** |
| `Just ordinary dialogue here.` | unchanged |

**Empty-cue behaviour is correct** — a cue reduced to nothing is dropped, not left blank, and the
file is renumbered. That item is settled.

**Music handling is narrower than "removed" or "kept".** `removeIfOnlyMusicSymbols` is already true
by default, and it means what it says: a cue containing *only* `♪` characters is deleted, while a
cue with text between the notes survives. The tool cannot distinguish `♪ music playing ♪` — a
sound description — from `♪ I was born by the river ♪`, which is a lyric and is content a hearing
viewer wants. Both are kept. Removing the first without the second needs semantics no
convention-matcher has.

`dump-settings` prints the whole schema, and the useful knobs for this stage are
`removeTextBetweenBrackets`, `removeTextBetweenParentheses`, `removeTextBeforeColon`,
`removeTextBeforeColonOnlyIfUppercase` (all true by default), and `removeInterjections` (false).
Note `removeTextBeforeColonOnlyIfUppercase` defaults true, which is the same uppercase rule
`SdhDetector` applies — the two agree without being told to.

**The row that dictates the design is the second-to-last one.** The tool strips parenthesised
text from ordinary dialogue. Pointed at a track that is not SDH, it silently deletes real words —
exactly the failure mode the `settle` list predicted, now confirmed rather than assumed. The tool
cannot tell an SDH track from a normal one; **that judgement has to be made before it is invoked**,
which is what `SdhDetector` exists for. Running the flag over everything is not an option.

`SdhDetector` is built and proven by `subcheck`, and deliberately agrees with the tool on what
counts: brackets and uppercase speaker labels yes, music notes no. It requires 5 marked cues and
8% of the file before it will say yes, since a false positive costs the user dialogue while a
false negative costs only an unstripped tag.

**Still open**: the container's `IsHearingImpaired` flag and the content verdict can disagree, and
the naming rule above ("emit with the `sdh` flag dropped") means a track detected as SDH by
content needs that flag *added* to its source name before it can be dropped from the output name.
Settle when the stage is built.

---

### Phase 10 spec — download missing subtitles (`Acquire` stage) — WITHDRAWN

> **Downloading is out of scope. This plugin modifies subtitles that already exist and fetches
> nothing.** Acquisition is handled by the OpenSubtitles plugin for Jellyfin, which already owns
> the credential store, the provider quotas, and the match quality. Adding a second downloader
> would put two things in a race to fill the same gap.
>
> `Services/QuotaLimiter.cs` was written for this phase and has been deleted — a provider rate
> limiter in a plugin that never contacts a provider is dead code that reads like a feature.
> Steps 34–40 are retired. The `Acquire` value stays in `SubtitleStageKind` so the persisted
> enum numbering does not shift under existing records.
>
> The spec below is kept as the record of what was considered and why it is not being built.

**Goal**: when an item has no subtitle in a wanted language, fetch one, then run it through the
normal sync pipeline. Rule-driven, so the user defines "wanted".

The plugin already computes which items have which tracks — detecting *absence* is the same query
inverted, and everything downstream already exists. A downloaded subtitle is almost always timed
against a different release, so it needs syncing immediately, which is this plugin's whole job.

**Configurable rules**: wanted languages in priority order; applicable item types and libraries;
whether an embedded track counts as "already has subtitles"; forced/SDH variants wanted, ignored,
or wanted-in-addition; minimum item age before searching (avoids hammering providers during a big
import); a cap on searches per run; and the upgrade rule — replace SDH or image-based tracks with
clean `.srt` sidecars, never touching muxed subs. **The upgrade rule is blocked on Phases 8 and 9
— see `RM-ORDER`.**

**Settle before building**:

- **Whose downloader?** Jellyfin has `ISubtitleManager` and configured providers already. Reusing
  that stack avoids a second credential store and a second rate limit to respect, at the cost of
  inheriting Jellyfin's provider behaviour including its matching quality. Adding an independent
  downloader is the alternative and it is worse.
- **Overlap with Bazarr.** If both run, two things race to fill the same gap. This needs an
  explicit "don't act if something else manages subtitles" story, not a config note.
- **Provider quotas.** OpenSubtitles limits downloads per day per account; a library sweep burns
  one in minutes. Needs the persisted daily counter from *Throttling*, and a dry run that reports
  how many downloads it *would* perform — see the dry-run invariant in *Security Notes*.
- **A bad match is worse than no subtitle.** A wrong-release subtitle that syncs "successfully"
  looks correct and is not. Record provider match confidence on the `Acquire` stage so the user
  can audit what was pulled.

---

## Implementation Order

### Phase 1 — Skeleton (this commit)
1. `.csproj`, `Plugin.cs`, `build.yaml`, `manifest.json`, `.gitignore`, `LICENSE`
2. `PluginConfiguration.cs` with every setting and its default
3. `PluginServiceRegistrator.cs` wiring
4. Minimal `configPage.html` that loads and saves config

### Phase 2 — Data + CLI bridge
5. `Models/*`, `Data/SyncStore.cs` (mirror `PairStore` exactly), `Data/BackupVault.cs`
6. `Cli/AssyRuntime.cs` — bundled-binary resolution, executable bit, pinned version
7. `Cli/AssyArgumentBuilder.cs` — pure, fully unit-testable argv construction
8. `Cli/AssyCliRunner.cs` — process spawn, NDJSON stdout parse, stderr capture, timeout kill
9. `tools/build-assy.ps1` + a first vendored payload — **first end-to-end proof of life**

### Phase 3 — Discovery
10. `Services/LibraryScopeResolver.cs`
11. `Subtitles/SubtitleNaming.cs` (+ tests — this is where silent bugs hide)
12. `Subtitles/SubtitleDiscoveryService.cs`

### Phase 4 — External subtitle sync (the 80% case)
13. `Services/SyncQueue.cs`
14. `Services/SyncOrchestrator.cs` — external path, fingerprint skip, engine fallback,
    scratch-then-move placement, backups
15. `Tasks/FullLibrarySyncTask.cs` including the prune pass
16. Ship it. External-only is a genuinely useful plugin on its own.

### Phase 5 — Embedded subtitle sync
17. `Subtitles/FfmpegSubtitleExtractor.cs`
18. Extend `SyncOrchestrator` with the extract → sync → place path

### Phase 6 — Events + reversibility
19. `EventHandlers/LibraryEventHandler.cs` with per-item debounce and a bounded queue
20. `POST /RollbackAll` + the config page button

> **Step 20 branches on `SubtitleProvenance` — see *Provenance* under *Data Model*.** This is not
> a roadmap concern: v1 already produces both kinds of output. An external subtitle overwritten in
> place is `Retimed` and rollback **restores its backup**; an extracted embedded track is a file
> the plugin created, with no backup and no original, and rollback **deletes it**. One verb for
> both destroys data in whichever direction it is wrong.

### Phase 7 — Config UI polish
21. `/Status` polling and the summary counts

---

Everything below is **roadmap, not committed**. Read *Roadmap: the staged pipeline* first — and
settle `RM-SCOPE` before starting any of it.

### Phase 8 — OCR image-based subtitles (`Convert`)

22. **DONE — see *Step 22*, *22d* and *22e results*.** nOCR emits `*` per unmatched glyph and exits
    0, so an OCR stage must validate output text rather than exit code — and per *22e*, it must do
    so on the **raw** result, since `--fix-common-errors` rewrites `*` into a plausible character.
    *22d* answers whether nOCR ever succeeds (yes, byte-perfectly, and the outline stroke is what
    breaks it); *22e* answers whether it can replace Tesseract (no).
23. **DONE.** `Models/SubtitleStage.cs`, `StageOutcome`, `SyncRecord.Stages`, `SyncRecord.Provenance`,
    and the `SyncStore` load-time migration for v1 records (+ fixture test with a real v1
    `records.json`, in `tools/storecheck/`).
24. **DONE.** Restructure `SubtitleDiscoveryService` to group candidates by (language, flags) and
    select one source per slot using the ranking in *Sidecar Naming*. Image tracks become
    candidates only when nothing better serves that slot. **This is where the phase's cost is
    decided.**
25. **DONE — see *Step 25 result*.** `assy-cli.lock.json` became `payload.lock.json`, keyed by tool;
    `assy-lock.psm1` became `payload-lock.psm1`; `PayloadStore`, `PayloadFetcher` and the runtimes
    are per-`PayloadTool`. No `build-seconv.ps1` was written — SeConv publishes prebuilt per-platform
    assets with a SHA-256 digest each, so `tools/pin-seconv.ps1` pins those instead of rebuilding
    them.
26. **DONE.** `Cli/SeConvRuntime.cs` and `Cli/SeConvRunner.cs`. No separate argument builder was
    written: the two invocations are fixed argument lists of six items, and a builder abstraction
    over that would have more surface than the thing it wraps.
27. **DONE.** `Subtitles/ImageSubtitleExtractor.cs` — extracts to a **single-track MKV** rather
    than to `.sup`/`.sub`+`.idx`. See *Step 22c result*: a one-track container removes the
    `--track-number` ambiguity entirely, and DVB additionally needs `-c:s dvdsub`.
28. **DONE.** The `Convert` stage runs in `SyncOrchestrator.ConvertAsync` ahead of `Sync`, inside
    the same `SyncQueue` slot, so OCR and sync for one target never run as two units of work.
29. **DONE.** `/Status` returns a `Stages` array — succeeded, skipped, failed and **mean** elapsed
    per `SubtitleStageKind`, in pipeline order. The config page renders every row, `Sync` included.
    The row and the headline cards disagree by design and both are kept — see *`Status` and what it
    deliberately does not report* in `ARCHITECTURE.md` for why, and why the two cannot be summed.

### Phase 9 — Strip SDH annotations (`Transform`)

Depends on Phase 8: the SeConv payload *is* the implementation. No stripper is written here.

30. **DONE — see *Step 30 result*.** Measured against a crafted SDH file. An emptied cue is
    deleted and the file renumbered; bare `♪` cues go too.
31. **DONE, reduced.** `SeConvRunner.RemoveHearingImpairedAsync` passes `--remove-text-for-hi` and
    nothing else. The generated `--settings` profile was dropped: the defaults measured correct on
    every case in *Step 30 result*, and a generated profile is a second source of truth for
    behaviour the plugin does not otherwise control.
32. **DONE.** `SyncOrchestrator.TransformAsync` runs after `Sync` and after the minimum-offset
    check, gated on `SdhDetector` rather than on the setting alone — `--remove-text-for-hi` also
    strips parenthetical asides from ordinary dialogue, so an ungated run corrupts non-SDH tracks.
    On success `target.IsHearingImpaired` is cleared, which drops the `sdh` token from the name.
33. **WITHDRAWN.** A config section for `removeTextBeforeColon` and `removeInterjections` asks the
    admin to tune two knobs whose failure mode is silent damage to their subtitles, with no way to
    preview the result. The single on/off switch stays; the detector decides the rest.

### Phase 10 — Download missing subtitles (`Acquire`) — WITHDRAWN, steps 34–40 retired

34. **Settle the Bazarr overlap story first.** Without an explicit "do not act if something else
    manages subtitles" rule this races another tool over the same files, and no amount of later
    polish fixes that.
35. **DONE.** `Services/QuotaLimiter.cs` — persisted daily counter surviving restart, plus the
    network concurrency gate. Must exist before the first real request is ever made. Limits are
    passed in per call; the config surface arrives with the rule engine in step 38.
36. Extend the dry run to report intended downloads without performing them, and re-verify the
    widened invariant in *Security Notes* across every stage.
37. `Services/SubtitleAcquirer.cs` over Jellyfin's `ISubtitleManager` and configured providers —
    no second credential store.
38. Rule engine: wanted languages in priority order, item types, age threshold, per-run cap,
    forced/SDH handling.
39. Record provider match confidence on the `Acquire` stage.
40. The upgrade rule — replace SDH and image-based tracks with clean `.srt` sidecars — **last**,
    since it composes Phases 8 and 9 (`RM-ORDER`).

---

### Phase 11 — Fetch the payload on first run

Independent of the 8 → 9 → 10 chain, and best done **before the first release** rather than after
(`PD-*` in *Payload delivery*). Steps 41–43 are the security core: none of the rest should be
written until a mismatched hash provably refuses to run.

**All of 41–48 are DONE.** `PayloadManifest.g.cs` is generated from the lock, `PayloadFetcher`
verifies before unpacking and path-checks every entry in both archive formats, `PayloadStore` keys
the cache by tool and version and prunes only after a new payload verifies, readiness is a state,
and the zip is DLL-only with `-ReleaseMode` failing on a pinned hash with no uploaded asset. The
negative cases of step 43 are pinned by `tools/payloadcheck` — corrupted archive, `../` entry in a
zip and in a tar.gz, failed promotion, failed install. What remains is not code: `assy-cli` has
never been built, so its asset list in the manifest is empty.

41. **Generate the pinned manifest into the assembly.** `build-assy.ps1` emits a source file
    carrying the upstream tag, payload version, and per-RID SHA-256 plus asset name, from the same
    lock it already writes. Generated, never hand-maintained — a hand-edited hash is a hash that
    eventually says what someone wished were true. It must not reference the lock file by path;
    the generated source has to stand alone at runtime.
42. `Cli/PayloadFetcher.cs` — HTTPS to a compiled-in host, download to a temp file, **verify the
    SHA-256 before unpacking anything**, delete on mismatch, extract with every entry's resolved
    path checked to stay inside the target directory, restore the executable bit on Unix, then
    promote atomically. Retry with backoff; single-flight so two triggers cannot both download.
43. **Prove the negative case.** A deliberately corrupted archive must be refused, deleted, and
    reported — and a zip with a `../` entry must be rejected. Verify these before building
    anything on top; they are the whole reason the feature is safe.
44. `Cli/PayloadStore.cs` — the versioned cache under `PluginConfigurationsPath` (`PD-CACHE`),
    resolution by RID, and pruning of superseded versions **only after** the new one verifies.
45. **Readiness as a state** (`PD-STATE`). `AssyRuntime` reports Ready / Fetching / Unavailable
    with a reason; `FullLibrarySyncTask` checks once per run and aborts with one log line rather
    than writing a `Failed` record per subtitle.
46. ~~The offline drop-in directory.~~ Withdrawn with `PD-OFFLINE`; there is no offline install
    path to support.
47. ~~Config page: payload status and a Download now button.~~ Withdrawn (`PD-SILENT`). The
    payload comes from the plugin's own release, so installing the plugin is the consent. The
    fetch is a startup background task that logs its start, quartile progress, and outcome.
48. **Release process changes.** Payloads become separate release assets and the plugin zip goes
    back to DLL-only. `verify.ps1 -ReleaseMode` must fail if any pinned hash has no matching
    uploaded asset — the failure mode this replaces (shipping a manifest pointing at nothing) is
    worse than the one it removes.

---

## Verification

**Unit** — `AssyArgumentBuilder` (every flag combination), `SubtitleNaming` (flags, collisions,
already-ours detection), `SubtitleOffsetProbe` (SRT/VTT/ASS timestamp forms), `SyncStore`
(concurrent upsert, corrupt-file recovery), `LibraryScopeResolver.IsUnder` (the `/media/movies`
vs `/media/movies-4k` boundary), `PluginConfiguration.AutoConcurrencyFor` across core counts.

**Rollback** — one test per `SubtitleProvenance` value asserting the *verb*: `Retimed` restores
its backup and leaves no plugin file behind; every other value deletes its output and restores
nothing. Include the case with no backup on disk. This is the only place in the plugin where a
wrong branch destroys user data, and it is unobservable until someone actually rolls back.

**Store migration** — a fixture `records.json` written by v1 (no `Stages`, no `Provenance`) loads
with a synthesized `Sync` stage and correctly inferred provenance. Runs once per user, silently.

**CLI contract** — fixture-driven parse tests against real captured `assy-cli --json` output for
success, `ok: false`, exit 2, and NDJSON batch. **Re-capture whenever the bundled version is
bumped**; this is the interface most likely to drift, and bundling means drift only ever happens
at a moment we control.

**Bundled payload** — on each supported platform: the correct RID folder is selected from a zip
containing all of them, the executable bit survives the zip round-trip, and the child picks up
Jellyfin's ffmpeg from the `PATH` we hand it. The ffmpeg check should be run with the network
disabled, so a `static_ffmpeg` download attempt fails loudly instead of silently succeeding and
masking a packaging mistake.

**Fetched payload (Phase 11)** — the failure cases carry the weight here, not the happy path:
a corrupted archive is refused, deleted, and reported; an archive containing a `../` entry is
rejected without writing outside the target; a payload already present and verified is not
re-downloaded; a plugin version bump fetches the newly pinned version and prunes the old one only
after the new one verifies; a fetch interrupted mid-download leaves nothing promoted. Then the
airgapped path: the drop-in directory is accepted with the network disabled, and rejected with the
same message as a bad download when its hash does not match.

**Integration** — a test Jellyfin instance with a deliberately desynced sidecar:
- Sidecar shifted +8s → run task → subtitle plays in sync, backup exists
- Rollback → original bytes restored exactly
- Re-run → target skipped via fingerprint, no second sync
- Replace the video with a different release, keep the sidecar → target is *not* skipped
- Embedded-only MKV → sidecar appears with correct language/forced flags and Jellyfin lists it
- PGS-only item → recorded `Unsupported`, no ffmpeg invocation
- Kill the server mid-sync → no partial file in the media folder, scratch file cleaned up
- Dry run on → records created, zero filesystem writes (verify with a filesystem watcher)

---

## Resolved Questions

**Is `--suffix` enough, or must we always use `-o`?** Always `-o`, with an absolute scratch path.
Jellyfin's sidecar naming is stricter than anything `assy-cli`'s save modes produce, and a scratch
path is also what keeps a crashed run from leaving a partial file in the media folder.

**Can we use `assy-cli`'s own `processed_items` DB instead of `SyncStore`?** No — the granularity
is wrong, and reading upstream's `processed_items_manager.py` makes that concrete. The table is
`(file_hash, file_size, original_filename, processed_at)` keyed on a hash of the **video**, one
row per video. It cannot express "the English sidecar for this film is synced but the Spanish one
isn't". Relying on it would mean the first synced track marks the whole video processed and every
other track on it is skipped forever — including a subtitle downloaded next week.

What *is* worth taking from it is the fingerprinting strategy: `size + first 64KB + last 64KB`
rather than a full read. That is now how `SyncRecord` fingerprints the video (see *The
idempotency guard*).

**Should `batch` ever be used?** No, and the answer above is most of why: `batch`'s main
advantages are its skip-processed DB (unusable here, and `--no-skip-processed` would be needed to
stop it interfering) and amortized interpreter startup. One `sync` per target is simpler to
attribute, throttle, and cancel. Startup cost for a frozen bundle is a few seconds against a sync
measured in minutes — under 2% overhead, and paid once per target ever thanks to fingerprinting.

**Detecting "already in sync" without doing the work.** There is no cheap pre-check that isn't
just a worse reimplementation of the alignment itself. Deciding whether a subtitle matches the
audio *is* the expensive part; sampling VAD at a few points would be both less accurate than
ffsubsync and still require decoding audio. `MinimumOffsetMs` only helps after the fact, and it
stays — its job is preventing no-op files, not saving time.

The real answer is to reframe the problem: **don't detect it, just never pay twice.** The
fingerprint guard means the expensive pass happens exactly once per (subtitle, video) pair for as
long as neither changes. The first full scan of a large library is genuinely expensive and that
should be communicated rather than engineered around — dry run first so the user sees the scale,
concurrency 1 by default, and a nightly trigger so it runs while nobody is watching. Every scan
after the first is O(new or changed subtitles), which is the shape that actually matters.

---

## Packaging Decisions

**One release artifact carries every platform.** The zip ships `assy-cli/linux-x64/` and
`assy-cli/win-x64/` side by side, and `AssyRuntime` picks the right one at startup from
`RuntimeInformation.ProcessArchitecture` plus the OS. There is no per-platform release, no
platform-specific manifest entry, and nothing for the user to choose.

The cost is an unusually large plugin zip, and it is paid by every user for every platform: a
linux-x64 server downloads the Windows payload too, on install and on every update. Splitting
releases does not fix it — Jellyfin's manifest has no per-platform axis, so it would mean separate
repository entries and a user who can pick wrong. Arm64 payloads can be added the same way if
there is demand; `AssyRuntime` already resolves `linux-arm64` and `osx-arm64`.

**This section is superseded before it ever ships.** Phase 11 replaces it with a fetched payload,
and that is a prerequisite for the first release rather than a later improvement (`PD-DECIDED`).
What survives from it is the RID resolution and the two freeze constraints below, which are
properties of the payload itself and hold however it arrives.

**The payload must be a frozen build, not a virtualenv, and must not bundle ffmpeg.** Both
constraints fall out of the same few lines in upstream's `constants.py`:

```python
NEEDS_STATIC_FFMPEG = (
    not getattr(sys, "frozen", False)
    and not (bundled ffmpeg and ffprobe both present)
)
```

and `cli.py:_ensure_ffmpeg()`, which prepends its own `FFMPEG_DIR` if one exists, otherwise
downloads binaries via `static_ffmpeg` when `NEEDS_STATIC_FFMPEG` is true, and otherwise returns
without touching `PATH`.

So:

- **A frozen build sets `sys.frozen`**, which makes `NEEDS_STATIC_FFMPEG` false. `_ensure_ffmpeg()`
  returns early, `FFMPEG_EXECUTABLE` stays the bare string `"ffmpeg"`, and it resolves against the
  `PATH` the plugin hands the child — which is Jellyfin's own ffmpeg. This is exactly what we want,
  and it is a property of the freeze, not something to hope for.
- **A virtualenv does not set `sys.frozen`.** `NEEDS_STATIC_FFMPEG` would be true and the first
  sync would try to download its own ffmpeg over the network. A venv also still requires a Python
  interpreter on the host, which is the entire problem bundling exists to solve, and is not
  reliably relocatable (`pyvenv.cfg` and script shebangs carry absolute interpreter paths). If a
  venv-shaped approach is ever wanted, the viable form is a `uv` / `python-build-standalone`
  self-contained interpreter, not `python -m venv` — but freezing is simpler and already gives the
  `sys.frozen` behaviour we depend on.
- **The freeze must not include ffmpeg/ffprobe in its resources directory.** If it does,
  `FFMPEG_DIR` is set and `_ensure_ffmpeg()` prepends it, silently overriding Jellyfin's build.
  Excluding them is a build-script requirement, and worth an assertion in the script rather than a
  note in a document.

Use PyInstaller **onedir**, not onefile: onefile unpacks the whole bundle to a temp directory on
every invocation, and this plugin invokes the CLI once per subtitle.

**Size expectation.** Upstream's frozen GUI builds are 90–154 MB per platform; a CLI-only freeze
with the Qt GUI modules excluded should come in well under that, though numpy and scipy dominate
either way. Two platforms in one zip is therefore a large but not absurd artifact. This is a
number to measure once the build script exists, not a design question — if it turns out
unworkable in practice it changes packaging logistics, nothing else.

### Payload delivery — fetch on first run (Phase 11)

Bundling every platform in the plugin zip makes each user pay for all of them, twice over: once at
install and again at every update, for payloads their architecture can never execute. Fetching the
payload the server actually needs removes that without surrendering anything bundling was chosen
to protect.

**PD-PIN — this is not the version drift bundling exists to prevent.** Drift comes from resolving
a *floating* reference at runtime: "latest", or whatever the admin happens to have installed. This
resolves nothing. The plugin assembly carries the pinned upstream tag and a SHA-256 per RID,
generated at build time from the same lock that `check-payload.ps1` already enforces. A payload
whose hash does not match is refused and deleted, never executed. The plugin can therefore only
ever run the exact build its own version was tested against — identical to bundling. What changes
is **when the bytes arrive, not which bytes**.

**PD-TRUST — the trust root does not move.** The expected hash ships inside the DLL, which came
from the release the user already chose to install. Verifying a download against it is the same
trust decision, made at a different moment. Three rules make that true rather than merely
plausible, and none is optional:

- **Verify before extracting, never after.** The hash is checked against the downloaded archive on
  disk before a single entry is unpacked, and the archive is deleted on mismatch.
- **No user-supplied URL.** The host and asset naming are compiled in. A configurable download
  location turns an elevated endpoint into an arbitrary-download-and-execute primitive, which is
  the one thing this feature could plausibly become if built carelessly.
- **Extraction is path-checked.** Every entry's resolved destination must stay inside the target
  directory. A zip carrying `../../` entries is otherwise a write primitive, and the archive is
  attacker-controlled in exactly the scenario where the hash check has already failed to save us.

**PD-OFFLINE — withdrawn. There is no offline install path.** An earlier version of this section
argued for a drop-in directory so an airgapped server could install the payload by hand. The
premise does not hold: installing a Jellyfin plugin means reaching a repository manifest and
downloading a plugin zip, so a server that cannot reach the internet never acquires the plugin in
the first place. The only case the drop-in served was a hand-sideloaded DLL, which is not a
supported install path and does not justify a second verification entry point.

A server with internet access at install time and none afterwards is the same situation as any
plugin whose first run needs the network, and is not designed around.

**PD-SILENT — the fetch needs no user interface.** The payload is an asset of this plugin's own
release, pinned by a hash inside the assembly the user chose to install. Installing the plugin is
the consent; a confirmation prompt would ask the user to approve a decision they already made,
and a settings toggle to decline it would produce a plugin that cannot do the one thing it exists
to do. The fetch therefore runs as a startup background task with no configuration and no UI, and
reports itself in the server log: one line when it starts with the size, a line per quartile, and
one line for the outcome. Readiness is still a state (`PD-STATE`) — that is what keeps a
not-yet-fetched payload from being reported once per subtitle.

**PD-CACHE — the payload cannot live in the plugin directory.** Jellyfin replaces that directory
on update, so a payload written there is destroyed by the next version bump and re-downloaded for
no reason. It lives under `PluginConfigurationsPath`, keyed by payload version:

```text
<PluginConfigurationsPath>/AutoSubSync/payloads/<version>/<rid>/
```

Keying by version is what makes a plugin update do the right thing automatically: the new assembly
pins a new version, finds no payload under it, and fetches. Superseded version directories are
pruned once the new one verifies — not before, so a failed fetch leaves the working payload in
place and the plugin keeps running on it.

**PD-STATE — readiness is a state, not an error per item.** `AssyRuntime` gains an explicit
readiness state, and the scheduled task checks it **once per run** and aborts with a single log
line. Without that rule a library sweep with no payload produces one `Failed` record per subtitle
— thousands of rows describing one problem, which buries every real failure and makes the store
worthless exactly when a user is trying to work out what went wrong. This is the same shape as the
provider-quota rule in *One limiter is not enough past v1*: stop requesting, do not fail each item.

**Phase 11 is independent of the 8 → 9 → 10 chain** and can be built at any point. It should
nonetheless land **before the first release**, not after: changing delivery once users have
installed means writing a migration for payloads already on disk, and the whole point is to avoid
shipping the every-platform zip even once.

**PD-DECIDED — both open decisions are settled.** Payloads are hosted as **GitHub release assets
on the plugin's own repository**, alongside the plugin zip that already ships there, and **Phase 11
lands before the first release**. Two consequences follow and are not optional:

- The every-platform zip described at the top of this section **never ships**. It is the design of
  record only until Phase 11 replaces it, and no release is cut against it.
- The payload never enters git, so the repository does not need git-LFS. Payloads are built,
  hashed into the assembly, and uploaded as release assets — the working tree only ever holds the
  lock file that pins them.

### The child process environment

The child gets an **allowlisted environment**, not the server's. `ProcessStartInfo.Environment` is
cleared and repopulated with only what a frozen CPython actually needs — `HOME`, `TMPDIR`/`TEMP`,
the `LANG`/`LC_*` locale variables, the Windows essentials (`SystemRoot`, `COMSPEC`, `PATHEXT`,
`windir`), and a `PATH` with Jellyfin's ffmpeg directory prepended. Anything else in the Jellyfin
server's environment — API tokens, database credentials, whatever the admin exported — is simply
not visible to the subprocess.

A virtualenv does **not** help here and is worth being explicit about, because it looks like it
should. A venv only manipulates `PATH`, `VIRTUAL_ENV`, and `sys.prefix`; it provides no
environment-variable isolation whatsoever, and a process launched from inside one still inherits
every variable its parent had. Process-level isolation is the only thing that solves this, and it
is available regardless of how the payload is built. (The separate reasons a venv is unsuitable as
the *packaging* format — `sys.frozen` and the need for a host interpreter — are above.)

The allowlist is deliberately conservative, so the failure mode is a payload that complains about
a missing variable at build-verification time rather than one that silently leaks the server's
environment forever. Extend it if the built payload turns out to need something.

---

## Open Questions

Nothing blocks v1. Three open items:

**v1 — a measurement, not a design question.** The actual size of the built payload (see
*Packaging Decisions*), which changes packaging logistics at most.

**v1 — Phase 11 is a prerequisite for the first release, and that is decided** (`PD-DECIDED`).
Payloads are hosted as GitHub release assets on the plugin's own repository. The every-platform
zip never ships, and the payload never enters git, so there is no git-LFS question to answer.

**Roadmap — `RM-SCOPE` is a genuine decision and it is the user's to make.** Whether this plugin
should manufacture subtitles at all, or stay a synchronizer. Phases 8–10 are specified so the
choice can be made against real costs, and none of them should start until it is. The measurement
that most informs it is already available: the `Unsupported` count on the config page says how
many tracks Phase 8 would rescue in this specific library.
