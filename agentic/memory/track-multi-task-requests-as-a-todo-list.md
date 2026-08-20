---
name: track-multi-task-requests-as-a-todo-list
description: "When the maintainer gives several tasks at once, keep a visible todo list, re-post it every time an item completes, and end with audit then verify before any commit"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-16T00:00:00.000Z
---

When the maintainer gives a set of tasks in one message, write out a todo list covering **all** of them and
re-post the whole table **every time an item finishes** — not batched at the end of a long stretch
of tool calls. Always append **audit** and then **verify** as the final two items, before the
commit step, even when they do not ask for them.

**Why:** they track progress across long multi-part sessions and want to see what is left without
re-reading the thread. A task that takes twenty tool calls with no table posted reads as no
progress at all. They have had to ask for this more than once in a single session, so batching is the
failure mode to watch for. The audit item is standing policy in this repo — see
[[no-changelog-no-doc-comments]] and the pre-release checklist in `agentic/CLAUDE.md`.

**How to apply:** no TodoWrite tool exists in this session, so keep the list in the reply itself as
a markdown table with an ID column. Re-post it at every task transition, including mid-turn when a
task completes. `verify.ps1` runs **once**, at the end, not per task — see
[[run-verify-only-before-commits]]. Findings go into `agentic/AUDIT.md` immediately once the audit
runs, without asking first. Never run the commit item yourself —
[[never-commit-yourself]].
