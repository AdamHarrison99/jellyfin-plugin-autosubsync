---
name: release-notes-are-bullets-only
description: "GitHub release notes are concise update bullets only — never requirements, installation steps, or prose"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-12T15:12:41.252Z
---

GitHub release bodies contain only concise bullets describing what changed in that version. No
requirements section, no installation instructions, no prose intro, no tables.

**Why:** on 2026-08-12 I gave v1.0.0.0 a body with installation and requirements sections, and was
told to drop both — a release body carries concise update notes in bullets and nothing else.
Install and requirements live in `README.md`, which is the one place they can be kept current;
duplicating them per-release means every old release page carries stale instructions forever.

**How to apply:** write the body as bullets of what changed. Same shape as
[[short-bulleted-commit-messages]]. The `payload-v<version>` releases are the exception worth
keeping short too, but they do need the "this is not the plugin" line and the asset hashes, since
those are release-specific facts with no home in the README.
