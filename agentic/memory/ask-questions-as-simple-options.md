---
name: ask-questions-as-simple-options
description: "Every question to the maintainer is asked through the options picker, in plain language — never as prose buried at the end of a report"
metadata:
  node_type: memory
  type: feedback
  modified: 2026-08-19T01:10:00.000Z
---

! **Always bring a question as a short plain-language question w/ simple options**, through the
question picker — ¬a paragraph at the end of a long report, ¬a choice phrased in code terms.
Name the recommended option first and mark it *(Recommended)*.

**Why:** a decision was buried in the closing paragraph of a technical report, phrased in terms of
component names (`SyncOrchestrator` vs `RecordReconciler`). The maintainer had to reconstruct the choice from
the surrounding analysis to answer it. They asked for it to be re-put in plain language with
simple options, and made that a standing rule for every question from then on.

**How to apply:** state the effect, ¬the mechanism — *"fix both places"*, ¬*"guard the un-retire
branch"*. One sentence per option covering what changes and what it costs. Analysis still goes in
the report; the **question** goes in the picker. Applies to any real fork: which fix, which
wording, whether to proceed. Related: [[ask-before-changing-the-ui]],
[[readme-is-a-starter-overview]] — both are ask-first rules w/ the same shape.
