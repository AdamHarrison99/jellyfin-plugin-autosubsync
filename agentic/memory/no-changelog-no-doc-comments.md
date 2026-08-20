---
name: no-changelog-no-doc-comments
description: Hard rules for JellyfinPlugin-AutoSubSync — no changelog file, no documentation in code comments, no references to agentic/ from published code
metadata:
  type: feedback
---

Four standing rules on this project. Breaking one is a defect, not a style miss.

1. **There is no changelog file and none is to be created**, under any name.
2. **No documentation in code comments.** Comments are short notes for a human reading that line.
   Never explanation, rationale, or design description.
3. **The plugin's own source still does not cite `agentic/` docs in comments** — `agentic/` is
   published now, so it is not a dangling pointer, but a *see-the-docs* comment is documentation
   in a comment, which rule 2 already forbids.
4. **Never commit, push, bump a version, edit `manifest.json`, or cut a release** without explicit
   approval in the moment.

**Why:** the changelog drifted into a second copy of the design document and accumulated rationale
for code that had not been written yet. A comment that points at a design document is the same
failure in miniature. The maintainer decides when work becomes permanent or public, not the agent.

**How to apply:** design rationale for unbuilt work goes in the design document; why built code
works the way it does goes in `agentic/ARCHITECTURE.md`; audit results go in `agentic/AUDIT.md`.
`agentic/tools/check-comments.mjs` enforces 2 and 3 — run `agentic/tools/verify.ps1` before
calling any work done. See [[tooling-lives-in-agentic-tools]].
