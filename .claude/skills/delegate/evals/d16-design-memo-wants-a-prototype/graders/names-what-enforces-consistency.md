---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the memo must name what in the design makes the GPU's level choice agree with the CPU's cull (for example one source both sides derive from, one door, a derived view, or the GPU consuming the CPU's cull output) and where that guarantee stops, rather than only asserting it.
Example PASS: "State the invariant and its enforcer: both choices derive from one shared bounds buffer. Then say where it stops holding, for example a frame where the bounds update late."

FAIL if the reply leaves the consistency line as an assertion, names no mechanism and no limit, or offers only a way to observe agreement (a readback diff, a mismatch count, a probe) as what backs the line, since a check shows disagreement after the fact and enforces nothing.
Example FAIL: "Back the line with a probe that counts GPU and CPU mismatches per frame, and report the count."
