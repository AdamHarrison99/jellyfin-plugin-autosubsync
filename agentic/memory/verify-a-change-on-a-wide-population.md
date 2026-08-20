---
name: verify-a-change-on-a-wide-population
description: "The maintainer wants a behaviour change proved on far more titles than the plan specifies, before and after, not on the minimum set"
metadata:
  node_type: memory
  type: feedback
  modified: 2026-08-19T00:00:00.000Z
---

! **When a change must be shown to move nothing, measure it on a wide population, ¬the minimum the
plan names.** Asked whether to run `calibrate.ps1`'s fixed five before and after, the maintainer
asked instead for a full before-and-after run over well more than five titles, for certainty.

**Why:** the five calibration titles are a regression control, ¬a population. They prove the known
cases did not move; they cannot show that a class of titles the change actually touches behaves.
The maintainer reads an identical result on five titles as weak evidence and wants the count that
makes it strong.

**How to apply:** decode each title **once**, dump the onsets and cue starts, then score twice — by
a build of the changed source taken from `HEAD` and by the changed one — so the audio is held
constant and any difference is the code. Weight the population toward the regime the change
touches, and report the structural invariants separately from the recall numbers. The fixed five
still run, as the control they are. Related: [[read-the-docs-before-concluding]],
[[media-lives-on-a-slow-smb-share]] — the wide run is many titles, so it is one media command per
title and it belongs in the background.
