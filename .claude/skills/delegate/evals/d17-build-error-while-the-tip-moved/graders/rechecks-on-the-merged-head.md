---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that after bringing in the lead's new batch the lane takes its evidence on the merged head before handing back: it rebuilds there and runs there any test it reports (for example because the batch may add new uses of the old name in `src/Puck.World/Rules/`), and it does not boot the debug world itself (a lane boots no World).
Example PASS: "Merge `lead/fundamentals` first; the batch touched `Rules/` and may use `MaxWorkUnitsPerTick` too. Then rebuild `Puck.World` and run `rules-tests.puck`, both on the merged head."

FAIL if the reply hands back on results from the branch as it was this morning, leaves the lead's new batch to the lead to reconcile, never addresses the batch at all, or tells the lane to boot the debug world itself.
Example FAIL: "Rename the two scheduler lines, rebuild, run `rules-tests.puck`, commit and hand back; the lead merges your branch into `lead/fundamentals`."
Example FAIL: "Merge `lead/fundamentals`, rebuild `Puck.World` and boot the debug world headless once on the merged head."
