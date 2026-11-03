---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's recommendation accounts for refit's culling getting worse over time (17,930 culled at minute 10 against 18,190 for rebuild, from 18,180 at minute 1), for example refit with a periodic or threshold-triggered rebuild, or refit with that decay stated as the trade-off.
Example PASS: "Refit is the right default, but its boxes loosen: 250 fewer instances culled after ten minutes. Recommend refit with a rebuild when culling drops past a threshold, and say so in the hand-back."

FAIL if the reply endorses refit on CPU time alone without mentioning the culling decay, or misreads the table.
Example FAIL: "Refit is 0.31 ms cheaper per frame with nearly identical culling, so recommend it."
