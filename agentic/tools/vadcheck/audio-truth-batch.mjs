// Runs `audio-truth.mjs` over every title in a sweep and joins the result against what each onset
// source said — the measurement that decides whether a recovered verdict is RIGHT.
//
//   node agentic/tools/vadcheck/audio-truth-batch.mjs --rows <sweep.json> [--out <json>]
//        [--source webrtc] [--ratio 3] [--take N]
//
// ! The only column that can violate the governing constraint is WRONG ACCEPT: the source said
//   `Aligned` and the audio says the subtitle is out. A false `Misaligned` is harmless ∵ every title
//   reaching the fallback is already refused today.
//
// ! Reads whole-track audio once per title. Keep --take sane; the media is on a slow share. The
//   onset cache in audio-truth.mjs makes a rerun free.

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const rowsPath = value('--rows');
const outPath = value('--out');
const vadSource = value('--source', 'webrtc');
const ratio = value('--ratio', '3');
const take = value('--take') ? Number(value('--take')) : null;

if (!rowsPath) {
  console.error('--rows <sweep.json> is required');
  process.exit(2);
}

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const script = path.join(here, 'audio-truth.mjs');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'audiotruth-'));

const rows = JSON.parse(fs.readFileSync(rowsPath, 'utf8')).filter((r) => r.appliedShiftMs === 0);
const byTitle = new Map();
for (const r of rows) {
  if (!byTitle.has(r.subtitle)) {
    byTitle.set(r.subtitle, { video: r.video, itemName: r.itemName, bucket: r.bucket, sources: {} });
  }
  byTitle.get(r.subtitle).sources[r.source] = r;
}

let titles = [...byTitle.entries()];
if (take) titles = titles.slice(0, take);

const results = [];
console.log(['title', 'AUDIO TRUTH', 'paired', 'IQR', 'silence', vadSource, `${vadSource} shift`, 'JUDGEMENT'].join('\t'));

for (const [subtitle, entry] of titles) {
  const perTitle = path.join(scratch, `${results.length}.json`);
  process.stderr.write(`[${results.length + 1}/${titles.length}] ${entry.itemName}\n`);

  const run = spawnSync('node', [
    script, '--video', entry.video, '--subtitle', subtitle,
    '--ratio', String(ratio), '--json', perTitle,
  ], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

  if (!fs.existsSync(perTitle)) {
    process.stderr.write(`   failed: ${String(run.stderr || '').slice(0, 160)}\n`);
    continue;
  }

  const truth = JSON.parse(fs.readFileSync(perTitle, 'utf8'));
  const vad = entry.sources[vadSource];
  const sil = entry.sources.silence;

  // ! `measurable` already refuses a median off too few pairs or w/ an IQR wider than the tolerance
  //   it is judging. An unmeasurable title carries no judgement rather than a soft one.
  const inSync = truth.measurable ? Math.abs(truth.medianGapMs) <= 500 : null;

  let judgement = '—';
  if (vad && inSync !== null) {
    if (vad.verdict === 'Aligned') judgement = inSync ? 'correct accept' : '!! WRONG ACCEPT';
    else if (vad.verdict === 'Misaligned') judgement = inSync ? 'false refuse (harmless)' : 'correct refuse';
    else judgement = 'no verdict';
  }

  const record = {
    subtitle,
    video: entry.video,
    itemName: entry.itemName,
    bucket: entry.bucket,
    truthMedianMs: truth.medianGapMs,
    truthPaired: truth.paired,
    truthIqrMs: truth.iqrMs,
    truthSeMs: truth.seMs,
    truthDriftMs: truth.driftMs,
    truthMeasurable: truth.measurable,
    truthInSync: inSync,
    silenceVerdict: sil?.verdict ?? null,
    vadVerdict: vad?.verdict ?? null,
    vadShiftMs: vad?.bestShiftMs ?? null,
    vadDriftMs: vad?.driftMs ?? null,
    judgement,
  };
  results.push(record);

  console.log([
    (entry.itemName || '').slice(0, 26),
    truth.measurable ? `${truth.medianGapMs} ms` : 'not measurable',
    truth.paired, truth.iqrMs ?? '—', truth.seMs ?? '—',
    sil?.verdict ?? '—', vad?.verdict ?? '—', vad?.bestShiftMs ?? '—',
    judgement,
  ].join('\t'));
}

// ---- the decision table ----
const judged = results.filter((r) => r.truthInSync !== null && r.vadVerdict);
const accepts = judged.filter((r) => r.vadVerdict === 'Aligned');
const refuses = judged.filter((r) => r.vadVerdict === 'Misaligned');

console.log('\n== the constraint ==\n');
console.log(`titles w/ usable audio truth : ${judged.length} of ${results.length}`);
console.log(`${vadSource} accepts                : ${accepts.length}`);
console.log(`  correct                     : ${accepts.filter((r) => r.truthInSync).length}`);
console.log(`  !! WRONG ACCEPT             : ${accepts.filter((r) => !r.truthInSync).length}`);
console.log(`${vadSource} refusals               : ${refuses.length}`);
console.log(`  correct                     : ${refuses.filter((r) => !r.truthInSync).length}`);
console.log(`  false (harmless)            : ${refuses.filter((r) => r.truthInSync).length}`);

for (const r of accepts.filter((x) => !x.truthInSync)) {
  console.log(`\n  ! WRONG ACCEPT: ${r.itemName}`);
  console.log(`      audio says ${r.truthMedianMs} ms (${r.truthPaired} pairs, IQR ${r.truthIqrMs})`);
  console.log(`      ${vadSource} said Aligned at ${r.vadShiftMs} ms`);
}

if (outPath) {
  fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
  console.error(`\nwrote ${results.length} rows to ${outPath}`);
}
