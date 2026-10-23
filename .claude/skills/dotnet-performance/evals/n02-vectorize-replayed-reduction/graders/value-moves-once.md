---
type: llm
weight: 1
---

The response explains that replacing the replayed float reduction with `Puck.Maths` fixed-point or integer math may change the result or state hash once, as a deliberate correction, after which runs stay bit-identical. A response that keeps floats in the replayed reduction, even as a fixed-width float tree, fails; fixed-width float advice counts only for the presentation-only reduction.
