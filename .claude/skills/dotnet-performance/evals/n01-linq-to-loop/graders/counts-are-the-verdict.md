---
type: llm
weight: 1
---

The response judges the rewrite by load-independent evidence (allocated bytes, work counters or disassembly) and treats any wall-clock timing as supporting evidence beside it, never as the verdict alone.

PASS example: "Compare allocated bytes before and after first; then run a `puck bench` A/B as supporting evidence only."

FAIL if the decision rests on a stopwatch or benchmark timing alone. Example: "Run the benchmark before and after; keep the loop if it's faster."
