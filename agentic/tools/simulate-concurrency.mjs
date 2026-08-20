#!/usr/bin/env node
// Convergence check for AdaptiveConcurrency's control law. See agentic/ARCHITECTURE.md.
// ! Mirrors the C#, does not bind it. Re-read both when either changes.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const SOURCE = join(
  dirname(fileURLToPath(import.meta.url)),
  '..', '..', 'Services', 'AdaptiveConcurrency.cs');

const SamplesPerLevel = 12;
const MeaningfulChange = 0.07;
const SettleBackOff = 1;

// Fails loudly if the C# constants move without this file moving with them.
function checkConstantsMatchSource() {
  let text;
  try {
    text = readFileSync(SOURCE, 'utf8');
  } catch {
    console.error(`could not read ${SOURCE}`);
    return false;
  }

  const expected = [
    ['SamplesPerLevel', String(SamplesPerLevel)],
    ['MeaningfulChange', String(MeaningfulChange)],
    ['SettleBackOff', String(SettleBackOff)]
  ];

  let ok = true;

  for (const [name, value] of expected) {
    const found = new RegExp(`${name}\\s*=\\s*([0-9.]+)`).exec(text);
    if (!found) {
      console.error(`FAIL  ${name} not found in AdaptiveConcurrency.cs`);
      ok = false;
    } else if (Number(found[1]) !== Number(value)) {
      console.error(`FAIL  ${name} is ${found[1]} in C# but ${value} here`);
      ok = false;
    }
  }

  return ok;
}

// ! Seeded on purpose: a flaky assertion cannot tell a regression from a bad draw.
function makeRandom(seed) {
  return function next() {
    seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function makeController(ceiling) {
  return {
    ceiling, level: 1, samples: 0, sum: 0, sumObserved: 0,
    step: 1, measuredLevel: 0, measuredThroughput: 0,
    probing: true, settledAt: null, peak: 1
  };
}

// observed is the concurrency the run actually saw, which a workload need not supply in full.
function report(c, msPerGb, observed) {
  if (!c.probing) { return; }

  c.samples++;
  c.sum += msPerGb;
  c.sumObserved += Math.min(observed, c.level);
  if (c.samples < SamplesPerLevel) { return; }

  const throughput = (c.sumObserved / c.samples) / (c.sum / c.samples);
  const prevLevel = c.measuredLevel;
  const prevThroughput = c.measuredThroughput;

  c.measuredLevel = c.level;
  c.measuredThroughput = throughput;
  c.samples = 0;
  c.sum = 0;
  c.sumObserved = 0;

  if (prevLevel === 0) { return move(c); }
  if (throughput > prevThroughput * (1 + MeaningfulChange)) { return move(c); }

  if (throughput < prevThroughput * (1 - MeaningfulChange)) {
    c.step = -c.step;
    return settle(c, prevLevel);
  }

  // Flat: the extra slot bought nothing, so keep the cheaper of the two.
  return settle(c, Math.min(c.level, prevLevel));
}

function move(c) {
  const next = Math.min(Math.max(c.level + c.step, 1), c.ceiling);
  if (next === c.level) { return settle(c, c.level); }
  c.level = next;
}

// ! The peak is recorded, the operating level sits SettleBackOff under it. Re-probing measures
//   from the peak, so a resettle cannot take another slot off each time.
function settle(c, level) {
  c.probing = false;
  c.measuredLevel = 0;
  c.measuredThroughput = 0;
  c.peak = level;
  c.level = Math.max(level - SettleBackOff, Math.min(level, 2));
  c.settledAt = c.level;
}

// Cost in ms per gigabyte at concurrency n, per storage profile. `achieved` is how much
// concurrency the caller actually supplies at a given permit level; it defaults to all of it.
// Bands not exact levels: a small mean lands one short near the ceiling, always downward.
const CASES = [
  {
    // A manual sync, or a lone new item: one job exists, so raising the permit changes nothing.
    name: 'Sequential caller',
    cost: () => 1000,
    achieved: () => 1,
    band: () => [1, 1],
    why: 'a caller that never runs two at once must not raise the level'
  },
  {
    // A caller that only ever offers two, on a box whose ceiling is higher.
    name: 'Caller capped at two',
    cost: () => 1000,
    achieved: (level) => Math.min(level, 2),
    band: (ceiling) => [1, Math.min(3, ceiling)],
    why: 'the level tracks the work offered, not the ceiling'
  },
  {
    // Tolerance widens above the shipped ceiling of 4: with perfect scaling the gain from the
    // nth slot is 1/n, so it closes on the decision margin and a small mean stops early.
    // ! Past the knee the ceiling stops mattering at all. 1/n falls under MeaningfulChange
    //   around n = 10, so the climb terminates there whether the ceiling is 16 or 64.
    name: 'CPU-bound, fast local disk',
    cost: () => 1000,
    band: (ceiling) => {
      const knee = Math.round(1 / MeaningfulChange) + 2;
      const top = Math.min(ceiling, knee);
      // Tight only while the ceiling is what binds. Approaching the knee the margin decides
      // instead, the spread widens, and SettleBackOff takes one more off whatever was found.
      return ceiling < knee - 2
        ? [Math.max(1, top - (ceiling > 4 ? 2 : 1)), top]
        : [6, top];
    },
    why: 'slots scale throughput until the marginal gain closes on the decision margin'
  },
  {
    name: 'Bandwidth saturated, NAS',
    cost: (n) => 1000 * n,
    band: (ceiling) => [1, Math.min(2, ceiling)],
    why: 'extra slots split fixed bandwidth and buy nothing'
  },
  {
    name: 'Spinning disk, seek thrash',
    cost: (n) => 1000 * Math.pow(n, 1.5),
    band: () => [1, 1],
    why: 'extra slots actively lose ground'
  },
  {
    name: 'Partial scaling',
    cost: (n) => 1000 * Math.pow(n, 0.4),
    band: (ceiling) => [Math.min(2, ceiling), ceiling],
    why: 'climbs while the gain clears the margin'
  }
];

const RUNS = 200;

const SEED = 20260811;

function main() {
  let failures = 0;

  if (!checkConstantsMatchSource()) {
    process.exit(1);
  }

  // 1-64 is the reachable domain: the ceiling is a flat MaxConcurrency, no core-count term.
  // ! 16, 32 and 64 exist to prove the climb still terminates on a plateau far below them.
  for (const ceiling of [1, 2, 3, 4, 6, 8, 16, 32, 64]) {
    console.log(`\nceiling ${ceiling}`);

    for (const testCase of CASES) {
      const [low, high] = testCase.band(ceiling);
      const settled = new Map();
      let worstSyncs = 0;

      const random = makeRandom(SEED);

      for (let run = 0; run < RUNS; run++) {
        const c = makeController(ceiling);
        let syncs = 0;

        while (c.settledAt === null && syncs < 1000) {
          const observed = testCase.achieved ? testCase.achieved(c.level) : c.level;

          // +/-10% jitter, so convergence cannot depend on noiseless samples.
          report(c, testCase.cost(observed) * (0.9 + random() * 0.2), observed);
          syncs++;
        }

        settled.set(c.settledAt, (settled.get(c.settledAt) ?? 0) + 1);
        worstSyncs = Math.max(worstSyncs, syncs);
      }

      const levels = [...settled.keys()];
      const inBand = levels.every((level) => level >= low && level <= high);
      const outcome = [...settled.entries()]
        .sort((a, b) => b[1] - a[1])
        .map(([level, count]) => `${level}x${count}`)
        .join(' ');

      if (!inBand) { failures++; }

      console.log(
        `  ${inBand ? 'ok  ' : 'FAIL'} ${testCase.name.padEnd(28)} ` +
        `want ${low}-${high}, got ${outcome} (<=${worstSyncs} syncs) — ${testCase.why}`);
    }
  }

  if (failures > 0) {
    console.error(`\nsimulate-concurrency: ${failures} case(s) did not converge as expected`);
    process.exit(1);
  }

  console.log('\nsimulate-concurrency: all cases converged');
}

main();
