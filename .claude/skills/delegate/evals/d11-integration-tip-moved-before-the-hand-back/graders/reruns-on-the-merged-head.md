---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that after the merge the lane takes its evidence on the merged head before handing back: it rebuilds what it touched there and re-runs there any check it reports (here `islands-tests.puck`), and it does not boot the debug world itself (a lane boots no World).
Example PASS: "Merge the tip, rebuild `Puck.Physics`, re-run `islands-tests.puck` there, and hand back on those results; anything that needs the running world goes in your hand-back as a verification request."
Example PASS: "Merge `lead/fundamentals`, then rebuild and re-run `islands-tests.puck` on the merged head; report that, not this morning's results."

FAIL if the reply hands back on the pre-merge results, merges without rebuilding or re-running anything, or tells the lane to boot the debug world itself.
Example FAIL: "Merge the tip so the lead gets a clean merge, and hand back with the results you already have."
Example FAIL: "After the merge, rebuild `Puck.Physics` and boot the debug world headless once; report that."
