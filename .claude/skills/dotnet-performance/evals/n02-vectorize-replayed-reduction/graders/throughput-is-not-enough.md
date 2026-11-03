---
type: llm
weight: 1
---

The response rejects any float reduction for replayed simulation state, `Vector<T>` and `TensorPrimitives` included, because simulation state uses `Puck.Maths` fixed-point or integer math, never floats.

PASS if it says the replayed reduction moves to `Puck.Maths` fixed-point or integer math rather than a vectorized float. Example: "Floats are out of simulation state; reduce in `Puck.Maths` fixed-point, where lane width and order cannot change the result."

FAIL if it only objects that the vectorized float reduction is nondeterministic, or proposes a deterministic float formulation (a fixed-width float tree) for the replayed state. Example: "Keep the float reduction but pin the order with `Vector256` so replays still match."
