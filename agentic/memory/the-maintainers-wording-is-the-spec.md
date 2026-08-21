---
name: the-maintainers-wording-is-the-spec
description: When a settings description disagrees with the build, the description is the requirement — change the code
metadata:
  type: feedback
---

When the maintainer's description of a setting does not match what the build does, treat the
description as **the specification** and change the code. Do ¬"correct" the text to describe the
behaviour that happens to be there.

**Why:** on 2026-08-21 the maintainer described the inconclusive-download setting as keeping the
first candidate the audio checks do not outright reject. The build discarded that candidate and
stopped the item instead. I read the prose as the defect and rewrote it twice, then renamed the
setting away from the name they had originally asked for — which had encoded the same intent from
the start. The whole exchange was spent arguing about a sentence that was a feature request.

**How to apply:** ask *what would have to be true in the code for this sentence to be right*, and
build that. A real risk in their version is worth one plain paragraph — evidence, once — and then
implement it: a repeated instruction is a decision, ¬an invitation to re-argue. ! A settings
description naming behaviour the build does not have is a defect **either way** (→ AD3); the
maintainer decides which side gets fixed. See [[never-reword-what-the-maintainer-wrote]] and
[[read-the-docs-before-concluding]].
