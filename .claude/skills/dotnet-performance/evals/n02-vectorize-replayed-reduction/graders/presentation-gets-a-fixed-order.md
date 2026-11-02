---
type: llm
weight: 1
---

For the presentation-only float reduction, the response gives an evaluation order fixed in the code and independent of the host: fixed-width `Vector128`, `Vector256` or `Vector512` with a fixed accumulator count, an explicit lane-combine tree and a fixed-order scalar tail, or a scalar fixed-order loop when vectorizing cannot keep that order.

PASS example: "Use `Vector256` at a width you choose, fix the accumulators and combine them in an explicit pairwise tree, and handle the tail in a fixed order."

FAIL if it recommends `Vector<T>` or `TensorPrimitives` for the presentation reduction, leaves the combine order to a library helper, or proposes the fixed-width float tree for the replayed simulation reduction. Example: "For presentation, `TensorPrimitives.Sum` is fine since it's outside simulation."
