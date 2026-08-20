// Compares two sweeps of the SAME titles planned with different window rules — the O3 question,
// which analyse.mjs cannot answer ∵ it groups by onset source within one plan, not across plans.
//
//   node agentic/tools/vadcheck/window-arms.mjs --base <a.json> --arm <b.json> [--source silence]
//
// ! The safety column is the one that decides O3. Unlike the VAD fallback, a window-rule change is
//   ¬write-monotone: a title that verifies today can be newly refused if the extra windows produce
//   a drift reading the four-window plan never took. On a known-good population that is a break.

import fs from 'node:fs';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const basePath = value('--base');
const armPath = value('--arm');
const only = value('--source');
const ALIGNED_WITHIN = 500;

if (!basePath || !armPath) {
  console.error('--base <json> and --arm <json> are required');
  process.exit(2);
}

const load = (file) => {
  const found = new Map();
  for (const row of JSON.parse(fs.readFileSync(file, 'utf8'))) {
    if (row.appliedShiftMs !== 0) continue;
    if (only && row.source !== only) continue;
    found.set(`${row.source}|${row.itemName}|${row.targetKey ?? row.subtitle}`, row);
  }
  return found;
};

const base = load(basePath);
const arm = load(armPath);
const sources = [...new Set([...base.values()].map((r) => r.source))];

const has = (v) => v !== null && v !== undefined;

console.log('\n== window plan ==\n');
console.log(['source', 'n paired', 'base windows', 'arm windows', 'gained windows'].join('\t'));
for (const source of sources) {
  const pairs = [...base.entries()].filter(([k]) => arm.has(k) && k.startsWith(`${source}|`));
  if (!pairs.length) continue;
  const med = (list) => {
    const s = list.slice().sort((a, b) => a - b);
    return s.length ? s[Math.floor(s.length / 2)] : '—';
  };
  const gained = pairs.filter(([k, r]) => arm.get(k).windows > r.windows).length;
  console.log([source, pairs.length, med(pairs.map(([, r]) => r.windows)),
    med(pairs.map(([k]) => arm.get(k).windows)), gained].join('\t'));
}

console.log('\n== drift: the point of the change ==\n');
console.log(['source', 'n', 'base measured', 'arm measured', 'GAINED', 'of those >500ms'].join('\t'));
for (const source of sources) {
  const pairs = [...base.entries()].filter(([k]) => arm.has(k) && k.startsWith(`${source}|`));
  if (!pairs.length) continue;
  const gained = pairs.filter(([k, r]) => !has(r.driftMs) && has(arm.get(k).driftMs));
  console.log([source, pairs.length,
    pairs.filter(([, r]) => has(r.driftMs)).length,
    pairs.filter(([k]) => has(arm.get(k).driftMs)).length,
    gained.length,
    gained.filter(([k]) => Math.abs(arm.get(k).driftMs) > ALIGNED_WITHIN).length].join('\t'));
}

console.log('\n== verdict movement ==\n');
console.log(['source', 'held', 'Inconcl→verdict', 'verdict→Inconcl', 'Aligned→MISALIGNED'].join('\t'));
for (const source of sources) {
  const pairs = [...base.entries()].filter(([k]) => arm.has(k) && k.startsWith(`${source}|`));
  if (!pairs.length) continue;
  let held = 0; let gainedV = 0; let lost = 0; let broke = 0;
  for (const [k, before] of pairs) {
    const after = arm.get(k).verdict;
    if (before.verdict === after) { held += 1; continue; }
    if (before.verdict === 'Inconclusive') gainedV += 1;
    else if (after === 'Inconclusive') lost += 1;
    if (before.verdict === 'Aligned' && after === 'Misaligned') broke += 1;
  }
  console.log([source, held, gainedV, lost, broke].join('\t'));
}

// ! An Aligned title carrying a large drift reading is refused by the drifting branch before the
//   stretch guard is ever reached → on a known-good population this is the regression to count.
console.log('\n== SAFETY: new drift >500ms on a title that verified before ==\n');
console.log(['source', 'n was Aligned', 'still fine', 'NEW DRIFT REFUSAL'].join('\t'));
for (const source of sources) {
  const pairs = [...base.entries()]
    .filter(([k, r]) => arm.has(k) && k.startsWith(`${source}|`) && r.verdict === 'Aligned');
  if (!pairs.length) continue;
  const broke = pairs.filter(([k]) => {
    const a = arm.get(k);
    return (has(a.driftMs) && Math.abs(a.driftMs) > ALIGNED_WITHIN) || a.verdict === 'Misaligned';
  });
  console.log([source, pairs.length, pairs.length - broke.length, broke.length].join('\t'));
  for (const [, r] of broke) console.log(`      ! ${r.itemName}`);
}

console.log('\n== recall, both arms (known 1500 ms displacement) ==\n');
console.log(['source', 'arm', 'n', 'returned it', 'wrong >250ms', 'no verdict'].join('\t'));
for (const [label, file] of [['base', basePath], ['arm', armPath]]) {
  const zero = load(file);
  const moved = new Map();
  for (const row of JSON.parse(fs.readFileSync(file, 'utf8'))) {
    if (row.appliedShiftMs !== 1500) continue;
    if (only && row.source !== only) continue;
    moved.set(`${row.source}|${row.itemName}|${row.targetKey ?? row.subtitle}`, row);
  }
  for (const source of sources) {
    let good = 0; let bad = 0; let none = 0; let scored = 0;
    for (const [k, r] of moved) {
      if (!k.startsWith(`${source}|`)) continue;
      const before = zero.get(k);
      if (!before || !has(before.bestShiftMs)) continue;
      scored += 1;
      if (!has(r.bestShiftMs)) { none += 1; continue; }
      if (Math.abs(r.bestShiftMs - (before.bestShiftMs - 1500)) <= 250) good += 1; else bad += 1;
    }
    if (scored) console.log([source, label, scored, good, bad, none].join('\t'));
  }
}
