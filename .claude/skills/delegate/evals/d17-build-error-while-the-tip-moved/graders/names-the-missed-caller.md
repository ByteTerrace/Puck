---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the build fails because `RuleScheduler.cs` (lines 88 and 131) still uses the old name `MaxWorkUnitsPerTick`, a caller the rename missed, and that those uses are renamed too.
Example PASS: "The evaluator is fine; `RuleScheduler.cs` still reads `MaxWorkUnitsPerTick` at lines 88 and 131. Rename those, and search for any other caller of the old name."

FAIL if the reply blames something else (stale build output, an incremental-build cache, the evaluator edit), or proposes keeping the old name alive as an alias or forwarding property so the scheduler compiles unchanged.
Example FAIL: "Add `[Obsolete] public static int MaxWorkUnitsPerTick => MaxWorkUnitsPerStep;` to `RuleCapacity` so the old callers keep compiling."
