---
name: ask-before-changing-the-ui
description: Never add, remove, or restyle a config-page UI element without asking first
metadata:
  type: feedback
---

Never add, remove, or restyle an element on the config page without asking first. Approving a
finding is not approving a UI change as its fix — propose the shape and wait.

**Why:** on 2026-08-16 the maintainer approved audit findings D2 and D3 (the *already in sync* card was
covering three different outcomes, and `Pending` records appeared on no card). I resolved both by
adding three new status cards — *refused by audio check*, *source gone*, *not yet run* — and
shipped them in 1.3.0.0. They had approved the defect, not the panel redesign, and told me to stop
adding UI elements without asking.

**How to apply:** when a finding's natural fix touches `Configuration/configPage.html`, present the
fix as a proposal describing what the user would see, and stop. A data-only fix (a corrected count,
a new field on a record) does not need this; anything that changes what appears on screen does. See
[[report-status-as-done-not-done-table]] and [[readme-is-a-starter-overview]].
