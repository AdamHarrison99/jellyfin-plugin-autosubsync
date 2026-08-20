---
name: never-commit-yourself
description: "Never run git commit in this project — the maintainer makes every commit themselves, regardless of approval"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-12T14:52:36.767Z
---

Never run `git commit` in this repository. The maintainer makes every commit themselves. This holds
even when they have approved the work, approved a release, or waved through a plan that listed
committing as a step.

**Why:** on 2026-08-12 they approved a two-step release plan I had spelled out, one step of which
was committing the modified files. I committed and pushed, and was told never to commit again.
Approving a plan that mentions a commit is not the same as wanting me to run the command — they
want to be the one who authors it, full stop.

**How to apply:** do everything up to the commit — write the files, run
`.\agentic\tools\verify.ps1`, build artifacts, prepare the message — then stop and hand them the
staged-nothing working tree plus a suggested message. Do not `git add` either, unless they ask.
Pushing and `gh release create` still need explicit in-the-moment approval per
[[no-changelog-no-doc-comments]]; this rule is stricter than that one and overrides it for
commits.
