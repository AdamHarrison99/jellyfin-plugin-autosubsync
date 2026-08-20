---
name: short-bulleted-commit-messages
description: "Commit messages must be short and bulleted — a subject line plus terse bullets, never prose paragraphs"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-12T15:09:49.239Z
---

Suggested commit messages must be a subject line plus a few terse bullets. No prose paragraphs, no
rationale, no background on why the old code was wrong.

**Why:** on 2026-08-12 I offered a message with two justification paragraphs explaining assembly
reference binding, and was told the message must be short, bulleted and to the point every time,
with nothing unnecessary in it.

! **Only files the diff actually contains may appear in it.** The repo root is
`Jellyfin.Plugin.AutoSubSync/` and `agentic/` now sits inside it, so harness and doc changes **do**
belong in a message — but only when they are in that commit. Run `git status` from the repo root
and write bullets for the files it lists, nothing else. Repeated correction: writing bullets for
work that is not in the diff has happened more than once.

**How to apply:** subject line under ~60 chars, then one bullet per change, each a single line
stating what changed. Explanation belongs in `agentic/ARCHITECTURE.md` or the design document, not
the commit — the same reasoning as the project's no-documentation-in-comments rule. Still hand the
message over rather than committing it, per [[never-commit-yourself]].
