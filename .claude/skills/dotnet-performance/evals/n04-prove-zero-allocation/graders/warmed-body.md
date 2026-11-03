---
type: llm
weight: 1
---

The response says the measured body must be warmed up before the allocation is measured.

PASS if it says to run the body warmed (warmup ticks, so JIT, tiering and lazy init have settled) before the measured window. Example: "Warm the body first so JIT and first-use work don't count."

FAIL if it measures the first cold run, or never mentions a warmup. Example: "Run the tick once under the counter and assert the bytes."
