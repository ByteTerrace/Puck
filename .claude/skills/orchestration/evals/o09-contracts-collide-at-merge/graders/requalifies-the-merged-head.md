---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply has the fix shown working on the combined (merged) code before it lands: built, and run or reproduced with its counters (for example `--reproduce` and the allocation counters).
Example PASS: "The fix lands once `--reproduce` and the allocation counters hold on the merged code, since each lane passing alone says nothing about the combination."

FAIL if the reply accepts either lane, or the fix, on the gates it passed alone, or lands the fix without fresh evidence on the combined code.
Example FAIL: "Both lanes are green, so land the second one with a note and let the next sweep catch anything."
