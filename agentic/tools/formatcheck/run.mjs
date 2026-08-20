// Establishes which subtitle formats, audio codecs and containers each assy-cli engine actually
// accepts, and whether the answer it gives on them is still correct.
//
// synccheck asks "how accurate is this engine on this title". This asks the prior question: does
// the engine run at all once the inputs stop being an SRT beside an H.264/AAC MKV. A format the
// engine rejects is a gap in the chain; a format it accepts and then answers wrongly on is worse,
// because nothing downstream can tell the difference.
//
// Every run applies the same known shift and scores the recovered timing against the truth, so a
// silent wrong answer shows up as a large error rather than as a pass.
//
// Usage:
//   node agentic/tools/formatcheck/run.mjs --truth <file.srt> --video <file>
//   node agentic/tools/formatcheck/run.mjs --truth <file.srt> --media <dir>
//
//   --formats srt,ass,ssa,vtt,sub   Subtitle formats to write the input in. Default: all.
//   --engines a,b,c                 Default: ffsubsync,alass,autosubsync.
//   --shift <ms>                    Offset the engine has to undo. Default 30000.
//   --fps <n>                       Frame rate for MicroDVD. Default 23.976.
//   --timeout <min>                 Per-run kill deadline. Default 10.
//   --out <dir>                     Working directory. Defaults to a temp directory.
//   --ffmpeg <dir>                  Prepended to PATH. Auto-detected from Jellyfin if omitted.
//   --json <file>                   Also write the results as JSON.

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const HERE = dirname(fileURLToPath(import.meta.url));
const AGENTIC = resolve(HERE, '..', '..');

const DEFAULT_EXE = join(AGENTIC, 'payload', 'assy-cli', 'win-x64', 'assy-cli.exe');
const DEFAULT_ENGINES = ['ffsubsync', 'alass', 'autosubsync'];
const VIDEO_EXTENSIONS = new Set([
    '.mkv', '.mp4', '.mov', '.ts', '.m2ts', '.avi', '.wmv', '.flv', '.webm', '.ogv', '.m4v',
]);

const FFMPEG_PROBES = [
    'C:\\Program Files\\Jellyfin\\Server',
    'C:\\Program Files\\Jellyfin\\Server\\ffmpeg',
];

// The same pins the plugin writes into its own assy config, so a result here means the same thing
// it would mean in production.
const ENGINE_CONFIG = {
    alass_check_video_for_subtitles: false,
    autosubsync_check_video_for_subtitles: false,
    add_tool_prefix: false,
    backup_subtitles_before_overwriting: false,
    keep_extracted_subtitles: false,
    keep_converted_subtitles: false,
    skip_previously_processed_videos: false,
    check_updates_startup: false,
    keep_log_records: false,
};

// ---- Time ---------------------------------------------------------------

const pad = (n, w) => String(n).padStart(w, '0');

function splitMs(ms) {
    const v = Math.max(0, Math.round(ms));
    return {
        h: Math.floor(v / 3600000),
        m: Math.floor((v % 3600000) / 60000),
        s: Math.floor((v % 60000) / 1000),
        ms: v % 1000,
    };
}

const toSrtTime = (ms) => {
    const t = splitMs(ms);
    return `${pad(t.h, 2)}:${pad(t.m, 2)}:${pad(t.s, 2)},${pad(t.ms, 3)}`;
};
const toVttTime = (ms) => toSrtTime(ms).replace(',', '.');
const toAssTime = (ms) => {
    const t = splitMs(ms);
    return `${t.h}:${pad(t.m, 2)}:${pad(t.s, 2)}.${pad(Math.floor(t.ms / 10), 2)}`;
};

function fromClock(h, m, s, frac) {
    const digits = frac.length === 2 ? +frac * 10 : +frac.padEnd(3, '0');
    return ((+h * 60 + +m) * 60 + +s) * 1000 + digits;
}

// ---- Format readers and writers -----------------------------------------

// Every writer takes cues and returns the file text; every reader takes the text and returns cues.
// A format is only listed here once both directions exist, because a result that cannot be read
// back cannot be scored and would otherwise pass by default.
const FORMATS = {
    srt: {
        extension: '.srt',
        write: (cues) => cues
            .map((c, i) => `${i + 1}\n${toSrtTime(c.start)} --> ${toSrtTime(c.end)}\n${c.text}\n`)
            .join('\n'),
        read: (text) => readCueBlocks(text, /(\d+):(\d{2}):(\d{2})[,.](\d{1,3})/g),
    },

    vtt: {
        extension: '.vtt',
        write: (cues) => `WEBVTT\n\n${cues
            .map((c, i) => `${i + 1}\n${toVttTime(c.start)} --> ${toVttTime(c.end)}\n${c.text}\n`)
            .join('\n')}`,
        read: (text) => readCueBlocks(text, /(\d+):(\d{2}):(\d{2})[,.](\d{1,3})/g),
    },

    ass: { extension: '.ass', write: (cues) => writeAss(cues, true), read: readAss },
    ssa: { extension: '.ssa', write: (cues) => writeAss(cues, false), read: readAss },

    sub: {
        extension: '.sub',
        write: (cues, { fps }) => `{1}{1}${fps}\n${cues
            .map((c) => `{${Math.round((c.start / 1000) * fps)}}{${Math.round((c.end / 1000) * fps)}}`
                + `${c.text.replace(/\n/g, '|')}`)
            .join('\n')}\n`,
        read: (text, { fps }) => {
            const cues = [];
            for (const line of text.split(/\r?\n/)) {
                const m = line.match(/^\{(\d+)\}\{(\d+)\}(.*)$/);
                if (!m) continue;
                // ! The declared-fps header is itself a {1}{1} cue and must not be scored.
                if (m[1] === '1' && m[2] === '1' && /^[\d.]+$/.test(m[3])) continue;
                cues.push({
                    start: (+m[1] / fps) * 1000,
                    end: (+m[2] / fps) * 1000,
                    text: m[3].replace(/\|/g, '\n'),
                });
            }
            return cues;
        },
    },
};

function readCueBlocks(text, timeRegex) {
    const cues = [];
    for (const block of text.replace(/^\uFEFF/, '').split(/\r?\n\r?\n+/)) {
        const lines = block.split(/\r?\n/).filter((l) => l.trim().length > 0);
        const i = lines.findIndex((l) => l.includes('-->'));
        if (i < 0) continue;

        const stamps = [...lines[i].matchAll(new RegExp(timeRegex.source, 'g'))];
        if (stamps.length < 2) continue;

        cues.push({
            start: fromClock(stamps[0][1], stamps[0][2], stamps[0][3], stamps[0][4]),
            end: fromClock(stamps[1][1], stamps[1][2], stamps[1][3], stamps[1][4]),
            text: lines.slice(i + 1).join('\n'),
        });
    }
    return cues;
}

function writeAss(cues, advanced) {
    const header = advanced
        ? '[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nPlayResX: 1920\nPlayResY: 1080\n\n'
          + '[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, '
          + 'OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, '
          + 'Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, '
          + 'Encoding\nStyle: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,'
          + '0,100,100,0,0,1,2,0,2,10,10,10,1\n\n'
          + '[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, '
          + 'Text\n'
        : '[Script Info]\nScriptType: v4.00\n\n'
          + '[V4 Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, '
          + 'TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, '
          + 'MarginL, MarginR, MarginV, AlphaLevel, Encoding\n'
          + 'Style: Default,Arial,48,16777215,255,0,0,0,0,1,2,0,2,10,10,10,0,1\n\n'
          + '[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, '
          + 'Text\n';

    const lead = advanced ? '0' : 'Marked=0';
    const body = cues
        .map((c) => `Dialogue: ${lead},${toAssTime(c.start)},${toAssTime(c.end)},Default,,0,0,0,,`
            + `${c.text.replace(/\n/g, '\\N')}`)
        .join('\n');

    return `${header}${body}\n`;
}

function readAss(text) {
    const cues = [];
    for (const line of text.split(/\r?\n/)) {
        if (!line.startsWith('Dialogue:')) continue;
        const fields = line.slice('Dialogue:'.length).split(',');
        const stamps = fields.filter((f) => /^\d+:\d{2}:\d{2}\.\d{2}$/.test(f.trim()));
        if (stamps.length < 2) continue;

        const parse = (v) => {
            const m = v.trim().match(/^(\d+):(\d{2}):(\d{2})\.(\d{2})$/);
            return fromClock(m[1], m[2], m[3], m[4]);
        };
        cues.push({ start: parse(stamps[0]), end: parse(stamps[1]), text: '' });
    }
    return cues;
}

// ---- Scoring ------------------------------------------------------------

function quantile(sorted, q) {
    if (sorted.length === 0) return NaN;
    const pos = (sorted.length - 1) * q;
    const lo = Math.floor(pos);
    const hi = Math.ceil(pos);
    return lo === hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
}

function score(truth, actual) {
    const n = Math.min(truth.length, actual.length);
    const errors = [];
    for (let i = 0; i < n; i++) errors.push(Math.abs(actual[i].start - truth[i].start));

    const sorted = [...errors].sort((a, b) => a - b);
    return {
        matched: n,
        cueCountDelta: actual.length - truth.length,
        medianMs: quantile(sorted, 0.5),
        maxMs: sorted.length ? sorted[sorted.length - 1] : NaN,
        within500: (errors.filter((e) => e <= 500).length / (errors.length || 1)) * 100,
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
        const args = [
            '--no-color', '--config-file', configPath,
            'sync', video, input, '-o', output, '-t', engine,
            '--json', '--encoding', 'same_as_input', '--no-prefix',
        ];

        const env = { ...process.env };
        if (ffmpegDir) env.PATH = `${ffmpegDir};${env.PATH ?? ''}`;

        const started = Date.now();
        const child = spawn(exe, args, { env, windowsHide: true });

        let stdout = '';
        let stderr = '';
        let timedOut = false;

        const timer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, timeoutMs);

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
                message: parsed?.message ?? stderr.trim().split('\n').slice(-2).join(' '),
            });
        });
    });
}

// ---- Reporting ----------------------------------------------------------

const fmtMs = (v) => (Number.isFinite(v) ? `${Math.round(v)}` : '-');

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

if (!opts.truth || (!opts.video && !opts.media)) {
    console.error('Required: --truth <file.srt> and one of --video <file> / --media <dir>');
    process.exit(2);
}

const exe = opts.exe ?? DEFAULT_EXE;
if (!existsSync(exe)) {
    console.error(`assy-cli not found: ${exe}`);
    process.exit(2);
}

const videos = opts.media
    ? readdirSync(opts.media)
        .filter((f) => VIDEO_EXTENSIONS.has(extname(f).toLowerCase()))
        .map((f) => join(opts.media, f))
        .filter((f) => statSync(f).isFile())
        .sort()
    : [opts.video];

if (videos.length === 0) {
    console.error(`No video files under ${opts.media}`);
    process.exit(2);
}

const formats = (opts.formats ?? Object.keys(FORMATS).join(',')).split(',');
for (const f of formats) {
    if (!FORMATS[f]) {
        console.error(`Unknown format: ${f}. Known: ${Object.keys(FORMATS).join(', ')}`);
        process.exit(2);
    }
}

const engines = (opts.engines ?? DEFAULT_ENGINES.join(',')).split(',');
const shiftMs = Number(opts.shift ?? 30000);
const fps = Number(opts.fps ?? 23.976);
const timeoutMs = Number(opts.timeout ?? 10) * 60_000;
const outDir = opts.out ?? join(tmpdir(), 'formatcheck');
const ffmpegDir = findFfmpegDir(opts.ffmpeg);

mkdirSync(outDir, { recursive: true });

const truth = readCueBlocks(readFileSync(opts.truth, 'utf8'), /(\d+):(\d{2}):(\d{2})[,.](\d{1,3})/g);
if (truth.length === 0) {
    console.error(`No cues parsed from ${opts.truth}`);
    process.exit(2);
}

const configPath = join(outDir, 'engine-config.json');
writeFileSync(configPath, JSON.stringify(ENGINE_CONFIG, null, 2), 'utf8');

console.log(`reference : ${opts.truth} (${truth.length} cues)`);
console.log(`media     : ${opts.media ?? opts.video} (${videos.length} file(s))`);
console.log(`assy-cli  : ${exe}`);
console.log(`ffmpeg    : ${ffmpegDir ?? '(relying on PATH)'}`);
console.log(`formats   : ${formats.join(', ')}`);
console.log(`engines   : ${engines.join(', ')}`);
console.log(`shift     : ${shiftMs} ms   fps (MicroDVD): ${fps}`);
console.log(`timeout   : ${timeoutMs / 60000} min/run\n`);

const shifted = truth.map((c) => ({ ...c, start: c.start + shiftMs, end: c.end + shiftMs }));
const results = [];

for (const video of videos) {
    const label = basename(video);

    for (const format of formats) {
        const spec = FORMATS[format];
        const inputPath = join(outDir, `input-${format}${spec.extension}`);
        writeFileSync(inputPath, spec.write(shifted, { fps }), 'utf8');

        for (const engine of engines) {
            const safe = `${label}-${format}-${engine}`.replace(/[^\w.+-]/g, '_');
            const outputPath = join(outDir, `out-${safe}${spec.extension}`);
            process.stdout.write(`  ${label.padEnd(24)} ${format.padEnd(4)} ${engine.padEnd(12)} ... `);

            const run = await runEngine({
                exe, video, input: inputPath, output: outputPath,
                engine, timeoutMs, ffmpegDir, configPath,
            });

            const row = { media: label, format, engine, ...run };

            if (run.ok && existsSync(outputPath)) {
                const produced = spec.read(readFileSync(outputPath, 'utf8'), { fps });
                Object.assign(row, score(truth, produced));
                console.log(`median ${fmtMs(row.medianMs)}ms  max ${fmtMs(row.maxMs)}ms  ${(run.elapsedMs / 1000).toFixed(0)}s`);
            } else {
                console.log(run.timedOut ? 'TIMED OUT' : `REJECTED (${run.message ?? 'no message'})`);
            }

            results.push(row);
        }
    }
}

console.log('\n=== Recovered timing against the reference ===\n');

table(results, [
    { header: 'media', value: (r) => r.media },
    { header: 'fmt', value: (r) => r.format },
    { header: 'engine', value: (r) => r.engine },
    { header: 'status', value: (r) => (r.ok ? 'ok' : r.timedOut ? 'timeout' : 'rejected') },
    { header: 'median', value: (r) => fmtMs(r.medianMs) },
    { header: 'max', value: (r) => fmtMs(r.maxMs) },
    { header: 'dCues', value: (r) => r.cueCountDelta ?? '-' },
    { header: 'sec', value: (r) => (r.elapsedMs / 1000).toFixed(0) },
    { header: 'note', value: (r) => (r.ok ? '' : (r.message ?? '').slice(0, 60)) },
]);

console.log('\nmedian/max are milliseconds of absolute start-time error after undoing the shift.');
console.log('ASS/SSA carry centiseconds and MicroDVD carries frames, so a few tens of ms of');
console.log('quantisation error in those rows is the format, not the engine.');

if (opts.json) {
    writeFileSync(opts.json, JSON.stringify({ truth: opts.truth, results }, null, 2));
    console.log(`\nWrote ${opts.json}`);
}
