---
type: llm
weight: 1
---

The response measures the pipeline's allocated bytes before and after the change with Puck's `AllocationWindow` (`Least` or `Measure`) on a warmed body.

PASS example: "Take a baseline with an allocation law using `AllocationWindow.Least`, warmed first, then re-run it after the rewrite."

FAIL if it names `AllocationWindow` only in passing or to rule it out, or measures allocations only with an external profiler or a generic `GC` call. Example: "Use BenchmarkDotNet's MemoryDiagnoser to compare allocations."
