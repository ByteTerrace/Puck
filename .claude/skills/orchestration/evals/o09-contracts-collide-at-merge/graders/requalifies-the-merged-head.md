---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says passing alone proves nothing about the combination, and the fix is shown working again on the combined result (built, and run or reproduced with its counters) before it lands.
Example PASS: "Both passed their gates alone, which says nothing about how they interact; the fix lands once `--reproduce` and the allocation counters hold on the merged code."

FAIL if the reply accepts either lane, or the fix, on the gates it passed alone, or lands the fix without fresh evidence on the combined code.
Example FAIL: "Both lanes are green, so land the second one with a note and let the next sweep catch anything."
