---
type: llm
weight: 1
---

The response rejects using machine-width `Vector<T>` or `TensorPrimitives` float reductions for replayed simulation state (either because simulation state uses `Puck.Maths` fixed-point/integer math rather than floats, or because throughput alone never justifies non-deterministic reductions).
