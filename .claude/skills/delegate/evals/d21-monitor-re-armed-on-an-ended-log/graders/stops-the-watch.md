---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to stop the Monitor still running on the log (`m3x3`) and arms no further watch for `PASS`; any later watch it proposes, for a re-run, ends on success, failure or the process exiting.
Example PASS: "Stop `m3x3` now. When I re-run after the fix, I'll rely on the job's own exit notice, or one Monitor whose filter also matches `crash`, `FAIL` and the process exiting."

FAIL if the reply leaves `m3x3` running, re-arms another watch on `PASS` alone, or only lengthens a timeout.
Example FAIL: "m3x3 has a 30-minute timeout, so let it run; if it expires I'll re-arm it once more."
