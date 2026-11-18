---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that after bringing in the lead's new batch the lane takes its evidence on the merged head before handing back: it rebuilds and boots the debug world there, and runs there any test it reports (for example because the batch may add new uses of the old name in `src/Puck.World/Rules/`).
Example PASS: "Merge `lead/fundamentals` first; the batch touched `Rules/` and may use `MaxWorkUnitsPerTick` too. Then rebuild `Puck.World`, boot the debug world headless once and run `rules-tests.puck`, all on the merged head."

FAIL if the reply hands back on results from the branch as it was this morning, leaves the lead's new batch to the lead to reconcile, never addresses the batch at all, or runs only the scenario test in place of the boot on the merged head.
Example FAIL: "Rename the two scheduler lines, rebuild, run `rules-tests.puck`, commit and hand back; the lead merges your branch into `lead/fundamentals`."
Example FAIL: "Merge `lead/fundamentals`, rebuild `Puck.World` and run `rules-tests.puck` on the merged head; that is your check."
