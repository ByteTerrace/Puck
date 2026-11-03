---
type: llm
weight: 1
---

The response says repeatable, bit-identical results must be shown by running (for example `puck test --reproduce`, or the same input under different instruction-set settings) before any speedup is measured or claimed.

PASS if it says repeatability is proved first and the speedup measured after. Example: "Prove two runs agree with `puck test --reproduce` before you time the vectorized version."

FAIL if it never says repeatability is checked before the speedup, or goes straight to the speedup. Example: "Vectorize it and benchmark; if it's faster, keep it."
