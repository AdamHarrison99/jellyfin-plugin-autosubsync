---
name: readme-is-a-starter-overview
description: "The plugin README is a short getting-started overview, not documentation — and it is never edited without asking first"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-18T23:20:00.000Z
---

! **Never edit `Jellyfin.Plugin.AutoSubSync/README.md` without asking first, and ask with the
exact wording proposed.** Not "should the README mention X" — the literal line, for approval or
rejection. This is the same standing rule the config page has: see [[ask-before-changing-the-ui]].

`Jellyfin.Plugin.AutoSubSync/README.md` is a simple overview to get someone installed and running.
It is not documentation and not a detailed reference. Every setting already has its full
explanation on the config page itself, so a README table gets one short line per setting.

**It carries no rationale at all.** Not a compressed version of the reasoning — none. A sentence
saying *why* the plugin does something ("that is how it recognizes its own output", "rollback
refuses to delete anything without it") does not belong in it at any length.

**Why:** the maintainer has asked four times to tighten it. Each time the added prose was correct and still
wrong for the file — it explained a mechanism to a reader who has not yet decided to install the
plugin. Long table cells, multi-paragraph feature sections, and trailing because-clauses are the
failure mode. The fourth was a whole `### After updating the plugin` section explaining that stored
verdicts are re-judged as titles are re-processed: true, load-bearing, and still not the README's
business. They deleted it themselves. ! A plan or design document saying *"the README must state X"* is
¬authorization — that instruction gets satisfied by the **release changelog**, which is what an
upgrading user actually reads, or it gets raised with them first.

**How to apply:** Ask, with the exact text. If approved: one line per setting, one or two sentences
per section, state *what* happens and stop. Explanation of *how* a mechanism works belongs in
`agentic/ARCHITECTURE.md`, rationale in the design document — see [[no-changelog-no-doc-comments]].
When adding a feature, add a line, not a section. ! The maintainer edits this file themselves; before touching
it, check `git diff` for their edits and keep them — do ¬rewrite a line they have just written.
