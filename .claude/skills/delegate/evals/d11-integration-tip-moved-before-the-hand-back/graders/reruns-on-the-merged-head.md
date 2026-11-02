---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that after the merge the lane rebuilds what it touched and re-runs its focused check on the merged head before handing back.
Example PASS: "After the merge, rebuild `Puck.Physics` and re-run `islands-tests.puck`; report those results."

FAIL if the reply hands back on the pre-merge results, or merges without re-running anything.
Example FAIL: "Merge the tip so the lead gets a clean merge, and hand back with the results you already have."
