---
type: llm
weight: 1
---

The response accepts that moving the replayed reduction off floats (to `Puck.Maths` fixed-point or integer math) may change the simulated value, treats that change as a deliberate correction, and has repeatability shown afterwards (for example with `puck test --reproduce`) rather than requiring the old float result to be kept.

PASS example: "Use the fixed-point primitive `maths-usage` names. If that deliberately alters the value, show it is repeatable with `puck test --reproduce` before measuring." A reply that says the state hash changes once and recorded baselines are re-recorded also passes.

FAIL if it requires the new code to reproduce the old float result bit for bit, keeps floats in the replayed reduction (a fixed-width float tree included), or never addresses that the value may change. Example: "Keep the float reduction but pin the order with `Vector256` so the existing replays still match."
