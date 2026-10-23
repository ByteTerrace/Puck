---
type: llm
weight: 1
---

The response rejects any float reduction for replayed simulation state, machine-width `Vector<T>` and `TensorPrimitives` included, because simulation state uses `Puck.Maths` fixed-point or integer math, never floats. Objecting only that the reduction is nondeterministic, or proposing a deterministic float formulation for the replayed state, fails.
