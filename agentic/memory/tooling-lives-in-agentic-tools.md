---
name: tooling-lives-in-agentic-tools
description: The maintainer wants reusable tooling and verification scripts committed to agentic/tools/, never left in the agent scratchpad
metadata:
  type: feedback
---

Reusable tooling and verification scripts belong in `agentic/tools/` in the repo, not in the
session scratchpad. Throwaway one-shot scripts still go in the scratchpad and are expected to
die there.

**Why:** the scratchpad is session-scoped, so anything useful left there is lost to the next
agent and gets rebuilt from scratch. Committing it makes the verification bar inheritable
rather than something each session re-derives.

**How to apply:** when a script gets run more than once, or encodes a project standard
(build gate, lint rule, payload build), write it to `agentic/tools/` and add a row to the
Tools table in `agentic/AGENT-HANDOFF.md`. Ask whether it is worth running twice — if yes,
it is not scratchpad material.
