import fs from 'node:fs';
import path from 'node:path';

const lab = path.dirname(new URL(import.meta.url).pathname.slice(1));
const matrix = path.join(lab, 'matrix');

function cues(file) {
    if (!fs.existsSync(file)) return null;
    const raw = fs.readFileSync(file, 'utf8').replace(/^﻿/, '');
    return raw
        .split(/\r?\n\r?\n/)
        .map(b => b.split(/\r?\n/).filter(l => l.trim().length))
        .filter(b => b.length >= 2 && b.some(l => l.includes('-->')))
        .map(b => b.slice(b.findIndex(l => l.includes('-->')) + 1).join(' '))
        .map(t => t.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim())
        .filter(t => t.length);
}

function lev(a, b) {
    const m = a.length, n = b.length;
    let prev = Array.from({ length: n + 1 }, (_, j) => j);
    for (let i = 1; i <= m; i++) {
        const cur = [i];
        for (let j = 1; j <= n; j++) {
            cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
        }
        prev = cur;
    }
    return prev[n];
}

const truth = cues(path.join(lab, 'truth.srt'));
const refs = {
    solid: truth,
    outline: truth,
    vobsub: truth.slice(0, 3),
    complex: cues(path.join(lab, 'cx-tesseract-iso.srt'))
};

const rows = [];
for (const f of fs.readdirSync(matrix).sort()) {
    if (!f.endsWith('.srt')) continue;
    const [tag, engine, iso] = f.replace('.srt', '').split('-');
    const got = cues(path.join(matrix, f));
    const ref = refs[tag];
    const n = Math.min(got.length, ref.length);

    let exact = 0, chars = 0, refChars = 0, stars = 0;
    for (let i = 0; i < n; i++) {
        if (got[i] === ref[i]) exact++;
        chars += lev(got[i], ref[i]);
        refChars += ref[i].length;
        stars += (got[i].match(/\*/g) || []).length;
    }
    const cer = refChars ? (chars / refChars) * 100 : 0;
    rows.push({ tag, engine, iso, cues: got.length, ref: ref.length, exact, pctExact: (exact / ref.length) * 100, cer, stars });
}

const ref = 'reference';
console.log('sample   engine     iso    cues  exact/total   exact%    CER%   "*"');
console.log('-'.repeat(72));
for (const r of rows) {
    console.log(
        r.tag.padEnd(9) + r.engine.padEnd(11) + r.iso.padEnd(7) +
        String(r.cues).padStart(4) + '  ' +
        `${r.exact}/${r.ref}`.padStart(11) + '  ' +
        r.pctExact.toFixed(1).padStart(7) + '  ' +
        r.cer.toFixed(1).padStart(6) + '  ' +
        String(r.stars).padStart(4));
}
console.log('\nCER = character error rate vs reference (lower is better). ' +
    'complex uses Tesseract output as the ' + ref + '; the rest use exact ground truth.');
