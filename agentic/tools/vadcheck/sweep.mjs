// Drives `vadcheck` over a real population drawn from the plugin's own record store, so the VAD
// question is answered against the library that produced the refusals rather than a chosen five.
//
//   node agentic/tools/vadcheck/sweep.mjs --records <records.json> --bucket stretch --take 20
//        [--python <p>] [--detector webrtc|silero] [--model <onnx>] [--shift 1500]
//        [--windows N] [--window-seconds S] [--out <json>] [--seed 7] [--gap ms] [--min-speech ms]
//
// ! Buckets are read off `Message`, which is what the status panel groups by, so the counts here
//   and the counts on screen are the same population.
//
// ! One title at a time and one decode per window — the media is on a slow SMB share, and the
//   flag cache in vad-onsets.py is what makes a second pass over the same titles free.

import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};
const has = (name) => argv.includes(name);

const BUCKETS = {
  stretch: (m) => m.includes('rescaled the subtitle across the runtime'),
  inconclusive: (m) => m.includes('reached no verdict'),
  drifting: (m) => m.includes('offset drifting'),
  misaligned: (m) => m.includes('out of alignment'),
  aligned: (m) => m.includes('already aligned with the audio'),
};

const recordsPath = value('--records');
const bucket = value('--bucket', 'stretch');
const take = Number(value('--take', '20'));
const seed = Number(value('--seed', '7'));
const outPath = value('--out');
const detectors = [];
for (let i = 0; i < argv.length - 1; i += 1) if (argv[i] === '--detector') detectors.push(argv[i + 1]);
if (!detectors.length) detectors.push('webrtc');
const python = value('--python', 'python');
const model = value('--model');
const shift = value('--shift');
const windows = value('--windows');
const windowSeconds = value('--window-seconds');
const configuration = value('--configuration', 'Debug');
const raiseSeconds = value('--raise-seconds');
const gap = value('--gap');
const minSpeech = value('--min-speech');
const minWindows = value('--min-windows') ? Number(value('--min-windows')) : null;
const maxWindows = value('--max-windows') ? Number(value('--max-windows')) : null;

if (!recordsPath || !BUCKETS[bucket]) {
  console.error(`--records is required; --bucket one of ${Object.keys(BUCKETS).join(', ')}`);
  process.exit(2);
}

const records = JSON.parse(fs.readFileSync(recordsPath, 'utf8'));

// The measurement is only worth anything on a sidecar still in the state the refusal was recorded
// against. X4 and Z1 are both this mistake; the stored hash is what makes it checkable.
function unchanged(record) {
  try {
    const buffer = fs.readFileSync(record.SourceSubtitlePath);
    if (!record.SourceSha256) return true;
    return crypto.createHash('sha256').update(buffer).digest('hex').toUpperCase() === record.SourceSha256;
  } catch {
    return false;
  }
}

function planCount(record) {
  let text;
  try {
    text = fs.readFileSync(record.SourceSubtitlePath, 'utf8');
  } catch {
    return null;
  }
  const times = [];
  for (const line of text.split(/\r?\n/)) {
    const m = /(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})/.exec(line);
    if (!m) continue;
    const frac = m[4].length === 2 ? Number(m[4]) * 10 : Number(m[4]);
    times.push(((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 + frac);
  }
  if (!times.length) return null;
  times.sort((a, b) => a - b);
  const span = times[times.length - 1] - times[0];
  if (span <= 600000) return 1;
  let count = Math.min(16, Math.max(4, Math.trunc(span / 360000)));
  if (count < 6 && Math.trunc(span / 18) >= 90000) count = 6;
  return count;
}

let pool = records.filter((r) => BUCKETS[bucket](r.Message || ''));

// One target per item; several sidecars off one film measure the same audio.
const seenItem = new Set();
pool = pool.filter((r) => {
  if (seenItem.has(r.ItemId)) return false;
  seenItem.add(r.ItemId);
  return true;
});

pool = pool.filter(unchanged);

if (minWindows !== null || maxWindows !== null) {
  pool = pool.filter((r) => {
    const count = planCount(r);
    if (count === null) return false;
    if (minWindows !== null && count < minWindows) return false;
    if (maxWindows !== null && count > maxWindows) return false;
    return true;
  });
}

// A fixed shuffle, so a rerun measures the same titles and a widened --take only adds to them.
let state = seed >>> 0;
const next = () => {
  state = (state * 1103515245 + 12345) >>> 0;
  return state / 4294967296;
};
pool = pool
  .map((r) => ({ r, k: next() }))
  .sort((a, b) => a.k - b.k)
  .map((x) => x.r);

const chosen = pool.slice(0, take);
console.error(`${bucket}: ${chosen.length} of ${pool.length} eligible`);

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const scratch = fs.mkdtempSync(path.join(process.env.TEMP || '/tmp', 'vadsweep-'));
const results = [];

for (const [index, record] of chosen.entries()) {
  const perTitle = path.join(scratch, `${index}.json`);
  const args = [
    'run', '--project', path.join(here), '--no-build', '-c', configuration, '--',
    '--video', record.VideoPath,
    '--subtitle', record.SourceSubtitlePath,
    '--python', python,
    '--json', perTitle,
  ];
  for (const name of detectors) args.push('--detector', name);
  if (model) args.push('--model', model);
  if (shift) args.push('--shift', shift);
  if (windows) args.push('--windows', windows);
  if (windowSeconds) args.push('--window-seconds', windowSeconds);
  if (raiseSeconds) args.push('--raise-seconds', raiseSeconds);
  if (gap) args.push('--gap', gap);
  if (minSpeech) args.push('--min-speech', minSpeech);

  process.stderr.write(`[${index + 1}/${chosen.length}] ${record.ItemName}\n`);

  try {
    const text = execFileSync('dotnet', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    if (!has('--quiet')) process.stdout.write(text);
  } catch (error) {
    process.stderr.write(`   failed: ${String(error.message).slice(0, 200)}\n`);
    continue;
  }

  if (!fs.existsSync(perTitle)) continue;
  for (const row of JSON.parse(fs.readFileSync(perTitle, 'utf8'))) {
    results.push({
      bucket,
      itemName: record.ItemName,
      targetKey: record.TargetKey,
      rejectedOffsetMs: record.RejectedOffsetMs ?? null,
      alignedAtMs: record.AlignedAtMs ?? null,
      ...row,
    });
  }
}

if (outPath) {
  fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
  console.error(`wrote ${results.length} rows to ${outPath}`);
}
