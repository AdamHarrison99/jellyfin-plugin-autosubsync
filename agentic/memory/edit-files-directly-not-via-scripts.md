---
name: edit-files-directly-not-via-scripts
description: Make file changes with the Edit tool, not with throwaway patch scripts
metadata:
  type: feedback
---

Make file changes with the editing tool directly. Do ¬write a throwaway Python or PowerShell script
to patch a file, even for a multi-hunk or multi-file change.

**Why:** the maintainer asked for this on 2026-08-21, after a run of edits delivered as scratchpad
scripts. A patch script hides the change behind an assert-and-replace wrapper — the diff never
appears in the transcript, so the maintainer cannot see what was altered until afterwards, which is
exactly the failure [[never-reword-what-the-maintainer-wrote]] exists to prevent.

**How to apply:** several edit calls in one message beats one script. Reach for a script only where
editing genuinely cannot do the job — a mechanical rename across many files, or a change that must
preserve exact bytes. ! The encoding traps still stand: `.csproj`, `build.yaml` and `manifest.json`
are CRLF and the csproj carries a BOM, while `.cs`, `.md` and `.html` in the tree are LF without
one. See [[tooling-lives-in-agentic-tools]] for what earns a committed script instead.
