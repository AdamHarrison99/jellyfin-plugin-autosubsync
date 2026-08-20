---
name: dont-re-raise-accepted-findings
description: "Once a finding is measured and accepted in AUDIT.md, stop surfacing it in later replies"
metadata: 
  node_type: memory
  type: feedback
  modified: 2026-08-16T02:27:52.245Z
---

When an audit finding has been investigated, attempted, measured, and recorded as `[A]` or `[R]`
in `agentic/AUDIT.md`, it is closed. Do not append it as a caveat to unrelated answers.

**Why:** `AUDIT.md` is the record, so repeating a known accepted risk adds no information and
reads as hedging. The maintainer tracked the W1 investigation in full and did not need it restated at the
end of a release summary.

**How to apply:** Raise an accepted finding again only when new evidence changes its status, or
when the maintainer asks about it directly. A failed fix attempt is a reason to stop proposing it, not a
reason to keep flagging it. See [[no-changelog-no-doc-comments]] for the same principle about
where rationale lives — the file carries it, not the conversation.
