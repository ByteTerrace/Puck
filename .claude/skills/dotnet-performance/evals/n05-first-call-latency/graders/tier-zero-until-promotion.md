---
type: llm
weight: 1
---

The response explains that a method runs unoptimized tier-0 code until call counting promotes it, after a call threshold and a delay, which is why the first calls in a fresh process are slow.

PASS if it says new methods start at tier 0 and are promoted to optimized code only after call counting (about 30 calls) and a background delay or quiet spell. Example: "A method runs tier-0 code until call counting promotes it; promotion waits on a delay timer that a busy startup keeps pushing back."

FAIL if it attributes the slowdown to something else only (GPU, caches), or says the kernel is optimized from the first call. Example: "The JIT compiles everything fully optimized at first call, so the cost must be elsewhere."
