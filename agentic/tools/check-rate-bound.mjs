#!/usr/bin/env node
// Does MaximumRateDrift admit every framerate conversion a subtitle can legitimately need?
// ! Reads the constant from the C#, so the two cannot drift apart.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const SOURCE = join(
  dirname(fileURLToPath(import.meta.url)),
  '..', '..', 'Services', 'SyncOrchestrator.cs');

// Exact rationals. 23.976 is 24000/1001 and 29.97 is 30000/1001; the decimals are nicknames.
const RATES = {
  '23.976': 24000 / 1001,
  '24': 24,
  '25': 25,
  '29.97': 30000 / 1001,
  '30': 30
};

// Rescales no framerate explains. From the eleventh pass's boundary table in AUDIT.md.
const ADVERSARIAL = [
  ['rate blowup, first cue pinned', 2.0101],
  ['rate collapse, half length', 0.4949]
];

function readBound() {
  const text = readFileSync(SOURCE, 'utf8');
  const found = /MaximumRateDrift\s*=\s*([0-9.]+)/.exec(text);

  if (!found) {
    console.error('FAIL  MaximumRateDrift not found in SyncOrchestrator.cs');
    return null;
  }

  return Number(found[1]);
}

function conversions() {
  const pairs = [];

  for (const from of Object.keys(RATES)) {
    for (const to of Object.keys(RATES)) {
      if (from !== to) {
        pairs.push({ from, to, ratio: RATES[to] / RATES[from] });
      }
    }
  }

  return pairs.sort((a, b) => Math.abs(b.ratio - 1) - Math.abs(a.ratio - 1));
}

function main() {
  const bound = readBound();
  if (bound === null) {
    process.exit(1);
  }

  const admits = (ratio) => Math.abs(ratio - 1) <= bound;
  const pairs = conversions();

  console.log(`MaximumRateDrift = ${bound}\n`);
  console.log('  widest legitimate conversions');

  for (const { from, to, ratio } of pairs.slice(0, 5)) {
    console.log(
      `    ${admits(ratio) ? 'admit ' : 'REJECT'} ${from} -> ${to}`.padEnd(30) +
      `ratio ${ratio.toFixed(6)}  drift ${(Math.abs(ratio - 1) * 100).toFixed(3)}%`);
  }

  const refused = pairs.filter((p) => !admits(p.ratio));
  const leaked = ADVERSARIAL.filter(([, ratio]) => admits(ratio));

  // ! A drift landing exactly on the bound is decided by rounding, since the ratio is
  //   computed from integer-millisecond spans. Treat it as a failure, not a pass.
  const onTheLine = pairs.filter((p) => Math.abs(Math.abs(p.ratio - 1) - bound) < 1e-9);

  console.log('');
  let failures = 0;

  for (const [label, values, detail] of [
    ['legitimate conversions refused', refused, (p) => `${p.from}->${p.to}`],
    ['conversions decided by rounding', onTheLine, (p) => `${p.from}->${p.to}`],
    ['adversarial rescales admitted', leaked, (a) => a[0]]
  ]) {
    const ok = values.length === 0;
    if (!ok) { failures++; }

    console.log(
      `  ${ok ? 'ok  ' : 'FAIL'} ${label.padEnd(34)} ` +
      (ok ? 'none' : values.map(detail).join(', ')));
  }

  if (failures > 0) {
    console.error(`\ncheck-rate-bound: ${failures} check(s) failed`);
    process.exit(1);
  }

  console.log('\ncheck-rate-bound: the bound admits every conversion and rejects every rescale');
}

main();
