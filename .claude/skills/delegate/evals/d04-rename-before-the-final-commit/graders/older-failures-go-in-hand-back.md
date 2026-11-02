---
type: llm
weight: 1
---

PASS if the reply says failures that predate the lane's change are reported in its hand-back (for example under **Found**, with their locations) rather than fixed in this lane.
Example PASS: "A failure that was already there isn't yours to fix here; put it under Found with its `file:line`."

FAIL if the reply tells the lane to fix the older failures in this change too, or to leave them unreported.
Example FAIL: "While you're in there, fix every broken link the check reports so the run is green."
