---
type: llm
weight: 1
---

The response says a deliberate rounding correction is expected to change state hashes, so persisted replays or baselines it invalidates are re-recorded rather than the old result being preserved.

PASS if it says the rounding change moves hashes, that this is accepted, and that invalidated replays or baselines are re-recorded; it keeps no path that reproduces the old rounding. Example: "A deliberate rounding correction changes state hashes; make the change and re-record the replays it invalidates. Don't keep the old rounding."

FAIL if it asks to preserve the old hashes or results, or adds a compatibility path for the old rounding. Example: "Keep the old rounding behind a flag so existing replays still match."
