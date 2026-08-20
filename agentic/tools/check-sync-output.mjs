// Validates shipped syncs against their vault backups: does the file on disk say what the record
// claims, and is the result plausible against the video's real duration?
//
//   node agentic/tools/check-sync-output.mjs --records <records.json> --vault <backups dir>
//                                            [--match <substring>] [--limit N]
//
// --vault remaps the server's local BackupPath onto wherever that directory is reachable from here.

import { readFileSync, existsSync, statSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

function arg(name, fallback = null) {
  const i = process.argv.indexOf(`--${name}`);
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}

const recordsPath = arg('records');
const vaultRoot = arg('vault');
const match = arg('match');
const limit = Number(arg('limit', '0'));

if (!recordsPath || !vaultRoot) {
  console.error('usage: --records <records.json> --vault <backups dir> [--match s] [--limit n]');
  process.exit(2);
}

// SRT timings are ASCII; latin1 keeps byte-level text comparable whatever the file's encoding is.
function read(path) {
  return readFileSync(path, 'latin1').replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');
}

const TIME = /(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})/;

function ms(h, m, s, milli) {
  return ((+h * 60 + +m) * 60 + +s) * 1000 + +milli;
}

function parse(path) {
  const cues = [];
  let pending = null;

  for (const line of read(path).split('\n')) {
    const t = TIME.exec(line);
    if (t) {
      if (pending) cues.push(pending);
      pending = { start: ms(t[1], t[2], t[3], t[4]), end: ms(t[5], t[6], t[7], t[8]), text: [] };
      continue;
    }
    if (pending && line.trim()) pending.text.push(line.trim());
  }

  if (pending) cues.push(pending);
  return cues;
}

// Markers seconv's --remove-text-for-hi targets: bracketed cues, speaker labels, music notes.
const HI_BRACKET = /[\[(][^\])]*[\])]/;
const HI_SPEAKER = /^[-\s]*[A-Z][A-Z0-9 .'#-]{1,24}:/;
const HI_NOTE = /[\u266a\u266b\u2669\u266c]/;

function hiCount(cues) {
  let n = 0;
  for (const cue of cues) {
    const text = cue.text.join(' ');
    if (HI_BRACKET.test(text) || HI_SPEAKER.test(text) || HI_NOTE.test(text)) n++;
  }
  return n;
}

function key(cue) {
  return cue.text
    .join(' ')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '')
    .slice(0, 60);
}

// Pairs cues that carry the same text and appear exactly once on each side.
function matchByText(before, after) {
  const index = new Map();
  for (const cue of before) {
    const k = key(cue);
    if (k.length < 8) continue;
    index.set(k, index.has(k) ? null : cue);
  }

  const used = new Set();
  const pairs = [];

  for (const cue of after) {
    const k = key(cue);
    if (k.length < 8 || used.has(k)) continue;
    const twin = index.get(k);
    if (!twin) continue;
    used.add(k);
    pairs.push({ before: twin, after: cue });
  }

  return pairs;
}

// delta(t) = intercept + slope*t over matched cues, refit once without the worst residuals.
function fit(pairs) {
  if (pairs.length < 8) return null;

  let sample = pairs.map((p) => ({ t: p.before.start, d: p.after.start - p.before.start }));

  for (let pass = 0; pass < 2; pass++) {
    const n = sample.length;
    if (n < 8) return null;

    const meanT = sample.reduce((a, p) => a + p.t, 0) / n;
    const meanD = sample.reduce((a, p) => a + p.d, 0) / n;

    let num = 0;
    let den = 0;
    for (const p of sample) {
      num += (p.t - meanT) * (p.d - meanD);
      den += (p.t - meanT) ** 2;
    }

    const slope = den === 0 ? 0 : num / den;
    const intercept = meanD - slope * meanT;

    if (pass === 1) return { slope, intercept };

    const residuals = sample
      .map((p) => Math.abs(p.d - (intercept + slope * p.t)))
      .sort((a, b) => a - b);
    const cutoff = Math.max(250, residuals[Math.floor(residuals.length * 0.9)]);
    sample = sample.filter((p) => Math.abs(p.d - (intercept + slope * p.t)) <= cutoff);
  }

  return null;
}

function words(cues) {
  return cues
    .flatMap((c) => c.text)
    .join(' ')
    .replace(HI_BRACKET, ' ')
    .toLowerCase()
    .replace(/[^a-z0-9' ]+/g, ' ')
    .split(/\s+/)
    .filter(Boolean);
}

const vendored = join(dirname(fileURLToPath(import.meta.url)), 'ffmpeg', 'ffprobe.exe');

let ffprobe = null;
for (const candidate of [vendored, 'ffprobe', 'ffprobe.exe']) {
  try {
    execFileSync(candidate, ['-version'], { stdio: 'ignore' });
    ffprobe = candidate;
    break;
  } catch {
    /* keep looking */
  }
}

function duration(video) {
  if (!ffprobe || !existsSync(video)) return null;
  try {
    const out = execFileSync(
      ffprobe,
      ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', video],
      { encoding: 'utf8', timeout: 60000 }
    );
    const seconds = Number.parseFloat(out.trim());
    return Number.isFinite(seconds) ? Math.round(seconds * 1000) : null;
  } catch {
    return null;
  }
}

function fmt(v) {
  return v === null || v === undefined ? '-' : `${v}ms`;
}

function clock(v) {
  const s = Math.round(v / 1000);
  return `${Math.floor(s / 60)}m${String(s % 60).padStart(2, '0')}s`;
}

const records = JSON.parse(readFileSync(recordsPath, 'utf8'));

let selected = records.filter(
  (r) => r.Status === 'Synced' && r.BackupPath && r.OutputPath && r.Provenance !== 'Superseded'
);

if (match) {
  const needle = match.toLowerCase();
  selected = selected.filter((r) => (r.ItemName || '').toLowerCase().includes(needle));
}

if (limit > 0) selected = selected.slice(0, limit);

let checked = 0;
let failures = 0;

for (const record of selected) {
  // The server's own path is meaningless here; only the vault folder and filename carry over.
  const parts = record.BackupPath.replace(/\\/g, '/').split('/');
  const backup = join(vaultRoot, parts[parts.length - 2], parts[parts.length - 1]);
  const output = record.OutputPath;

  if (!existsSync(backup) || !existsSync(output)) continue;

  const before = parse(backup);
  const after = parse(output);
  if (before.length === 0 || after.length === 0) continue;

  checked++;
  const problems = [];

  const firstAfter = after[0].start;
  const lastAfter = after[after.length - 1].start;

  // Cue identity, not cue position: a dropped marker or a stripped opener shifts every index.
  const pairs = matchByText(before, after);
  const line = fit(pairs);

  // The plugin measures its shift at the input's first cue; evaluate the fit at the same instant.
  const shift = line === null ? null : Math.abs(Math.round(line.intercept + line.slope * before[0].start));
  const ratio = line === null ? null : 1 + line.slope;
  const naive = Math.abs(firstAfter - before[0].start);

  if (line === null) {
    problems.push(`inconclusive: only ${pairs.length} cues matched by text`);
  } else if (record.AppliedOffsetMs !== null && Math.abs(shift - record.AppliedOffsetMs) > 500) {
    problems.push(
      `recorded shift ${fmt(record.AppliedOffsetMs)} but the cues moved ${fmt(shift)}` +
        (Math.abs(naive - record.AppliedOffsetMs) <= 2 ? '; the recorded value came from a changed first cue' : '')
    );
  }

  // A zero-duration or letterless opener is a tool marker, and the engine drops it.
  const opener = before[0];
  if (opener.end - opener.start < 10 || !/[a-z]/i.test(opener.text.join(''))) {
    problems.push(
      `leading marker cue ${JSON.stringify(opener.text.join(' ').slice(0, 20))} at ${opener.start}ms; ` +
        `the recorded ${fmt(record.AppliedOffsetMs)} shift is measured against it`
    );
  }

  const hiStage = (record.Stages || []).find((s) => s.Kind === 'Transform');
  const strippedHi = hiStage && hiStage.Outcome === 'Succeeded';
  const hiBefore = hiCount(before);
  const hiAfter = hiCount(after);

  if (!strippedHi && after.length !== before.length) {
    problems.push(`cue count changed ${before.length} to ${after.length} with no transform stage`);
  }

  if (strippedHi && hiAfter > hiBefore * 0.25) {
    problems.push(`hearing-impaired strip left ${hiAfter} of ${hiBefore} marked cues`);
  }

  // A strip must not take the dialogue with it.
  const wordsBefore = words(before).length;
  const wordsAfter = words(after).length;
  if (wordsBefore > 0 && wordsAfter / wordsBefore < 0.55) {
    problems.push(`dialogue dropped from ${wordsBefore} to ${wordsAfter} words`);
  }

  for (let i = 1; i < after.length; i++) {
    if (after[i].start < after[i - 1].start) {
      problems.push(`cue ${i + 1} starts before the one ahead of it`);
      break;
    }
  }

  if (after.some((c) => c.end < c.start)) problems.push('a cue ends before it starts');
  if (firstAfter < 0) problems.push('the first cue starts before zero');

  const runtime = duration(record.VideoPath);
  if (runtime && lastAfter > runtime) {
    problems.push(`last cue at ${clock(lastAfter)} is past the ${clock(runtime)} runtime`);
  }

  if (runtime && lastAfter < runtime * 0.5) {
    problems.push(`last cue at ${clock(lastAfter)} is under half the ${clock(runtime)} runtime`);
  }

  const status = problems.length === 0 ? 'ok  ' : 'FAIL';
  if (problems.length > 0) failures++;

  const rate = ratio === null ? '-' : `${((ratio - 1) * 100).toFixed(2)}%`;
  console.log(
    `${status} ${record.ItemName}\n` +
      `       ${basename(output)}\n` +
      `       cues ${before.length}->${after.length}  shift ${fmt(shift)} (recorded ${fmt(record.AppliedOffsetMs)})  ` +
      `stretch ${rate}  first ${clock(firstAfter)}  last ${clock(lastAfter)}` +
      (runtime ? `  runtime ${clock(runtime)}` : '') +
      (strippedHi ? `  hi ${hiBefore}->${hiAfter}` : '')
  );

  for (const problem of problems) console.log(`       ! ${problem}`);
}

console.log(`\nchecked ${checked} synced subtitles, ${failures} with problems`);
process.exit(failures > 0 ? 1 : 0);
