// How far ahead of the speech does each cue appear? Measured off the audio, not off the engine,
// so it can disagree with a sync the engine calls perfect.
//
//   node agentic/tools/check-cue-lead.mjs --video <path> --subtitle <path> [--subtitle <path>...]
//
// Subtitles are conventionally shown a little before the line is spoken. A median lead near zero
// means the cue lands on the speech; a large positive median means the subtitle runs early.

import { existsSync } from 'node:fs';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const ffmpeg = join(here, 'ffmpeg', 'ffmpeg.exe');

function args(name) {
  const out = [];
  for (let i = 0; i < process.argv.length; i++) {
    if (process.argv[i] === `--${name}` && process.argv[i + 1]) out.push(process.argv[i + 1]);
  }
  return out;
}

const video = args('video')[0];
const subtitles = args('subtitle');
const noise = args('noise')[0] || '-30dB';

if (!video || subtitles.length === 0) {
  console.error('usage: --video <path> --subtitle <path> [--subtitle <path>...] [--noise -30dB]');
  process.exit(2);
}

// silence_end is the instant sound resumes, which is the best cheap proxy for a speech onset.
function speechOnsets(path) {
  // silencedetect reports on stderr, so the result has to be read off the spawn, not stdout.
  const run = spawnSync(
    ffmpeg,
    // ! -vn and an explicit audio map. Without them ffmpeg decodes the video to read the audio,
    //   which takes minutes on a 1080p HEVC film.
    // ! The downmix has to be inside the filter graph. As an output option it lands after
    //   silencedetect, which then reads 5.1 and reports silence only where every channel is quiet.
    ['-hide_banner', '-nostats', '-vn', '-i', path, '-map', '0:a:0',
     '-af', `aformat=channel_layouts=mono,silencedetect=noise=${noise}:d=0.35`, '-f', 'null', '-'],
    { encoding: 'utf8', maxBuffer: 1 << 28 }
  );
  const text = run.stderr || '';
  return [...text.matchAll(/silence_end:\s*([\d.]+)/g)].map((m) => Math.round(Number(m[1]) * 1000));
}

const TIME = /(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->/;

function cueStarts(path) {
  const text = readFileSync(path, 'latin1').replace(/\r\n/g, '\n');
  const out = [];
  for (const line of text.split('\n')) {
    const m = TIME.exec(line);
    if (m) out.push(((+m[1] * 60 + +m[2]) * 60 + +m[3]) * 1000 + +m[4]);
  }
  return out;
}

function median(values) {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

// Nearest-onset matching re-anchors once a file moves, so two files cannot be compared that way.
// Sweeping a shift and scoring the whole subtitle at each one is immune to that.
const TOLERANCE = 250;
const SWEEP = 4000;
const STEP = 25;

function score(onsetSet, starts, shift) {
  let hits = 0;
  for (const start of starts) {
    const t = start + shift;
    for (let d = -TOLERANCE; d <= TOLERANCE; d += 50) {
      if (onsetSet.has(Math.round((t + d) / 50) * 50)) {
        hits++;
        break;
      }
    }
  }
  return hits;
}

function bestShift(onsets, starts) {
  const onsetSet = new Set(onsets.map((o) => Math.round(o / 50) * 50));
  let best = { shift: 0, hits: -1 };
  const curve = [];

  for (let shift = -SWEEP; shift <= SWEEP; shift += STEP) {
    const hits = score(onsetSet, starts, shift);
    curve.push({ shift, hits });
    if (hits > best.hits) best = { shift, hits };
  }

  const atZero = curve.find((p) => p.shift === 0).hits;
  return { ...best, atZero, total: starts.length };
}

console.log(`video   : ${basename(video)}`);
const onsets = speechOnsets(video);
console.log(`onsets  : ${onsets.length} speech onsets detected at ${noise}\n`);

for (const subtitle of subtitles) {
  if (!existsSync(subtitle)) {
    console.log(`${basename(subtitle)}: not found\n`);
    continue;
  }

  const starts = cueStarts(subtitle);
  const fit = bestShift(onsets, starts);

  console.log(basename(subtitle));
  console.log(`  cues ${fit.total}`);
  console.log(`  on speech as it stands : ${fit.atZero} cues`);
  console.log(`  best at ${fit.shift >= 0 ? '+' : ''}${fit.shift}ms : ${fit.hits} cues`);
  console.log(
    `  => ${Math.abs(fit.shift) <= 100
      ? 'aligned'
      : `wants to move ${fit.shift > 0 ? 'LATER' : 'EARLIER'} by ${Math.abs(fit.shift)}ms`}\n`
  );
}
