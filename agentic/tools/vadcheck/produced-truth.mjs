// Measures the ENGINE'S OUTPUT against the video's own audio, then joins it with what the check
// said about that same output. This is the only population that matters to the governing
// constraint: the plugin writes the engine's output, ¬the source sidecar.
//
//   node produced-truth.mjs --outcome <outcome.json> --out <json>
//
// ! Every earlier measurement scored SOURCE sidecars. A source that is 900 ms out is supposed to be
//   rewritten; the question was never whether the source is good, but whether what replaces it is.

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const argv = process.argv.slice(2);
const value = (n, d = null) => { const i = argv.indexOf(n); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : d; };

const outcomePath = value('--outcome');
const outPath = value('--out');
const here = path.dirname(fileURLToPath(import.meta.url));
const script = path.join(here, 'audio-truth.mjs');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'prodtruth-'));

const rows = JSON.parse(fs.readFileSync(outcomePath, 'utf8'));

// index titles by targetKey, keeping every stage/source cell
const titles = new Map();
for (const r of rows) {
  if (!titles.has(r.targetKey)) titles.set(r.targetKey, { itemName: r.itemName, video: r.video, cells: {} });
  titles.get(r.targetKey).cells[`${r.stage}|${r.source}`] = r;
}

const results = [];
let i = 0;
for (const [key, t] of titles) {
  i += 1;
  const produced = t.cells['produced|webrtc'] ?? t.cells['produced|silence'];
  if (!produced) continue;
  const srt = produced.subtitle;
  if (!fs.existsSync(srt)) { process.stderr.write(`[${i}] MISSING ${srt}\n`); continue; }

  process.stderr.write(`[${i}/${titles.size}] ${t.itemName}\n`);
  const per = path.join(scratch, `${i}.json`);
  spawnSync('node', [script, '--video', t.video, '--subtitle', srt, '--ratio', '3', '--json', per],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (!fs.existsSync(per)) { process.stderr.write(`   audio-truth failed\n`); continue; }
  const truth = JSON.parse(fs.readFileSync(per, 'utf8'));

  results.push({
    itemName: t.itemName,
    targetKey: key,
    producedSrt: srt,
    truthMedianMs: truth.medianGapMs,
    truthPaired: truth.paired,
    truthIqrMs: truth.iqrMs,
    truthSeMs: truth.seMs,
    truthMeasurable: truth.measurable,
    srcSilence: t.cells['original|silence']?.verdict ?? null,
    srcWebrtc: t.cells['original|webrtc']?.verdict ?? null,
    prodSilence: t.cells['produced|silence']?.verdict ?? null,
    prodWebrtc: t.cells['produced|webrtc']?.verdict ?? null,
    prodWebrtcShift: t.cells['produced|webrtc']?.bestShiftMs ?? null,
    prodWebrtcDrift: t.cells['produced|webrtc']?.driftMs ?? null,
  });
}

if (outPath) fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
console.error(`wrote ${results.length} rows`);
