---
name: run-verify-only-before-commits
description: "Run agentic/tools/verify.ps1 only before a commit, not after every edit"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-19T20:35:00.000Z
---

`.\agentic\tools\verify.ps1` runs only before a commit. Do not run it after every edit, after a
documentation change, or to confirm an experiment — use `dotnet build` for an intermediate compile
check, and run the single relevant harness (`dotnet run --project agentic/tools/<name>`) when a
behaviour needs checking.

**Why:** the maintainer has now raised it across multiple sessions — three times in one. The full gate builds, lints, runs sixteen
harnesses and checks every payload hash against the network, so running it for an `AUDIT.md` edit
or to confirm an experiment is pure wasted time. **Treat an unprompted `verify.ps1` run as a
defect, not a diligence.**

**How to apply:** Edits to `agentic/*.md` never need it. During an investigation, run the one
harness that covers what changed — `simulate-concurrency.mjs` for the concurrency control law,
`verifycheck` for the audio check, `orchestratorcheck` for the verify-step gates. After a code
edit, `dotnet build Jellyfin.Plugin.AutoSubSync.csproj` from the repo root is the intermediate check. Save the full gate
for the moment before the maintainer commits — they author every commit, see [[never-commit-yourself]].
When work is finished, **explain what changed and what it means**; do not reach for the gate as a
way of demonstrating completeness.
