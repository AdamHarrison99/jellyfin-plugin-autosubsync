// Ground truth for the sweep's sample, from each video's own embedded track. Wraps
// `check-vs-embedded.ps1` — which is independent of both the sync engine and the audio check, so
// it can judge either — and emits one row per title for `verdicts.mjs` to join against.
//
//   node agentic/tools/vadcheck/truth.mjs --rows <sweep.json> [--out <json>] [--take N]
//
// ! A title with no embedded subtitle stream cannot be judged this way and is reported as such,
//   ¬silently dropped: the share of the sample that HAS ground truth is itself a result.

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const argv = process.argv.slice(2);
const value = (name, fallback = null) => {
  const at = argv.indexOf(name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
};

const rowsPath = value('--rows');
const outPath = value('--out');
const take = value('--take') ? Number(value('--take')) : null;

if (!rowsPath) {
  console.error('--rows <sweep.json> is required');
  process.exit(2);
}

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const script = path.join(here, '..', 'check-vs-embedded.ps1');

const seen = new Map();
for (const row of JSON.parse(fs.readFileSync(rowsPath, 'utf8'))) {
  if (row.appliedShiftMs !== 0) continue;
  const key = `${row.video}|${row.subtitle}`;
  if (!seen.has(key)) seen.set(key, row);
}

let pairs = [...seen.values()];
if (take) pairs = pairs.slice(0, take);

const results = [];
console.log(['title', 'truth', 'early ms', 'late ms', 'spread ms'].join('\t'));

for (const [index, row] of pairs.entries()) {
  process.stderr.write(`[${index + 1}/${pairs.length}] ${path.basename(row.subtitle)}\n`);

  let text = '';
  try {
    text = execFileSync('powershell', [
      '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script,
      '-Video', row.video, '-Subtitle', row.subtitle,
    ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    text = String(error.stdout || '');
  }

  const early = /early\s*:\s*embedded is off the external by ([+-]?\d+) ms/.exec(text)?.[1];
  const late = /late\s*:\s*embedded is off the external by ([+-]?\d+) ms/.exec(text)?.[1];
  const verdict = /VERDICT\s*:\s*(.+)/.exec(text)?.[1]?.trim() ?? 'no embedded track';

  const spread = early !== undefined && late !== undefined
    ? Math.abs(Number(late) - Number(early))
    : null;

  const entry = {
    video: row.video,
    subtitle: row.subtitle,
    itemName: row.itemName,
    bucket: row.bucket,
    truthVerdict: verdict,
    truthEarlyMs: early !== undefined ? Number(early) : null,
    truthLateMs: late !== undefined ? Number(late) : null,
    truthSpreadMs: spread,
  };

  results.push(entry);
  console.log([path.basename(row.subtitle).slice(0, 44), verdict.slice(0, 44),
    entry.truthEarlyMs ?? '—', entry.truthLateMs ?? '—', spread ?? '—'].join('\t'));
}

if (outPath) {
  fs.writeFileSync(outPath, JSON.stringify(results, null, 1));
  console.error(`wrote ${results.length} rows to ${outPath}`);
}

const measured = results.filter((r) => r.truthEarlyMs !== null).length;
console.error(`\nground truth available on ${measured} of ${results.length}`);
