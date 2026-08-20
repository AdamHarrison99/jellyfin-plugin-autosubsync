---
name: read-the-docs-before-concluding
description: Read agentic/ docs on a component before concluding anything about a defect found in it
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-16T13:24:52.741Z
---

When I find an issue, read the `agentic/` documentation covering that component **before** deciding
what it is or how to fix it — not after proposing a fix.

**This applies to the maintainer's questions too, not only to findings.** Asking what something
means, why it is the way it is, or what it is for are design questions, and `ARCHITECTURE.md` is
the file that answers them — `CLAUDE.md` routes to it as *what each component does and why it is built that way*.
The code says what happens, ¬what it is for.

**Why:** on 2026-08-16 I reported the config page's pipeline table as a labelling problem. The maintainer told
me to check the docs. `ARCHITECTURE.md` recorded a deliberate decision to omit the `Sync` row *for
exactly the reason the confusion arose* — the decision had silently reverted. Reasoning from code
alone could never have found that. In the same check, three findings turned out to be already
recorded as accepted (N3, N6, A4), and the harness gap was cheaper to close than I had claimed.

On 2026-08-16 it happened again on the question form: the maintainer asked what a `Rejected` count on the
`Synchronization` row meant and I read five layers of source to derive it. `ARCHITECTURE.md`'s
`SettledTwin` paragraph answers it outright. ! Doc drift found in the same session is **¬** a
reason to distrust the narrative — a constant goes stale silently, the prose does not.

**How to apply:** before presenting a finding **or answering a question about intent**, grep
`agentic/ARCHITECTURE.md`, `agentic/AUDIT.md` and the design document for the component and its key
type names. Look for a recorded decision that
the code now contradicts — that is a finding in itself, and a stronger one. See
[[dont-re-raise-accepted-findings]] and [[report-status-as-done-not-done-table]].
