---
type: llm
weight: 1
---

The response says `TensorPrimitives` promises no evaluation order for its reductions, so a float result can differ by instruction set or library version.

PASS if it says `TensorPrimitives` (or `Vector<T>` helpers) pick their own widths and combine order, so a float reduction may differ between machines. Example: "`TensorPrimitives.Sum` doesn't promise a summation order, so the float result can change with the CPU."

FAIL if it treats `TensorPrimitives` as giving the same float result on every host. Example: "`TensorPrimitives.Sum` is deterministic, so it is safe here."
