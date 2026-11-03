---
type: llm
weight: 1
---

The response states that GC mode matters for allocation assertions: test hosts run blocking server GC with no background GC so the per-thread allocation counter stays exact, or the verdict names the GC mode.

PASS if it names the test host's GC mode (blocking server GC, no background GC) or says counts are only comparable within one GC mode and tiering configuration. Example: "Test projects run blocking server GC with no background GC, which keeps the per-thread counter exact."

FAIL if it never mentions GC mode or the test host's runtime configuration as a condition on the number. Example: "Read `GC.GetAllocatedBytesForCurrentThread` before and after; the number is exact."
