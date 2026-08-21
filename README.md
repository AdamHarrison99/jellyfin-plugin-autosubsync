# Jellyfin Plugin: AutoSubSync

Finds out-of-sync subtitles across your library and realigns them against the audio of the media
they belong to, using a pinned build of
[AutoSubSync](https://github.com/denizsafak/AutoSubSync)'s headless CLI.

## What it does

- **Syncs external subtitle files** — in place with a backup, or alongside the original.
- **Syncs embedded subtitle tracks** — extracted and written back as a subtitle file.
- **Removes duplicate external subtitles** — same content and formatting, backed up first.
- **Converts image-based subtitles to text** — PGS, VobSub, and DVB bitmaps, via OCR.
- **Removes hearing-impaired tags** — `[door creaks]`, `(SIGHS)`, and speaker labels.
- **Downloads subtitles for a language you have none in** — through your own provider plugins, and
  only kept when the audio check confirms it.

Runs nightly, on newly added items, or on demand, and remembers what it has done so the expensive
first pass happens once.

It does not talk to the \*arr stack; it is a deliberately self-contained alternative to Bazarr.

## Requirements

Jellyfin 10.11.0 or later, on 64-bit Windows, Linux, or macOS.

The sync engine, a few hundred megabytes, downloads once after installing or updating — from this
plugin's own releases, against a checksum built into the plugin. Turning on OCR or hearing-impaired
removal adds a second download, [Subtitle
Edit](https://github.com/SubtitleEdit/subtitleedit)'s converter, about 40 MB.

OCR also needs [Tesseract](https://github.com/tesseract-ocr/tesseract) on the server, with language
data for what you want read. The plugin looks on your `PATH` and in the usual install locations;
set `TESSDATA_PREFIX` if your language data lives elsewhere. In Docker, install it from an init
hook: `apt-get install -y tesseract-ocr tesseract-ocr-eng`. Without Tesseract, image tracks are
reported as unsupported and everything else works normally.

## Installation

1. In **Dashboard → Plugins → Repositories**, add a repository with this URL:

   ```text
   https://raw.githubusercontent.com/AdamHarrison99/jellyfin-plugin-autosubsync/master/manifest.json
   ```

2. Install **AutoSubSync** from the catalogue and restart Jellyfin.
3. Open the plugin settings and **select the libraries to process**.

## Getting started

**Nothing happens on a fresh install** — no libraries are selected and dry run is on. Pick your
libraries, run the "Full Library Sync" task with dry run still on, and read the results before
turning it off.

| Setting | Default | |
| --- | --- | --- |
| Dry run mode | On | Finds and records everything, changes nothing. |
| Only sync when the audio checks are conclusive | On | Leaves a subtitle alone when neither the audio check nor voice detection can decide. Off, the sync engine's own score decides. |
| Libraries | *none* | Nothing runs until you pick some. |
| Process external subtitle files | On | Sidecar files next to your media. |
| Process embedded subtitle tracks | Off | Extracted to a new file. The video is never modified, so Jellyfin lists both. |
| Process embedded tracks when an external of the same language exists | Off | Off keeps the list short. On also syncs "Signs &amp; Songs" tracks. |
| Convert image-based subtitles to text | Off | OCR for PGS, VobSub, and DVB. Slow, never perfect, needs Tesseract. |
| Run the OCR when a text subtitle of the same language exists | Off | Off skips minutes of OCR per track a sidecar already covers. |
| Remove hearing-impaired tags | Off | Strips `[door creaks]`, `(SIGHS)`, and speaker labels from what is written. |
| Remove duplicate external subtitles | Off | Collapses external files holding the same text and styling. Backed up first. |
| Languages | *all* | e.g. `eng, spa` or `en, es`. Two- and three-letter forms mix freely. |
| Download missing subtitles | Off | Only where a wanted language has nothing at all. Needs a provider plugin installed; **every candidate tried spends your provider allowance, including rejected ones**. |
| Maximum downloads per item | `3` | How many candidates one item may download before giving up on that language. `0` is unlimited, not off. Counts over the retry window below. |
| Wait before trying an item again | `7` days | How long an item that came away with nothing is left alone before the providers are asked again. Files it has already downloaded are never downloaded twice. `0` is no wait, max `365`. |
| Download even when the video has an embedded track | Off | On aims the feature at far more of your library — most items without a sidecar do have an embedded track. |
| Accept hearing-impaired subtitles | Off | On makes them acceptable, never preferred. Worth turning on if *Remove hearing-impaired tags* is on. |
| Only save downloads when the audio checks are conclusive | On | On keeps downloading candidates until the check reaches a good verdict, up to the download limit. Off stops the item at the first result the check doesn't outright reject. An item that never reaches a verdict is listed under *rejected as inconclusive*. No effect unless *Only sync when the audio checks are conclusive* is on. |
| Additional download providers | *none* | Both a priority order and the way to tell the plugin an unrecognised provider can download. |
| Where to write synced external subtitles | Overwrite | Replaces the file in place, always backing it up first. Side-by-side writes a new marked file. |
| Concurrent syncs | `0` | Automatic: adds workers only while they help, never past half your cores. |
| Synchronize new items as they are added | Off | Picks up what Jellyfin downloads without waiting for the nightly scan. |
| Refresh the item after writing a subtitle | Off | Re-indexes it so the subtitle appears without a manual scan. |

A subtitle is synced once and then left alone — but changing a setting that would have produced a
different result puts the affected subtitles back in the queue. **Retry failed subtitles** does the
same for anything that failed or was rejected by the audio check, releases any item waiting out its
retry window, and starts a full library sync.

Files the plugin writes alongside your originals carry an autosubsync marker in their name. Overwritten files keep their original name — the backup vault is what makes those reversible.

## The audio check

Every subtitle is scored against the video's own audio, before and after syncing. It costs a few
seconds against a sync that takes minutes.

Some titles cannot be measured at all — a film under a continuous score may never give a clear
answer. Those are left alone by default; all that remains to judge them is the sync engine's own
opinion, which is sometimes confidently wrong. Untick **Only sync when the audio checks are
conclusive** to let it decide instead.

## Undoing everything

Overwritten and removed subtitles are backed up in the plugin's data folder, not beside your media.
**Settings → Danger zone → Roll back everything** restores them and deletes what the plugin
created. **Clear database** discards the index of those backups — the files stay on disk, but
rollback can no longer find them.

Subtitles the plugin downloaded are files it created, so rollback deletes them; replacing one costs
another download from your provider account.

## Credits

Subtitle alignment is done entirely by [AutoSubSync](https://github.com/denizsafak/AutoSubSync) by
Deniz Şafak, and by the engine it wraps, [ffsubsync](https://github.com/smacke/ffsubsync) by
Stephen Macke.

Image-based subtitles and hearing-impaired text are handled by [Subtitle
Edit](https://github.com/SubtitleEdit/subtitleedit) by Nikolaj Olsson, via its headless `seconv`
converter. The OCR itself is [Tesseract](https://github.com/tesseract-ocr/tesseract), maintained by
the Tesseract OCR community and originally developed at HP and Google.

This plugin decides what to sync, when, and where the result goes.

---

*This project was built utilizing AI code development tools ([Claude Code](https://www.anthropic.com/claude-code)).*

*The `agentic/` folder holds the design document, architecture notes, audit history and test harnesses used to build and verify the plugin; nothing in it ships in the plugin itself.*
