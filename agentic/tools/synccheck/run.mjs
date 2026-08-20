// Measures each assy-cli engine against a subtitle whose correct timing is known.
//
// The reference file is the ground truth. Each case derives an input from it by a transformation
// whose inverse is exactly known, the engine is asked to undo that transformation, and the output
// is scored against the reference cue by cue. The "identity" case feeds the reference back
// unchanged: a correct engine returns it unchanged, and one that does not is destroying work.
//
// Usage:
//   node agentic/tools/synccheck/run.mjs --video <file> --truth <file.srt> [options]
//
//   --exe <path>       assy-cli executable. Defaults to the staged win-x64 payload.
//   --out <dir>        Where to write inputs and outputs. Defaults to a temp directory.
//   --engines a,b,c    Defaults to ffsubsync,alass,autosubsync. An entry may name a preset as
//                      "engine@preset" (see PRESETS), which is passed via --config-file.
//   --cases a,b,c      Defaults to every case below.
//   --timeout <min>    Per-run kill deadline. Defaults to 20, matching PerSyncTimeoutMinutes.
//   --ffmpeg <dir>     Prepended to PATH. Auto-detected from a Jellyfin install if omitted.
//   --json <file>      Also write the results as JSON.

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const HERE = dirname(fileURLToPath(import.meta.url));
const AGENTIC = resolve(HERE, '..', '..');

const DEFAULT_EXE = join(AGENTIC, 'payload', 'assy-cli', 'win-x64', 'assy-cli.exe');
const DEFAULT_ENGINES = ['ffsubsync', 'alass', 'autosubsync'];

const FFMPEG_PROBES = [
    'C:\\Program Files\\Jellyfin\\Server',
    'C:\\Program Files\\Jellyfin\\Server\\ffmpeg',
];

// Engine option sets, written to a JSON file and handed to assy-cli as --config-file. Keys are
// "<tool>_<option>" and are only emitted when they differ from upstream's own default.
const PRESETS = {
    // alass splits the subtitle into independently-offset segments; the penalty governs how
    // willingly. -1 is upstream's sentinel for --no-split.
    nosplit: { alass_split_penalty: -1 },
    strictsplit: { alass_split_penalty: 60 },

    // Both engines default check_video_for_subtitles to true, which aligns against a subtitle
    // track extracted from the video instead of its audio. The plugin forbids that outright.
    audioonly: { alass_check_video_for_subtitles: false },
    audioonly_nosplit: { alass_check_video_for_subtitles: false, alass_split_penalty: -1 },
    as_audioonly: { autosubsync_check_video_for_subtitles: false },
};

// Each case is a transformation applied to the ground truth to make the engine's input.
const CASES = {
    identity: { label: 'already correct', apply: (cues) => cues },
    'shift+5s': { label: 'late by 5s', apply: (cues) => shift(cues, 5000) },
    'shift-5s': { label: 'early by 5s', apply: (cues) => shift(cues, -5000) },
    'shift+30s': { label: 'late by 30s', apply: (cues) => shift(cues, 30000) },
    // 23.976 -> 25 fps, the classic PAL mismatch: a pure offset cannot fix it.
    stretch: { label: 'PAL 25/23.976 stretch', apply: (cues) => stretch(cues, 25 / 23.976) },
};

// ---- SRT ----------------------------------------------------------------

function parseTime(value) {
    const m = value.trim().match(/^(\d+):(\d{2}):(\d{2})[,.](\d{1,3})$/);
    if (!m) return null;
    return (+m[1] * 3600 + +m[2] * 60 + +m[3]) * 1000 + +m[4].padEnd(3, '0');
}

function formatTime(ms) {
    const clamped = Math.max(0, Math.round(ms));
    const h = Math.floor(clamped / 3600000);
    const min = Math.floor((clamped % 3600000) / 60000);
    const s = Math.floor((clamped % 60000) / 1000);
    const milli = clamped % 1000;
    const pad = (n, w) => String(n).padStart(w, '0');
    return `${pad(h, 2)}:${pad(min, 2)}:${pad(s, 2)},${pad(milli, 3)}`;
}

function parseSrt(text) {
    const cues = [];
    const blocks = text.replace(/^\uFEFF/, '').split(/\r?\n\r?\n+/);

    for (const block of blocks) {
        const lines = block.split(/\r?\n/).filter((l) => l.trim().length > 0);
        if (lines.length === 0) continue;

        const timingIndex = lines.findIndex((l) => l.includes('-->'));
        if (timingIndex < 0) continue;

        const [rawStart, rawEnd] = lines[timingIndex].split('-->');
        const start = parseTime(rawStart);
        const end = parseTime(rawEnd ?? '');
        if (start === null || end === null) continue;

        cues.push({ start, end, text: lines.slice(timingIndex + 1).join('\n') });
    }
    return cues;
}

function formatSrt(cues) {
    return cues
        .map((c, i) => `${i + 1}\n${formatTime(c.start)} --> ${formatTime(c.end)}\n${c.text}\n`)
        .join('\n');
}

const shift = (cues, ms) => cues.map((c) => ({ ...c, start: c.start + ms, end: c.end + ms }));
const stretch = (cues, f) => cues.map((c) => ({ ...c, start: c.start * f, end: c.end * f }));

// ---- Scoring ------------------------------------------------------------

function quantile(sorted, q) {
    if (sorted.length === 0) return NaN;
    const pos = (sorted.length - 1) * q;
    const lo = Math.floor(pos);
    const hi = Math.ceil(pos);
    return lo === hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

// ! Cues are matched by position. The engines retime, they never insert or drop, so a count
//   mismatch is itself a defect and is reported rather than realigned around.
function score(truth, actual) {
    const n = Math.min(truth.length, actual.length);
    const errors = [];
    for (let i = 0; i < n; i++) errors.push(Math.abs(actual[i].start - truth[i].start));

    const sorted = [...errors].sort((a, b) => a - b);
    const within = (ms) => (errors.filter((e) => e <= ms).length / (errors.length || 1)) * 100;

    // The pathology that ruins a file while every per-cue error still looks survivable: distinct
    // scenes welded together because a segment boundary landed inside a real gap.
    let collapsed = 0;
    for (let i = 1; i < n; i++) {
        const truthGap = truth[i].start - truth[i - 1].end;
        const actualGap = actual[i].start - actual[i - 1].end;
        if (truthGap > 1000 && actualGap <= 0) collapsed++;
    }

    let nonMonotonic = 0;
    for (let i = 1; i < actual.length; i++) {
        if (actual[i].start < actual[i - 1].start) nonMonotonic++;
    }

    return {
        matched: n,
        cueCountDelta: actual.length - truth.length,
        medianMs: quantile(sorted, 0.5),
        p90Ms: quantile(sorted, 0.9),
        maxMs: sorted.length ? sorted[sorted.length - 1] : NaN,
        within50: within(50),
        within150: within(150),
        within500: within(500),
        collapsed,
        nonMonotonic,
    };
}

// ---- Running assy-cli ---------------------------------------------------

function findFfmpegDir(explicit) {
    if (explicit) return explicit;
    for (const dir of FFMPEG_PROBES) {
        if (existsSync(join(dir, 'ffmpeg.exe'))) return dir;
    }
    return null;
}

function runEngine({ exe, video, input, output, engine, timeoutMs, ffmpegDir, configPath }) {
    return new Promise((done) => {
        const args = ['--no-color'];
        if (configPath) args.push('--config-file', configPath);
        args.push(
            'sync', video, input,
            '-o', output,
            '-t', engine,
            '--json', '--encoding', 'same_as_input', '--no-prefix',
        );

        const env = { ...process.env };
        if (ffmpegDir) env.PATH = `${ffmpegDir};${env.PATH ?? ''}`;

        const started = Date.now();
        const child = spawn(exe, args, { env, windowsHide: true });

        let stdout = '';
        let stderr = '';
        let timedOut = false;

        const timer = setTimeout(() => {
            timedOut = true;
            child.kill('SIGKILL');
        }, timeoutMs);

        child.stdout.on('data', (d) => { stdout += d; });
        child.stderr.on('data', (d) => { stderr += d; });

        child.on('error', (err) => {
            clearTimeout(timer);
            done({ ok: false, timedOut, elapsedMs: Date.now() - started, message: err.message });
        });

        child.on('close', (code) => {
            clearTimeout(timer);
            let parsed = null;
            for (const line of stdout.split('\n').map((l) => l.trim()).reverse()) {
                if (!line.startsWith('{')) continue;
                try {
                    const candidate = JSON.parse(line);
                    if ('ok' in candidate) { parsed = candidate; break; }
                } catch { /* not JSON */ }
            }
            done({
                ok: code === 0 && !timedOut && parsed?.ok === true,
                timedOut,
                exitCode: code,
                elapsedMs: Date.now() - started,
                message: parsed?.message ?? stderr.trim().split('\n').slice(-3).join(' '),
            });
        });
    });
}

// ---- Reporting ----------------------------------------------------------

const fmtMs = (v) => (Number.isFinite(v) ? `${Math.round(v)}` : '-');
const fmtPct = (v) => (Number.isFinite(v) ? `${v.toFixed(1)}%` : '-');

function table(rows, columns) {
    const widths = columns.map((c) =>
        Math.max(c.header.length, ...rows.map((r) => String(c.value(r)).length)));
    const line = (cells) => cells.map((c, i) => String(c).padEnd(widths[i])).join('  ');

    console.log(line(columns.map((c) => c.header)));
    console.log(widths.map((w) => '-'.repeat(w)).join('  '));
    for (const row of rows) console.log(line(columns.map((c) => c.value(row))));
}

// ---- Main ---------------------------------------------------------------

function parseArgs(argv) {
    const out = {};
    for (let i = 0; i < argv.length; i += 2) out[argv[i].replace(/^--/, '')] = argv[i + 1];
    return out;
}

const opts = parseArgs(process.argv.slice(2));

if (!opts.video || !opts.truth) {
    console.error('Required: --video <file> --truth <file.srt>');
    process.exit(2);
}

const exe = opts.exe ?? DEFAULT_EXE;
if (!existsSync(exe)) {
    console.error(`assy-cli not found: ${exe}`);
    process.exit(2);
}

const outDir = opts.out ?? join(tmpdir(), 'synccheck');
mkdirSync(outDir, { recursive: true });

// "alass@nosplit" -> run alass with the nosplit preset, reported under that name.
const engines = (opts.engines ?? DEFAULT_ENGINES.join(',')).split(',').map((spec) => {
    const [engine, preset] = spec.split('@');
    if (preset && !PRESETS[preset]) {
        console.error(`Unknown preset: ${preset}. Known: ${Object.keys(PRESETS).join(', ')}`);
        process.exit(2);
    }
    return { spec, engine, preset };
});
const caseNames = (opts.cases ?? Object.keys(CASES).join(',')).split(',');
const timeoutMs = (Number(opts.timeout ?? 20)) * 60_000;
const ffmpegDir = findFfmpegDir(opts.ffmpeg);

const truth = parseSrt(readFileSync(opts.truth, 'utf8'));
if (truth.length === 0) {
    console.error(`No cues parsed from ${opts.truth}`);
    process.exit(2);
}

console.log(`reference : ${opts.truth} (${truth.length} cues)`);
console.log(`video     : ${opts.video}`);
console.log(`assy-cli  : ${exe}`);
console.log(`ffmpeg    : ${ffmpegDir ?? '(relying on PATH)'}`);
console.log(`engines   : ${engines.map((e) => e.spec).join(', ')}`);
console.log(`cases     : ${caseNames.join(', ')}`);
console.log(`timeout   : ${timeoutMs / 60000} min/run\n`);

const results = [];

for (const caseName of caseNames) {
    const testCase = CASES[caseName];
    if (!testCase) {
        console.error(`Unknown case: ${caseName}`);
        process.exit(2);
    }

    const inputPath = join(outDir, `input-${caseName.replace(/[^\w+-]/g, '_')}.srt`);
    writeFileSync(inputPath, formatSrt(testCase.apply(truth)), 'utf8');

    for (const { spec, engine, preset } of engines) {
        const safe = `${caseName}-${spec}`.replace(/[^\w+-]/g, '_');
        const outputPath = join(outDir, `out-${safe}.srt`);
        process.stdout.write(`  ${caseName.padEnd(10)} ${spec.padEnd(18)} ... `);

        let configPath = null;
        if (preset) {
            configPath = join(outDir, `config-${preset}.json`);
            writeFileSync(configPath, JSON.stringify(PRESETS[preset], null, 2), 'utf8');
        }

        const run = await runEngine({
            exe, video: opts.video, input: inputPath, output: outputPath,
            engine, timeoutMs, ffmpegDir, configPath,
        });

        const row = { case: caseName, engine: spec, ...run };

        if (run.ok && existsSync(outputPath)) {
            Object.assign(row, score(truth, parseSrt(readFileSync(outputPath, 'utf8'))));
            console.log(`median ${fmtMs(row.medianMs)}ms  max ${fmtMs(row.maxMs)}ms  ${(run.elapsedMs / 1000).toFixed(0)}s`);
        } else {
            console.log(run.timedOut ? 'TIMED OUT' : `FAILED (${run.message ?? 'no message'})`);
        }

        results.push(row);
    }
}

console.log('\n=== Start-time error against the reference ===\n');

table(results, [
    { header: 'case', value: (r) => r.case },
    { header: 'engine', value: (r) => r.engine },
    { header: 'status', value: (r) => (r.ok ? 'ok' : r.timedOut ? 'timeout' : 'failed') },
    { header: 'median', value: (r) => fmtMs(r.medianMs) },
    { header: 'p90', value: (r) => fmtMs(r.p90Ms) },
    { header: 'max', value: (r) => fmtMs(r.maxMs) },
    { header: '<=150ms', value: (r) => fmtPct(r.within150) },
    { header: '<=500ms', value: (r) => fmtPct(r.within500) },
    { header: 'collapsed', value: (r) => r.collapsed ?? '-' },
    { header: 'dCues', value: (r) => r.cueCountDelta ?? '-' },
    { header: 'sec', value: (r) => (r.elapsedMs / 1000).toFixed(0) },
]);

console.log('\nmedian/p90/max are milliseconds of absolute start-time error.');
console.log('collapsed = real gaps (>1s) welded shut in the output.');

if (opts.json) {
    writeFileSync(opts.json, JSON.stringify({ truth: opts.truth, video: opts.video, results }, null, 2));
    console.log(`\nWrote ${opts.json}`);
}
