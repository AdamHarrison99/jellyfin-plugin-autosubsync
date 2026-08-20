// Turns the sweep's rows into the four tables the VAD question is actually decided on:
//
//   1. safety   — on titles the shipping check calls Aligned, does the VAD source disagree?
//   2. recovery — on titles it cannot measure, does the VAD source produce a verdict?
//   3. drift    — does the VAD source measure drift where silencedetect returns null?
//   4. recall   — given a known 1500 ms displacement, does the source hand it back?
//
//   node agentic/tools/vadcheck/analyse.mjs <sweep.json> [<sweep.json> ...]
//
// ! A verdict is not a right answer. Recall is the only column here carrying ground truth, so a
//   recovery that fails recall is a source inventing verdicts, not measuring them.

import fs from 'node:fs';

const rows = [];
for (const file of process.argv.slice(2)) {
  for (const row of JSON.parse(fs.readFileSync(file, 'utf8'))) rows.push(row);
}

const key = (r) => `${r.bucket}|${r.itemName}|${r.targetKey ?? r.subtitle}`;
const sources = [...new Set(rows.map((r) => r.source))];
const buckets = [...new Set(rows.map((r) => r.bucket))];

// The check's own tolerance. A recovered shift is only recovered if it lands inside it.
const ALIGNED_WITHIN = 500;
const RECALL_TOLERANCE = 250;

function at(bucket, source, shift) {
  const found = new Map();
  for (const r of rows) {
    if (r.bucket !== bucket || r.source !== source || r.appliedShiftMs !== shift) continue;
    found.set(key(r), r);
  }
  return found;
}

console.log('\n== verdicts, as shipped (no injected shift) ==\n');
console.log(['bucket', 'source', 'n', 'Aligned', 'Misaligned', 'Inconcl.', 'drift measured'].join('\t'));
for (const bucket of buckets) {
  for (const source of sources) {
    const found = [...at(bucket, source, 0).values()];
    if (!found.length) continue;
    const count = (v) => found.filter((r) => r.verdict === v).length;
    const drift = found.filter((r) => r.driftMs !== null && r.driftMs !== undefined).length;
    console.log([bucket, source, found.length, count('Aligned'), count('Misaligned'),
      count('Inconclusive'), drift].join('\t'));
  }
}

console.log('\n== 1. safety: where silencedetect says Aligned, what does the VAD source say? ==\n');
console.log(['bucket', 'source', 'n agreed on', 'also Aligned', 'Inconclusive', 'MISALIGNED (break)'].join('\t'));
for (const bucket of buckets) {
  const base = at(bucket, 'silence', 0);
  for (const source of sources) {
    if (source === 'silence') continue;
    const other = at(bucket, source, 0);
    const shared = [...base.entries()].filter(([k, r]) => r.verdict === 'Aligned' && other.has(k));
    if (!shared.length) continue;
    const of = (v) => shared.filter(([k]) => other.get(k).verdict === v).length;
    console.log([bucket, source, shared.length, of('Aligned'), of('Inconclusive'),
      of('Misaligned')].join('\t'));
  }
}

console.log('\n== 2. recovery: where silencedetect reaches no verdict ==\n');
console.log(['bucket', 'source', 'n inconclusive', 'now Aligned', 'now Misaligned', 'still none'].join('\t'));
for (const bucket of buckets) {
  const base = at(bucket, 'silence', 0);
  for (const source of sources) {
    if (source === 'silence') continue;
    const other = at(bucket, source, 0);
    const shared = [...base.entries()].filter(([k, r]) => r.verdict === 'Inconclusive' && other.has(k));
    if (!shared.length) continue;
    const of = (v) => shared.filter(([k]) => other.get(k).verdict === v).length;
    console.log([bucket, source, shared.length, of('Aligned'), of('Misaligned'),
      of('Inconclusive')].join('\t'));
  }
}

console.log('\n== 3. drift: where silencedetect measures none ==\n');
console.log(['bucket', 'source', 'n null drift', 'drift gained', 'of those, |drift| > 500'].join('\t'));
for (const bucket of buckets) {
  const base = at(bucket, 'silence', 0);
  for (const source of sources) {
    if (source === 'silence') continue;
    const other = at(bucket, source, 0);
    const shared = [...base.entries()].filter(([k, r]) => (r.driftMs === null || r.driftMs === undefined) && other.has(k));
    if (!shared.length) continue;
    const gained = shared.filter(([k]) => other.get(k).driftMs !== null && other.get(k).driftMs !== undefined);
    const big = gained.filter(([k]) => Math.abs(other.get(k).driftMs) > ALIGNED_WITHIN);
    console.log([bucket, source, shared.length, gained.length, big.length].join('\t'));
  }
}

console.log('\n== 4. recall: a known 1500 ms displacement handed back ==\n');
console.log(['bucket', 'source', 'n scored both', 'returned it', 'wrong by >250ms', 'no verdict'].join('\t'));
for (const bucket of buckets) {
  for (const source of sources) {
    const zero = at(bucket, source, 0);
    const moved = at(bucket, source, 1500);
    if (!moved.size) continue;
    let good = 0; let bad = 0; let none = 0; let scored = 0;
    for (const [k, r] of moved) {
      const before = zero.get(k);
      if (!before || before.bestShiftMs === null || before.bestShiftMs === undefined) continue;
      scored += 1;
      if (r.bestShiftMs === null || r.bestShiftMs === undefined) { none += 1; continue; }
      const expected = before.bestShiftMs - 1500;
      if (Math.abs(r.bestShiftMs - expected) <= RECALL_TOLERANCE) good += 1; else bad += 1;
    }
    if (scored) console.log([bucket, source, scored, good, bad, none].join('\t'));
  }
}

console.log('\n== onset supply and speech share ==\n');
console.log(['bucket', 'source', 'median onsets', 'median hits/floor', 'median speech share'].join('\t'));
const median = (list) => {
  const s = list.filter((v) => v !== null && v !== undefined).sort((a, b) => a - b);
  return s.length ? Math.round(s[Math.floor(s.length / 2)] * 100) / 100 : '—';
};
for (const bucket of buckets) {
  for (const source of sources) {
    const found = [...at(bucket, source, 0).values()];
    if (!found.length) continue;
    console.log([bucket, source, median(found.map((r) => r.onsets)),
      median(found.map((r) => (r.floor ? r.hits / r.floor : null))),
      median(found.map((r) => r.speechShare))].join('\t'));
  }
}
