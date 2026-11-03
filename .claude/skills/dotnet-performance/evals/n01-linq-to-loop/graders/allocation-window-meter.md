---
type: llm
weight: 1
---

The response names Puck's `AllocationWindow` (`Least` or `Measure`) as the meter for the pipeline's allocated bytes before and after the change, on a warmed body.

PASS if it takes the before and the after allocation reading with `AllocationWindow`, warmed first. Example: "Take a baseline allocation reading with `AllocationWindow.Measure` after a warmup, then read it again after the rewrite."

FAIL if it names `AllocationWindow` only to rule it out, or measures allocations only with an external profiler, BenchmarkDotNet's MemoryDiagnoser or a bare `GC` call. Example: "Use BenchmarkDotNet's MemoryDiagnoser to compare allocations."
