---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply tells the lane to stop reshaping the lenses and to treat the bake's fixed minimum spacing (the 15 mm `MinimumCell`, or "the bake's detail floor") as an engine limit that the Moth's lens size has met. It proposes handing the limit back with its evidence (the audit's unchanged triangle count, the source's doc comment, the failed rounds), or fixing the limit at the engine, rather than a third resculpting attempt.
Example PASS: "Round 3 should not be another sculpt. The lens is 6 mm against a 15 mm minimum cell, so the bake cannot resolve it: that is an engine limit. Hand it back with the audit counts and the `MinimumCell` comment."

FAIL if the reply tells the lane to run another sculpting round on the lenses, or to keep working the lens parts until the critic passes, without handing the limit back.
Example FAIL: "Try round 3 with a larger lens and tiny spheres around it; if the critic passes, you are done."
