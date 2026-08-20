#!/usr/bin/env node
/*
 * Enforces the comment rule and the no-out-of-repo-references rule in AGENT-HANDOFF.md.
 *
 * Comment rules apply to source under Jellyfin.Plugin.AutoSubSync/ only. agentic/ is exempt —
 * those comments are working notes and may be as long as they are useful. The out-of-repo
 * reference check is likewise published-only, since that is the tree people clone.
 *
 * A comment is a note that makes the next line readable. Anything larger is documentation and
 * belongs in agentic/ARCHITECTURE.md. Enforced as:
 *   comment-run           max 2 adjacent comment lines, "!" traps included
 *   comment-length        max 100 characters on one comment line
 *   comment-block-length  max 180 characters across an adjacent run
 *   comment-prose         max 2 sentences across an adjacent run
 *   rationale-word        connectives, history, and tutorial voice
 *   notation-shorthand    the agent notation from agentic/CLAUDE.md, written out in plain words
 *   doc-comment           /// is documentation
 *   block-comment         a source block comment is documentation
 *   commented-code        dead code, delete it
 *   redundant-comment     restates the line below it
 *
 * Usage:
 *   node agentic/tools/check-comments.mjs                         lines this branch changed
 *   COMMENT_LINT_BASE=HEAD node agentic/tools/check-comments.mjs   uncommitted work only
 *   node agentic/tools/check-comments.mjs <path>                   whole-file scan
 *
 * Exit 0 clean, 1 violations found, 2 usage error.
 */

import { execFileSync } from 'node:child_process';
import { readFileSync, statSync, readdirSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';

const ROOT = resolve(new URL('../..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));

const BANNED = [
  // Rationale connectives.
  'because', 'otherwise', 'rather than', 'so that', 'instead of', 'in order to',
  'which is why', 'this is why', 'the reason', 'due to', 'therefore', 'hence', 'thus',
  // Narration of history.
  'used to', 'previously', 'would have', 'turns out', 'historically', 'originally',
  'at one point', 'we changed', 'was changed', 'no longer',
  // Tutorial voice.
  'note that', 'keep in mind', 'be aware', 'for example', 'e.g.', 'i.e.',
  'this means', 'in other words', 'as opposed to', 'the point is',
];

// Agent notation from agentic/CLAUDE.md. Fine in agentic/, never in a shipped comment.
const SHORTHAND = [
  ['¬', 'write "not"'],
  ['∵', 'rationale belongs in agentic/ARCHITECTURE.md, not a comment'],
  ['→', 'write the words'],
  ['w/', 'write "with"'],
];

// A comment is a note for the next line, not a paragraph. These bound it three ways.
const MAX_RUN = 2;
const MAX_LINE_CHARS = 100;
const MAX_BLOCK_CHARS = 180;
const MAX_SENTENCES = 2;

// Words too common to prove a comment restates the code below it.
const STOPWORDS = new Set([
  'the', 'a', 'an', 'and', 'or', 'of', 'to', 'in', 'on', 'for', 'is', 'are', 'be',
  'it', 'its', 'this', 'that', 'with', 'from', 'by', 'as', 'at', 'not', 'no', 'one',
  'we', 'you', 'all', 'any', 'per', 'has', 'have', 'was', 'were', 'here', 'only',
]);
const SOURCE_EXT = new Set(['.cs', '.mjs', '.js', '.ts', '.ps1', '.py']);
const TEXT_EXT = new Set(['.md', '.html', '.json', '.yaml', '.yml']);

// agentic/ ships with the repo but is not the plugin. Everything outside it is.
const AGENT_DIR = 'agentic';

// Files that do not exist in the repo. Plugin source naming one is a dangling pointer.
const OUT_OF_REPO_DOCS = [
  'custom-CHANGELOG-local.md',
  'CHANGELOG.md',
];

const EXCLUDED_DIRS = new Set(['bin', 'obj', '.git', 'node_modules', '.vs']);

function hasExt(path, set) {
  const dot = path.lastIndexOf('.');
  return dot !== -1 && set.has(path.slice(dot));
}

const isSource = (path) => hasExt(path, SOURCE_EXT);
const isScanned = (path) => isSource(path) || hasExt(path, TEXT_EXT);

function walk(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.has(entry.name)) walk(join(dir, entry.name), out);
    } else if (isScanned(entry.name)) {
      out.push(join(dir, entry.name));
    }
  }
  return out;
}

function changedLines() {
  const base = process.env.COMMENT_LINT_BASE ?? 'origin/HEAD';
  let diff;
  try {
    diff = execFileSync('git', ['diff', '--unified=0', base, '--', '.'], {
      cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'],
    });
  } catch {
    return null;
  }

  const map = new Map();
  let file = null;
  for (const line of diff.split('\n')) {
    if (line.startsWith('+++ b/')) {
      file = line.slice(6).trim();
      if (!map.has(file)) map.set(file, new Set());
    } else if (line.startsWith('@@') && file) {
      const m = /\+(\d+)(?:,(\d+))?/.exec(line);
      if (m) {
        const start = Number(m[1]);
        const count = m[2] === undefined ? 1 : Number(m[2]);
        for (let i = 0; i < count; i++) map.get(file).add(start + i);
      }
    }
  }
  return map;
}

// Comment lines only. Not a full parser; a "//" inside a string literal is a false positive.
function commentLines(text) {
  const out = [];
  const lines = text.split('\n');
  let inBlock = false;

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const trimmed = raw.trim();

    if (inBlock) {
      out.push({ n: i + 1, text: trimmed.replace(/^\*+/, '').trim(), kind: 'block' });
      if (trimmed.includes('*/')) inBlock = false;
      continue;
    }
    if (trimmed.startsWith('/*')) {
      out.push({ n: i + 1, text: trimmed.slice(2).replace(/\*\/$/, '').trim(), kind: 'block' });
      if (!trimmed.includes('*/')) inBlock = true;
      continue;
    }
    if (trimmed.startsWith('///')) {
      out.push({ n: i + 1, text: trimmed.slice(3).trim(), kind: 'doc' });
      continue;
    }
    if (trimmed.startsWith('//') || trimmed.startsWith('#')) {
      const body = trimmed.startsWith('//') ? trimmed.slice(2) : trimmed.slice(1);
      out.push({ n: i + 1, text: body.trim(), kind: 'line' });
    }
  }
  return out;
}

// Published code cannot cite a file the reader has no way to open.
function checkDocRefs(rel, text, only) {
  const violations = [];
  if (rel === AGENT_DIR || rel.startsWith(AGENT_DIR + '/')) return violations;

  const lines = text.split('\n');
  for (let i = 0; i < lines.length; i++) {
    if (only && !only.has(i + 1)) continue;
    for (const doc of OUT_OF_REPO_DOCS) {
      if (lines[i].includes(doc)) {
        violations.push({
          line: i + 1,
          rule: 'out-of-repo-ref',
          message: `"${doc}" is not in the published repository`,
        });
      }
    }
  }
  return violations;
}

// Strips the leading trap marker so it does not read as a sentence terminator.
const body = (c) => c.text.replace(/^!\s*/, '').trim();

const looksLikeCode = (s) =>
  /[;{}]\s*$/.test(s) ||
  /^(if|else|for|foreach|while|switch|return|var|await|using|public|private|protected|internal|const)\b/.test(s) ||
  /^[A-Za-z_][\w.]*\s*\([^)]*\)\s*;?$/.test(s);

// Crude stem so "calculates" still matches CalculateX. Prefix matching is the point.
const stem = (w) => w.replace(/-/g, '').replace(/(ies|es|s|ing|ed)$/, '');

// All of a short comment's own words appearing in the line below means it says nothing new.
function restatesCode(runText, lines, afterLine) {
  const words = runText.toLowerCase().match(/[a-z][a-z-]{2,}/g) ?? [];
  const content = [...new Set(words.filter((w) => !STOPWORDS.has(w)))];
  if (content.length < 2 || content.length > 5) return false;

  for (let i = afterLine; i < Math.min(lines.length, afterLine + 3); i++) {
    const next = lines[i].trim();
    if (!next || next.startsWith('//') || next.startsWith('*') || next.startsWith('[')) continue;
    const flat = next.toLowerCase().replace(/[^a-z]/g, '');
    return content.every((w) => stem(w).length > 2 && flat.includes(stem(w)));
  }
  return false;
}

function checkComments(text, only) {
  const comments = commentLines(text);
  const lines = text.split('\n');
  const violations = [];

  const inScope = (n) => !only || only.has(n);
  const add = (line, rule, message) => {
    if (inScope(line)) violations.push({ line, rule, message });
  };

  let lastBlockLine = -10;

  for (const c of comments) {
    if (c.kind === 'doc') {
      add(c.n, 'doc-comment', 'XML doc comments are documentation - use agentic/ARCHITECTURE.md');
    }
    if (c.kind === 'block') {
      // One report per block, not per line of it.
      if (c.n !== lastBlockLine + 1) {
        add(c.n, 'block-comment', 'block comments are documentation - use a short // note');
      }
      lastBlockLine = c.n;
    }
    if (body(c).length > MAX_LINE_CHARS) {
      add(c.n, 'comment-length', `${body(c).length} characters (max ${MAX_LINE_CHARS}) - shorten it`);
    }
    if (looksLikeCode(body(c))) {
      add(c.n, 'commented-code', 'commented-out code - delete it');
    }

    for (const [symbol, advice] of SHORTHAND) {
      const re = symbol === 'w/' ? /\bw\//i : new RegExp(symbol);
      if (re.test(body(c))) {
        add(c.n, 'notation-shorthand', `"${symbol}" is agent notation - ${advice}`);
      }
    }

    const lower = body(c).toLowerCase();
    for (const word of BANNED) {
      // Word-boundary match; multi-word entries tolerate a line break.
      const re = new RegExp(`\\b${word.replace(/ /g, '\\s+').replace(/\./g, '\\.')}\\b`, 'i');
      if (re.test(lower)) {
        add(c.n, 'rationale-word', `"${word}" - explanation belongs in agentic/ARCHITECTURE.md, not a comment`);
      }
    }
  }

  // Runs of adjacent comment lines, traps included. One note, not a paragraph.
  let run = [];
  const flushRun = () => {
    if (!run.length) return;
    const first = run[0].n;
    const joined = run.map(body).join(' ');
    const sentences = (joined.match(/[.!?](?=\s|$)/g) ?? []).length;

    if (run.length > MAX_RUN) {
      add(first, 'comment-run', `${run.length} consecutive comment lines (max ${MAX_RUN}) - this is documentation, move it to agentic/ARCHITECTURE.md`);
    }
    if (joined.length > MAX_BLOCK_CHARS) {
      add(first, 'comment-block-length', `${joined.length} characters across ${run.length} lines (max ${MAX_BLOCK_CHARS}) - shorten it`);
    }
    if (sentences > MAX_SENTENCES) {
      add(first, 'comment-prose', `${sentences} sentences (max ${MAX_SENTENCES}) - a comment is a note, not a paragraph`);
    }
    if (restatesCode(joined, lines, run[run.length - 1].n)) {
      add(first, 'redundant-comment', 'restates the code below it - delete it');
    }
    run = [];
  };

  let prev = -10;
  for (const c of comments) {
    if (c.kind !== 'line') { flushRun(); prev = -10; continue; }
    if (c.n === prev + 1) run.push(c);
    else { flushRun(); run = [c]; }
    prev = c.n;
  }
  flushRun();

  return violations;
}

function check(file, only) {
  const rel = relative(ROOT, file).split(sep).join('/');
  const text = readFileSync(file, 'utf8');

  // agentic/ is working notes for an agent, not shipped code. Only the plugin is audited.
  const published = !(rel === AGENT_DIR || rel.startsWith(AGENT_DIR + '/'));

  const violations = [
    ...(isSource(file) && published ? checkComments(text, only) : []),
    ...checkDocRefs(rel, text, only),
  ];

  return violations.length ? { file: rel, violations } : null;
}

function main() {
  const arg = process.argv[2];
  let files;
  let onlyByFile = null;

  if (arg) {
    const target = resolve(ROOT, arg);
    let st;
    try { st = statSync(target); } catch {
      console.error(`check-comments: no such path: ${arg}`);
      process.exit(2);
    }
    files = st.isDirectory() ? walk(target) : [target];
  } else {
    const map = changedLines();
    if (map === null) {
      console.error('check-comments: no git base available; pass a path for a whole-file scan');
      process.exit(2);
    }
    onlyByFile = map;
    files = [...map.keys()]
      .filter(isScanned)
      .map((f) => resolve(ROOT, f))
      .filter((f) => { try { return statSync(f).isFile(); } catch { return false; } });
  }

  const results = [];
  for (const file of files) {
    const only = onlyByFile
      ? onlyByFile.get(relative(ROOT, file).split(sep).join('/'))
      : null;
    const r = check(file, only);
    if (r) results.push(r);
  }

  if (!results.length) {
    console.log(`check-comments: clean (${files.length} file${files.length === 1 ? '' : 's'})`);
    process.exit(0);
  }

  let total = 0;
  for (const { file, violations } of results) {
    for (const v of violations.sort((a, b) => a.line - b.line)) {
      console.log(`${file}:${v.line}  [${v.rule}] ${v.message}`);
      total++;
    }
  }
  console.log(`\ncheck-comments: ${total} violation${total === 1 ? '' : 's'} in ${results.length} file${results.length === 1 ? '' : 's'}`);
  process.exit(1);
}

main();
