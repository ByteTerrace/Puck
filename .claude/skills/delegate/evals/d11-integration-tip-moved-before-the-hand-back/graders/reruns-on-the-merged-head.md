---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that after the merge the lane takes its evidence on the merged head before handing back: it rebuilds what it touched and boots the debug world there, and re-runs there any check it reports (here `islands-tests.puck`).
Example PASS: "Merge the tip, rebuild `Puck.Physics`, boot the debug world headless once on the merged head, re-run `islands-tests.puck` there, and hand back on those results."
Example PASS: "Merge `lead/fundamentals`, then rebuild and do your one headless boot of the debug world on the merged head; report that, not this morning's results."

FAIL if the reply hands back on the pre-merge results, merges without re-running anything, or re-runs only the scenario test in place of the boot on the merged head.
Example FAIL: "Merge the tip so the lead gets a clean merge, and hand back with the results you already have."
Example FAIL: "After the merge, rebuild `Puck.Physics` and re-run `islands-tests.puck`; report those results."
