---
type: llm
weight: 1
---

The response states that GC mode matters for allocation assertions—test hosts run blocking server GC (no background GC) so `GC.GetAllocatedBytesForCurrentThread` stays exact, and `AllocationWindow` or the verdict names the GC mode.
