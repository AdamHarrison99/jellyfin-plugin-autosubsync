---
name: never-reword-what-the-maintainer-wrote
description: Never edit or delete prose the maintainer wrote without asking clearly and getting a yes
metadata:
  type: feedback
---

Prose the maintainer wrote or edited — a `README.md` row, a config-page description, any text they
typed — is theirs. Never reword, replace or delete it without asking clearly first and waiting for
a yes. This holds **even when the text is wrong about the code**.

**Why:** on 2026-08-21 the maintainer rewrote the description of the inconclusive-download setting.
The wording described behaviour the build did not have, so I edited it to match the code, announced
it afterwards, and was told twice to stop — the second time in the middle of the next task. Editing
their prose silently reads as overruling them on their own product, and it leaves them unable to
trust that what they wrote is still there.

**How to apply:** where their wording contradicts the code, say so in a sentence, quote the code
that proves it, propose a replacement, and **stop**. Two fixes exist and the choice is theirs:
change the words, or change the code so the words come true — usually they mean the second, see
[[the-maintainers-wording-is-the-spec]]. Renaming a control they explicitly asked to rename is
authorized; the description around it is not. Reverting on request means restoring their text
**verbatim**, line breaks included. See [[ask-before-changing-the-ui]].
