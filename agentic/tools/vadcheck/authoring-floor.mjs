// The distribution of the cue-start-to-speech-onset gap across titles the check already calls
// aligned — i.e. files that are FINE. That spread is the **authoring + detector noise floor**, and
// `AlignedWithinMs` has to sit outside it.
//
//   node agentic/tools/vadcheck/authoring-floor.mjs --records <records.json> [--bucket aligned]
//        [--take 120] [--seed 7] [--out <json>] [--ratio 3]
//
// ! WHY THIS IS WORTH RE-MEASURING. `AUDIT.md` W1 rejected subtracting a display lead ∵ the gap
//   looked per-source rather than constant. W1 measured against **embedded tracks**, and every
//   embedded track sampled on this library is a `dvd_subtitle` carrying its own per-disc offset →
//   W1's scatter may be variance between DVD masters, ¬variance in authoring. This measures the
//   same quantity against the audio, which is the only reference the project accepts.
//
// ! An out-of-sync file in the sample widens the floor and argues for a LOOSER bound → the bias of
//   this measurement is conservative, which is the right direction for a safety threshold.

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

const BUCKETS = {
  aligned: (m) => m.includes('already aligned with the audio'),
  inconclusive: (m) => m.includes('reached no verdict'),
  stretch: (m) => m.includes('rescaled the subtitle across the runtime'),
};

const recordsPath = value('--records');
const bucket = value('--bucket', 'aligned');
const take = Number(value('--take', '120'));
const seed = Number(value('--seed', '7'));
const ratio = value('--ratio', '3');
const outPath = value('--out');
const reportOnly = value('--report');

// ! `--report` re-analyses a finished run w/o touching the media. The centred distribution is the
//   decision variable — `|gap - typical_lead| < bound` — ¬the raw gap, ∵ a subtitle carrying the
//   normal authored lead is CORRECT, and only deviation from that convention is a defect.
if (reportOnly) {
  report(JSON.parse(fs.readFileSync(reportOnly, 'utf8')));
  process.exit(0);
}

if (!recordsPath || !BUCKETS[bucket]) {
  console.error(`--records required; --bucket one of ${Object.keys(BUCKETS).join(', ')}`);
  process.exit(2);
}

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const script = path.join(here, 'audio-truth.mjs');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'floor-'));

const records = JSON.parse(fs.readFileSync(recordsPath, 'utf8'));

function unchanged(record) {
  try {
    if (!record.SourceSha256) return true;
    const buffer = fs.readFileSync(record.SourceSubtitlePath);
    return crypto.createHash('sha256').update(buffer).digest('hex').toUpperCase() === record.SourceSha256;
  } catch {
    return false;
  }
}

let pool = records.filter((r) => BUCKETS[bucket](r.Message || ''));
const seen = new Set();
pool = pool.filter((r) => (seen.has(r.ItemId) ? false : (seen.add(r.ItemId), true)));
pool = pool.filter(unchanged);

let state = seed >>> 0;
const next = () => { state = (state * 1103515245 + 12345) >>> 0; return state / 4294967296; };
pool = pool.map((r) => ({ r, k: next() })).sort((a, b) => a.k - b.k).map((x) => x.r);

const chosen = pool.slice(0, take);
console.error(`${bucket}: ${chosen.length} of ${pool.length} eligible`);

const results = [];
for (const [index, record] of chosen.entries()) {
  process.stderr.write(`[${index + 1}/${chosen.length}] ${record.ItemName}\n`);
  const perTitle = path.join(scratch, `${index}.json`);

  spawnSync('node', [
    script, '--video', record.VideoPath, '--subtitle', record.SourceSubtitlePath,
    '--ratio', String(ratio), '--json', perTitle,
  ], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

  if (!fs.existsSync(perTitle)) continue;
  const t = JSON.parse(fs.readFileSync(perTitle, 'utf8'));
  if (!t.measurable) continue;
  results.push({ itemName: record.ItemName, ...t });

  // ! Written after every title, not once at the end. A run stopped early used to discard
  //   everything it had measured, and these runs are hours of reads off a network share.
  checkpoint();
}

checkpoint();
if (outPath) console.error(`wrote ${results.length} rows to ${outPath}`);

report(results);

function checkpoint() {
  if (!outPath) return;
  fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
}

function report(rows) {
  const measured = rows.filter((r) => r.measurable !== false && r.medianGapMs !== null);
  const gaps = measured.map((r) => r.medianGapMs).sort((a, b) => a - b);
  if (!gaps.length) {
    console.log('no measurable titles');
    return;
  }
  const at = (list, q) => list[Math.min(list.length - 1, Math.floor(list.length * q))];
  const centre = at(gaps, 0.5);

  console.log(`
== raw gap (cue start -> speech onset) ==
`);
  console.log(`measurable titles : ${measured.length} of ${rows.length}`);
  console.log(`min / max         : ${gaps[0]} .. ${gaps[gaps.length - 1]} ms`);
  for (const q of [0.05, 0.25, 0.5, 0.75, 0.95]) {
    console.log(`p${String(Math.round(q * 100)).padStart(2, '0')}               : ${at(gaps, q)} ms`);
  }
  console.log(`positive / negative: ${gaps.filter((g) => g > 0).length} / ${gaps.filter((g) => g < 0).length}`);

  // ! THE decision variable. `typical_lead` is the population median; a file carrying it is correct.
  const dev = measured.map((r) => Math.abs(r.medianGapMs - centre)).sort((a, b) => a - b);
  console.log(`
== centred on typical lead = ${centre} ms — |gap - typical| ==
`);
  for (const q of [0.5, 0.75, 0.9, 0.95, 0.99]) {
    console.log(`p${String(Math.round(q * 100)).padStart(2, '0')}               : ${at(dev, q)} ms`);
  }

  console.log(`
== what each bound refuses, among files that are FINE ==
`);
  console.log(['bound', 'raw |gap|', 'CENTRED |gap-typ|'].join('	'));
  for (const bound of [100, 150, 200, 250, 300, 400, 500]) {
    const raw = gaps.filter((g) => Math.abs(g) > bound).length;
    const cen = dev.filter((d) => d > bound).length;
    console.log([`${bound} ms`,
      `${raw} (${Math.round((100 * raw) / gaps.length)}%)`,
      `${cen} (${Math.round((100 * cen) / dev.length)}%)`].join('	'));
  }

  const spreads = measured.map((r) => r.iqrMs).filter((v) => v !== null).sort((a, b) => a - b);
  if (spreads.length) {
    console.log(`
within-title IQR  : median ${at(spreads, 0.5)} ms (p90 ${at(spreads, 0.9)} ms)`);
  }
}

