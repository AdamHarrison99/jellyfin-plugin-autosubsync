# Jellyfin Plugin: AutoSubSync

A Jellyfin plugin that automatically synchronizes out-of-sync subtitles across your Jellyfin library, using a pinned
build of [AutoSubSync](https://github.com/denizsafak/AutoSubSync)'s headless CLI.

## What it does

Realigns subtitles against the audio of the media they belong to.

- **Syncs external subtitle files** — in place with a backup, or alongside the original.
- **Syncs embedded subtitle tracks** — extracted and written back as a subtitle file.
- **Removes duplicate external subtitles** — if multiple subtitle files have the same content and formatting, they are backed up and removed.
- **Converts image-based subtitles to text** — PGS, VobSub, and DVB bitmaps, via OCR.
- **Removes hearing-impaired tags** — `[door creaks]`, `(SIGHS)`, and speaker labels.

Runs nightly, on newly added items, or on demand, and remembers what it has done so the expensive
first pass happens once.

## What it does not do

- **Downloads new subtitles for your media** — if you want missing subtitles fetched, use a plugin built for that, such as the [Jellyfin OpenSubtitles Plugin](https://github.com/jellyfin/jellyfin-plugin-opensubtitles).

- **Integrates with the \*arr stack** — this plugin is an intentional, self contained alternative to Bazarr, along with the other \*arr internet PVR applications.

## Requirements

Jellyfin 10.11.0 or later, on 64-bit Windows, Linux, or macOS. On
startup, after installing or updating, the plugin downloads the sync engine for your platform, a
few hundred megabytes, once. It is fetched from this plugin's own releases and checked against a
checksum built into the plugin, and the server log records it.

**If you turn on "Convert image-based subtitles to text" or "Remove hearing-impaired text":** the
plugin downloads a second tool, the [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit)
converter, about 40 MB — from Subtitle Edit's own releases, against a checksum built into the plugin. Leave both settings off and it is never downloaded.

**Only if you turn on "Convert image-based subtitles to text":** OCR additionally needs
[Tesseract](https://github.com/tesseract-ocr/tesseract) installed on the server, along with the
language data for the subtitles you want read. The plugin looks for it on your `PATH`, then
in `C:\Program Files\Tesseract-OCR`, `/usr/bin`, `/usr/local/bin`, `/snap/bin`, and
`/opt/homebrew/bin`. If your language data lives somewhere non-standard, set `TESSDATA_PREFIX` in
the environment Jellyfin runs under and it is passed through. See [Tesseract's installation
instructions](https://tesseract-ocr.github.io/tessdoc/Installation.html) for your platform.

**In Docker,** you'll need an init hook for tesseract. `apt-get install -y tesseract-ocr tesseract-ocr-eng`.

Without Tesseract, image tracks are
reported as unsupported and everything else keeps working normally.

## Installation

1. In **Dashboard → Plugins → Repositories**, add a repository with this URL:

   ```text
   https://raw.githubusercontent.com/AdamHarrison99/jellyfin-plugin-autosubsync/master/manifest.json
   ```

2. Install **AutoSubSync** from the catalogue and restart Jellyfin.
3. Open the plugin settings and **select the libraries to process**.

Source and releases: [github.com/AdamHarrison99/jellyfin-plugin-autosubsync](https://github.com/AdamHarrison99/jellyfin-plugin-autosubsync).

## Configuration

**Nothing happens on a fresh install.** No libraries are selected and dry run is on, so you have
to change both. Run the "Full Library Sync" task once with dry run left on and read the log to see
what it would have done.

### Safety

| Setting | Default | What it does |
| --- | --- | --- |
| Dry run mode | On | Discovers and logs everything, changes nothing. The plugin records what it found, and what it would have done, so you can inspect the results before committing to it. |

### Scope

| Setting | Default | What it does |
| --- | --- | --- |
| Libraries | *none* | Which libraries to process. Empty means nothing runs — this is opt-in. |
| Process external subtitle files | On | Sidecar files sitting next to your media. |
| Process embedded subtitle tracks | Off | Tracks inside the video container. They are extracted, synced, and written out as a separate file; the video is never modified. Jellyfin will then list **both** the original embedded track and the synced file — a plugin cannot hide an embedded track. Every matching track is processed. |
| Skip embedded tracks when an external of the same language exists | Off | Keeps the list short when a sidecar is already there and synced. |
| Convert image-based subtitles to text | Off | PGS, VobSub, and DVB tracks are pictures of text, which no alignment engine can read. OCR converts them to a text subtitle, which is then synced — and stripped, if that is on — exactly like any other. Slow, and never perfect. The original bitmap track is never modified or replaced. Needs Tesseract on the server; see Requirements. |
| Remove hearing-impaired tags | Off | Strips `[door creaks]`, `(SIGHS)`, and speaker labels from the subtitles the plugin writes, and drops cues that were nothing but a tag. Applied only to tracks that actually look hearing-impaired, so ordinary dialogue is left alone. A track that is stripped loses `sdh` from its filename. |
| Remove duplicate external subtitles | Off | Once everything for an item has synced, subtitle **files** of the same language and flags that hold at least 85% the same text **and** the same styling are collapsed to one. Only external files are ever removed; a track inside the video is never touched. Every removed file is copied to the backup vault first and comes back with **Roll back everything**. Different formats are never merged — a `.srt` and an `.ass` are left alone, as are two `.ass` files whose styles differ. |
| Languages | *all* | Comma-separated language codes, e.g. `eng, spa` or `en, es`. Two- and three-letter forms both work and can be mixed, as can the `ger`/`deu` style variants containers disagree about. Empty processes every language. Unlabelled and unrecognized tracks are always processed. |

### Output

| Setting | Default | What it does |
| --- | --- | --- |
| Where to write synced external subtitles | Overwrite the original | Overwrite replaces the file in place, keeping its name, and always backs the original up first. Side-by-side leaves the original alone and writes a new marked file. Embedded tracks always become new files regardless. |

### Throttling

| Setting | Default | What it does |
| --- | --- | --- |
| Concurrent syncs | `0` | How many subtitles are synced at once, both during the nightly full scan and as new items arrive. Setting to 0 automatically starts at one, measures how much work actually completes, and adds more only while that helps, never taking more than half of your cores. On network storage a lower number is usually faster, not slower. |

### Automation

| Setting | Default | What it does |
| --- | --- | --- |
| Synchronize new items as they are added | Off | Also covers updates, so a subtitle downloaded by Jellyfin is picked up without waiting for the nightly scan. |
| Refresh the item after writing a subtitle | Off | Tells Jellyfin to re-index the item so the new subtitle appears without a manual library scan. |

Works seamlessly with the [Jellyfin OpenSubtitles Plugin](https://github.com/jellyfin/jellyfin-plugin-opensubtitles), picking up auto downloaded subs and syncing them to your media. Make sure you have "Only download subtitles that are a perfect match for video files" unchecked in your library's settings to take advantage of auto subtitle syncing.

Files the plugin writes carry an `autosubsync` marker in their filename, which is how it
recognizes its own output and how rollback knows what it may delete.

### Changing a setting later

**Settings apply to what has already been processed.** A subtitle is normally synced once and then
left alone until it or its video changes — but changing a setting that would have produced a
different result puts the affected subtitles back in the queue on the next run. Turning off dry run,
turning on hearing-impaired removal or OCR, or changing the write mode, output encoding or marker
puts the affected subtitles back in the queue. Concurrency and the timeout change nothing about the output, so they reprocess
nothing.

## The audio check

Every subtitle is scored against the video's own audio, twice, and this is not something you
configure.

**Before syncing**, the plugin reads a sample of the audio and asks whether the subtitle's lines
already land on the speech. If they do, the sync engine is never run and the file is left exactly
as it is. That is faster than syncing it, and it removes the only way this plugin can make a
correct subtitle worse.

**After syncing**, the result is scored the same way. A sync that latched onto the wrong audio is
thrown away and your original is left alone, and the item is listed under **refused by audio
check** on the status panel rather than as a failure.

A rate error — a subtitle that is right at the start and minutes out by the end, usually a
framerate mismatch — is caught by fitting the first half of the film against the second and
comparing the two.

Some titles cannot be measured: an action film with a continuous score may never produce a clear
answer. When that happens the subtitle is synced and the result is kept, exactly as before. The
check only ever refuses a result it can positively show is wrong. It adds a few seconds per
subtitle, against a sync that takes minutes.

## Undoing everything

Overwritten subtitles are backed up in the plugin's data folder, not beside your media.
**Settings → Danger zone → Roll back everything** restores them and deletes the files the plugin
created. **Clear database** discards the index of what was backed up, so rollback can no longer
find them. However, they will remain on disk and can be manually restored as a last resort.

## Credits

Subtitle alignment is done entirely by [AutoSubSync](https://github.com/denizsafak/AutoSubSync) by
Deniz Şafak, and by the engine it wraps that this plugin uses,
[ffsubsync](https://github.com/smacke/ffsubsync) by Stephen Macke.

Reading image-based subtitles and removing hearing-impaired text are done by
[Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) by Nikolaj Olsson, via its headless
`seconv` converter. Decades of OCR post-correction live in that project, and this plugin would not
attempt image subtitles without it.

The OCR itself is [Tesseract](https://github.com/tesseract-ocr/tesseract), maintained by the
Tesseract OCR community and originally developed at HP and Google.

This plugin decides what to sync, when, and where the result goes.

---

*This project was built utilizing AI code development tools ([Claude Code](https://www.anthropic.com/claude-code)).*