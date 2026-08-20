// Per-title alignment measured against the VIDEO'S OWN AUDIO — the only reference this project
// accepts (`agentic/CLAUDE.md`, invariant 1). Replaces `truth.mjs` for judging a verdict.
//
//   node agentic/tools/vadcheck/audio-truth.mjs --video <mkv> --subtitle <srt> [--json <out>]
//        [--min-silence 500] [--isolation 1000] [--search 1500] [--ratio 3]
//        [--detector silence|webrtc] [--python <exe>] [--cache <dir>] [--ffmpeg <exe>]
//
// ! --detector exists to separate the two things that both push the gap POSITIVE: the subtitle's
//   authored lead, and `silencedetect` reporting speech late ∵ it fires on a -30 dB level crossing,
//   which on a soft attack is well after the word begins. webrtcvad is trained on speech rather than
//   level → if its onsets sit systematically earlier, that difference is OUR INSTRUMENT, ¬authoring,
//   and it comes off the floor that bounds `AlignedWithinMs`.
//
// ! WHY THIS EXISTS. `truth.mjs` wraps `check-vs-embedded.ps1`, which compares the sidecar against
//   the video's embedded track. On this library **every** embedded track sampled was `dvd_subtitle`
//   — a DVD VobSub bitmap, matched by timestamp proximity ∵ it carries no readable text. Those
//   timings come from a different master. On MPFC S03E13 that harness reported the sidecar "OUT by
//   -505 ms" while the sidecar's first cue sits **25 ms** from the real speech onset: it was
//   reporting the EMBEDDED track's error and attributing it to the sidecar. Every verdict `truth.mjs`
//   produced on this library is unsafe → ¬use it to judge a check.
//
// HOW THIS IS DIFFERENT. It does not sweep and it does not aggregate over every cue. It pairs a
// small set of **unambiguous** cues — ones that follow real silence, sit alone, and have exactly one
// candidate onset — and reports the median gap. A wrong pairing is excluded rather than averaged in.
//
// ! HONEST LIMIT. Onsets still come from `silencedetect`, so this is ¬independent of that detector.
//   It IS independent of the sweep, the gates, and the 250 ms bucket tolerance — which is what is
//   under test when judging a verdict. Do not present it as detector-independent ground truth.

import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const video = value('--video');
const subtitle = value('--subtitle');
const jsonOut = value('--json');
const minSilenceMs = Number(value('--min-silence', '500'));
const isolationMs = Number(value('--isolation', '1000'));
const searchMs = Number(value('--search', '1500'));
const ratio = Number(value('--ratio', '3'));
const detector = value('--detector', 'silence');
const python = value('--python');
const ffmpeg = value('--ffmpeg') ?? path.join(here, '..', 'ffmpeg', 'ffmpeg.exe');
const cacheDir = value('--cache') ?? path.join(os.tmpdir(), 'audio-truth');

if (!video || !subtitle) {
  console.error('--video and --subtitle are required');
  process.exit(2);
}

fs.mkdirSync(cacheDir, { recursive: true });

// ---- cue starts -------------------------------------------------------------------------------

function cueStarts(file) {
  const text = fs.readFileSync(file, 'utf8');
  const out = [];
  for (const m of text.matchAll(/(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->/g)) {
    const frac = m[4].length === 2 ? Number(m[4]) * 10 : Number(m[4]);
    out.push(((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 + frac);
  }
  for (const m of text.matchAll(/^Dialogue:[^,]*,(\d):(\d{2}):(\d{2})\.(\d{2}),/gm)) {
    out.push(((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 + (Number(m[4]) * 10));
  }
  return [...new Set(out)].sort((a, b) => a - b);
}

// ---- speech onsets over the whole track -------------------------------------------------------

// ! The filter graph and threshold are copied from SyncVerifier.OnsetsAsync so the onsets here are
//   the same events the check sees; only what is done with them differs.
function onsets() {
  const key = crypto.createHash('sha256')
    .update(`${video}|whole|${detector}|-30dB|0.35|${minSilenceMs}`).digest('hex');
  const cached = path.join(cacheDir, `${key}.json`);
  if (fs.existsSync(cached)) return JSON.parse(fs.readFileSync(cached, 'utf8'));

  const found = detector === 'silence' ? silenceOnsets() : vadOnsets();
  fs.writeFileSync(cached, JSON.stringify(found));
  return found;
}

// ! The filter graph and threshold are copied from SyncVerifier.OnsetsAsync so the onsets here are
//   the same events the check sees; only what is done with them differs.
function silenceOnsets() {
  // ! silencedetect logs to STDERR. execFileSync returns stdout only, which is empty for `-f null`
  //   → read both streams off spawnSync instead.
  const run = spawnSync(ffmpeg, [
    '-hide_banner', '-nostats', '-i', video, '-map', '0:a:0',
    '-af', 'aformat=channel_layouts=mono,silencedetect=noise=-30dB:d=0.35',
    '-f', 'null', '-',
  ], { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  const text = `${run.stdout || ''}${run.stderr || ''}`;

  const found = [];
  for (const m of text.matchAll(/silence_end:\s*([\d.]+)\s*\|\s*silence_duration:\s*([\d.]+)/g)) {
    found.push({ atMs: Math.round(Number(m[1]) * 1000), quietMs: Math.round(Number(m[2]) * 1000) });
  }
  return found;
}

// One window over the whole track. `gapMs` is the quiet a rising edge must follow, so the onsets
// that come back are already the clean ones — the same bar `--min-silence` sets for silencedetect.
function vadOnsets() {
  if (!python) {
    console.error('--python is required for --detector webrtc');
    process.exit(2);
  }
  const script = path.join(here, 'vad-onsets.py');
  const probe = spawnSync(ffmpeg.replace(/ffmpeg(\.exe)?$/i, 'ffprobe$1'), [
    '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', video,
  ], { encoding: 'utf8' });
  const seconds = Number(String(probe.stdout || '0').trim()) || 0;
  if (!seconds) return [];

  const plan = {
    video, ffmpeg, detectors: [detector],
    windows: [{ startMs: 0, lengthMs: Math.round(seconds * 1000) }],
    gapMs: minSilenceMs, minSpeechMs: 100, aggressiveness: 3,
  };
  const run = spawnSync(python, [script], {
    input: JSON.stringify(plan), encoding: 'utf8', maxBuffer: 256 * 1024 * 1024,
  });
  let parsed;
  try {
    parsed = JSON.parse(run.stdout);
  } catch {
    console.error(`vad-onsets failed: ${String(run.stderr || '').slice(0, 300)}`);
    return [];
  }
  const list = (parsed.byDetector?.[detector] ?? parsed).onsets ?? [];
  // ! quietMs is already guaranteed >= minSilenceMs by the gap rule → record it as such rather than
  //   inventing a figure the detector never reported.
  return list.map((atMs) => ({ atMs, quietMs: minSilenceMs }));
}

// ---- pair the unambiguous ones ----------------------------------------------------------------

const starts = cueStarts(subtitle);
const all = onsets();
// ! Only onsets that follow real quiet. An onset after 40 ms of silence is a pause inside a line.
const clean = all.filter((o) => o.quietMs >= minSilenceMs);

// ! A pair only counts when the match is DECISIVE. Two tests, both needed:
//   1. the cue is isolated — back-to-back dialogue makes the pairing a guess;
//   2. the nearest clean onset is at least `ratio`x nearer than the next nearest, so a cue sitting
//      between two candidates is dropped rather than assigned to one of them.
//   Without (2) a cue w/ a single onset 1.4 s away still scored as "unambiguous" and the gap
//   distribution came back w/ a 1064 ms IQR — noise wearing the shape of a measurement.
const pairs = [];
for (let i = 0; i < starts.length; i++) {
  const cue = starts[i];

  if (i > 0 && cue - starts[i - 1] < isolationMs) continue;
  if (i + 1 < starts.length && starts[i + 1] - cue < isolationMs) continue;

  const ranked = clean
    .map((o) => ({ o, d: Math.abs(o.atMs - cue) }))
    .sort((a, b) => a.d - b.d);

  if (!ranked.length || ranked[0].d > searchMs) continue;
  if (ranked.length > 1 && ranked[1].d < ranked[0].d * ratio) continue;

  pairs.push({ cueMs: cue, onsetMs: ranked[0].o.atMs, gapMs: ranked[0].o.atMs - cue });
}

const median = (list) => {
  if (!list.length) return null;
  const s = list.slice().sort((a, b) => a - b);
  const mid = Math.floor(s.length / 2);
  return s.length % 2 ? s[mid] : Math.round((s[mid - 1] + s[mid]) / 2);
};

const gaps = pairs.map((p) => p.gapMs);
const sorted = gaps.slice().sort((a, b) => a - b);
const span = starts.length ? starts[starts.length - 1] - starts[0] : 0;
const half = starts[0] + span / 2;
const early = median(pairs.filter((p) => p.cueMs < half).map((p) => p.gapMs));
const late = median(pairs.filter((p) => p.cueMs >= half).map((p) => p.gapMs));

const result = {
  video,
  subtitle,
  detector,
  cues: starts.length,
  onsetsTotal: all.length,
  onsetsClean: clean.length,
  paired: pairs.length,
  iqrMs: null,
  medianGapMs: median(gaps),
  p25Ms: sorted.length ? sorted[Math.floor(sorted.length * 0.25)] : null,
  p75Ms: sorted.length ? sorted[Math.floor(sorted.length * 0.75)] : null,
  earlyMs: early,
  lateMs: late,
  driftMs: early !== null && late !== null ? late - early : null,
};

// ! A median off a handful of pairs is not a measurement. Say so rather than printing a number.
result.iqrMs = result.p75Ms !== null && result.p25Ms !== null ? result.p75Ms - result.p25Ms : null;

// ! O8. The quantity under test is the CENTRE, and the precision of a median is
//     SE(median) ~ 1.253 * sigma / sqrt(n), sigma ~ IQR/1.349  =>  SE ~ 0.9288 * IQR / sqrt(pairs).
//   The old gate refused anything with IQR > 500, which conflates a sloppily-timed subtitle with
//   an imprecise measurement: a wide spread over many pairs still pins its centre. Gating on the
//   IQR took the usable sample on the produced-file bucket from 16 of 20 down to 0.
const SE_LIMIT_MS = 100;
result.seMs =
  result.iqrMs !== null && result.paired > 0
    ? Math.round((0.9288 * result.iqrMs) / Math.sqrt(result.paired))
    : null;

result.measurable = result.paired >= 12 && result.seMs !== null && result.seMs <= SE_LIMIT_MS;
result.verdict = !result.measurable
  ? `not measurable (${result.paired} pairs, SE ${result.seMs ?? '—'} ms)`
  : Math.abs(result.medianGapMs) <= 500
    ? `IN SYNC (median ${result.medianGapMs} ms, SE ±${result.seMs} ms)`
    : `OUT by ${result.medianGapMs} ms (SE ±${result.seMs} ms)`;

console.log(`subtitle : ${path.basename(subtitle)}`);
console.log(`cues     : ${result.cues}, onsets ${result.onsetsTotal} (${result.onsetsClean} clean)`);
console.log(`paired   : ${result.paired} unambiguous cues`);
console.log(`gap      : median ${result.medianGapMs} ms  (p25 ${result.p25Ms}, p75 ${result.p75Ms})`);
console.log(`halves   : early ${result.earlyMs} ms, late ${result.lateMs} ms, drift ${result.driftMs} ms`);
console.log(`VERDICT  : ${result.verdict}`);

if (jsonOut) fs.writeFileSync(jsonOut, JSON.stringify(result, null, 1));
