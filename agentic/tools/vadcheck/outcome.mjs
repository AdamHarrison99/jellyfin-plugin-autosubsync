// The refused run reproduced, then judged by both onset sources. `check-stretch-outcome.ps1` does
// this for the shipping check alone; this exists ∵ the stretch guard's question is about the file
// the ENGINE produced, and the plugin deletes that file, so nothing in the log or the store can be
// asked about it.
//
//   node agentic/tools/vadcheck/outcome.mjs --records <records.json> --bucket stretch --take 10
//        --engine <assy-cli.exe> --python <p> [--detector webrtc] [--model <onnx>]
//        [--min-windows 6] [--max-windows 5] [--out <json>] [--seed 7]
//
// Per title it reports six rows — the ORIGINAL sidecar and the ENGINE OUTPUT, each scored by
// silencedetect and by each VAD — plus what the engine said it did. The last rows are the only
// ones that decide anything.
//
// ! One sync and two audio reads per title. Keep --take small; the media is on a slow share.

import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const BUCKETS = {
  stretch: (m) => m.includes('rescaled the subtitle across the runtime'),
  inconclusive: (m) => m.includes('reached no verdict'),
  drifting: (m) => m.includes('offset drifting'),
  misaligned: (m) => m.includes('out of alignment'),
  aligned: (m) => m.includes('already aligned with the audio'),
};

const recordsPath = value('--records');
const bucket = value('--bucket', 'stretch');
const take = Number(value('--take', '10'));
const seed = Number(value('--seed', '7'));
const engine = value('--engine');
const python = value('--python', 'python');
const model = value('--model');
const outPath = value('--out');
const minWindows = value('--min-windows') ? Number(value('--min-windows')) : null;
const maxWindows = value('--max-windows') ? Number(value('--max-windows')) : null;
const detectors = [];
for (let i = 0; i < argv.length - 1; i++) if (argv[i] === '--detector') detectors.push(argv[i + 1]);
if (!detectors.length) detectors.push('webrtc');

if (!recordsPath || !engine || !BUCKETS[bucket]) {
  console.error('--records, --engine and a valid --bucket are required');
  process.exit(2);
}

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'vadoutcome-'));

// The same global options the plugin pins, so the engine behaves as it does in the field.
const configPath = path.join(scratch, 'assy-config.json');
fs.writeFileSync(configPath, JSON.stringify({
  automatic_save_location: 'save_next_to_input_subtitle',
  add_tool_prefix: false,
  custom_suffix: '',
  backup_subtitles_before_overwriting: false,
  keep_extracted_subtitles: false,
  keep_converted_subtitles: false,
  skip_previously_processed_videos: false,
  check_updates_startup: false,
  keep_log_records: false,
}, null, 2));

const records = JSON.parse(fs.readFileSync(recordsPath, 'utf8'));

function unchanged(record) {
  try {
    const buffer = fs.readFileSync(record.SourceSubtitlePath);
    if (!record.SourceSha256) return true;
    return crypto.createHash('sha256').update(buffer).digest('hex').toUpperCase() === record.SourceSha256;
  } catch {
    return false;
  }
}

function windowCount(record) {
  let text;
  try { text = fs.readFileSync(record.SourceSubtitlePath, 'utf8'); } catch { return null; }
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
const seenItem = new Set();
pool = pool.filter((r) => (seenItem.has(r.ItemId) ? false : (seenItem.add(r.ItemId), true)));
pool = pool.filter(unchanged);
if (minWindows !== null || maxWindows !== null) {
  pool = pool.filter((r) => {
    const c = windowCount(r);
    if (c === null) return false;
    if (minWindows !== null && c < minWindows) return false;
    if (maxWindows !== null && c > maxWindows) return false;
    return true;
  });
}

let state = seed >>> 0;
const next = () => { state = (state * 1103515245 + 12345) >>> 0; return state / 4294967296; };
pool = pool.map((r) => ({ r, k: next() })).sort((a, b) => a.k - b.k).map((x) => x.r);

const chosen = pool.slice(0, take);
console.error(`${bucket}: ${chosen.length} of ${pool.length} eligible`);

const results = [];

for (const [index, record] of chosen.entries()) {
  process.stderr.write(`[${index + 1}/${chosen.length}] ${record.ItemName}\n`);

  const produced = path.join(scratch, `out${index}.srt`);
  let engineStderr = '';
  let ok = false;

  try {
    execFileSync(engine, [
      '--no-color', '--config-file', configPath,
      'sync', record.VideoPath, record.SourceSubtitlePath,
      '-o', produced, '-t', 'ffsubsync', '--json', '--no-prefix',
    ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 64 * 1024 * 1024 });
    ok = fs.existsSync(produced);
  } catch (error) {
    engineStderr = String(error.stderr || '');
    ok = fs.existsSync(produced);
  }

  // The three numbers the engine prints about its own work. Z3: a floor, never a warrant.
  const declared = {
    score: /score:\s*(-?[\d.]+)/.exec(engineStderr)?.[1] ?? null,
    offset: /offset seconds:\s*(-?[\d.]+)/.exec(engineStderr)?.[1] ?? null,
    rate: /framerate scale factor:\s*(-?[\d.]+)/.exec(engineStderr)?.[1] ?? null,
  };

  for (const [stage, subtitle] of [['original', record.SourceSubtitlePath], ['produced', produced]]) {
    if (stage === 'produced' && !ok) continue;

    const perTitle = path.join(scratch, `${index}-${stage}.json`);
    const args = ['run', '--project', here, '--no-build', '--',
      '--video', record.VideoPath, '--subtitle', subtitle,
      '--python', python, '--json', perTitle];
    for (const d of detectors) args.push('--detector', d);
    if (model) args.push('--model', model);

    try {
      const text = execFileSync('dotnet', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
      process.stdout.write(text.split('\n').filter((l) => !l.startsWith('title ')).join('\n'));
    } catch (error) {
      process.stderr.write(`   score failed: ${String(error.message).slice(0, 160)}\n`);
      continue;
    }

    if (!fs.existsSync(perTitle)) continue;
    for (const row of JSON.parse(fs.readFileSync(perTitle, 'utf8'))) {
      results.push({
        bucket,
        stage,
        itemName: record.ItemName,
        targetKey: record.TargetKey,
        rejectedOffsetMs: record.RejectedOffsetMs ?? null,
        engineScore: declared.score,
        engineOffset: declared.offset,
        engineRate: declared.rate,
        ...row,
      });
    }
  }
}

if (outPath) {
  fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
  console.error(`wrote ${results.length} rows to ${outPath}`);
}
