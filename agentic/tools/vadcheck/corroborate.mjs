// Tests whether two onset sources AGREEING on where the alignment sits separates a correct accept
// from a wrong one — the only mechanism left that could satisfy the governing constraint:
//
//   "The plugin must not write a badly synced sub (to the best of our ability to measure)."
//
//   node agentic/tools/vadcheck/corroborate.mjs --rows <sweep.json> [--truth <truth.json>]
//        [--agree 100] [--source webrtc]
//
// ! A single detector's ABSOLUTE reading is not trustworthy at this tolerance — W1 measured the
//   shipping check off ground truth by up to 906 ms. Two independent detectors landing on the same
//   shift is a different and much stronger claim than either landing somewhere on its own.
//
// ! Only false `Aligned` violates the constraint. A false `Misaligned` is harmless ∵ every title
//   reaching the fallback is already refused today → the column that decides is WRONG ACCEPT alone.

import fs from 'node:fs';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const rowsPath = value('--rows');
const truthPath = value('--truth');
const agreeWithin = Number(value('--agree', '100'));
const vadSource = value('--source', 'webrtc');

if (!rowsPath) {
  console.error('--rows <sweep.json> is required');
  process.exit(2);
}

const rows = JSON.parse(fs.readFileSync(rowsPath, 'utf8')).filter((r) => r.appliedShiftMs === 0);

const byTitle = new Map();
for (const r of rows) {
  if (!byTitle.has(r.subtitle)) byTitle.set(r.subtitle, { itemName: r.itemName, sources: {} });
  byTitle.get(r.subtitle).sources[r.source] = r;
}

const truth = new Map();
if (truthPath) {
  for (const t of JSON.parse(fs.readFileSync(truthPath, 'utf8'))) {
    if (t.truthEarlyMs === null) continue;
    truth.set(t.subtitle, t);
  }
}

const has = (v) => v !== null && v !== undefined;

console.log('\n== corroboration: do the two sources land on the same shift? ==\n');
console.log(['title', 'silence pk', `${vadSource} pk`, 'gap', 'agree?', `${vadSource} verdict`, 'truth'].join('\t'));

const table = [];
for (const [subtitle, entry] of byTitle) {
  const vad = entry.sources[vadSource];
  const sil = entry.sources.silence;
  if (!vad || !sil) continue;

  const sp = sil.relaxedShiftMs;
  const vp = vad.relaxedShiftMs;
  const gap = has(sp) && has(vp) ? Math.abs(sp - vp) : null;
  const agree = gap !== null && gap <= agreeWithin;

  const t = truth.get(subtitle);
  const inSync = t ? /IN SYNC/.test(t.truthVerdict) : null;

  table.push({ subtitle, itemName: entry.itemName, sp, vp, gap, agree, verdict: vad.verdict, inSync, t });

  console.log([
    (entry.itemName || '').slice(0, 26),
    has(sp) ? sp : '—',
    has(vp) ? vp : '—',
    gap === null ? '—' : gap,
    gap === null ? '—' : (agree ? 'YES' : 'no'),
    vad.verdict,
    t ? (inSync ? 'IN SYNC' : t.truthVerdict.slice(0, 22)) : '—',
  ].join('\t'));
}

// ---- the decision table ----
const accepts = table.filter((r) => r.verdict === 'Aligned' && r.inSync !== null);
if (accepts.length) {
  console.log('\n== would corroboration have stopped the wrong accepts? ==\n');
  console.log(['rule', 'accepts kept', 'CORRECT', 'WRONG', 'recovery lost'].join('\t'));

  const report = (label, keep) => {
    const kept = accepts.filter(keep);
    console.log([label, kept.length, kept.filter((r) => r.inSync).length,
      kept.filter((r) => !r.inSync).length, accepts.length - kept.length].join('\t'));
  };

  report('today (accept all)', () => true);
  for (const bound of [250, 150, 100, 50]) {
    report(`agree within ${bound}ms`, (r) => r.gap !== null && r.gap <= bound);
  }
  report('require silence peak at all', (r) => has(r.sp));
}

const withTruth = table.filter((r) => r.inSync !== null);
console.log(`\nrows: ${table.length}   with ground truth: ${withTruth.length}`
  + `   ${vadSource} accepts w/ truth: ${accepts.length}`);
if (accepts.length < 8) {
  console.log('! too few accepts w/ ground truth to set a threshold on. Widen the sample before'
    + ' concluding — fitting a bound to a handful is exactly the W1 mistake.');
}
