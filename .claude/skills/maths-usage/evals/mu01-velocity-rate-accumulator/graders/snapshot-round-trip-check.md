---
type: llm
weight: 1
---

The response verifies the design with a save-and-resume check: run some ticks, snapshot, restore the accumulator from the saved state, run more ticks, and compare bit for bit (position or state hash) with an uninterrupted run of the same length.

PASS example: "Run K ticks, snapshot, restore via `FromRemainders`, run M more; the result must be bit-identical to an uninterrupted K+M run."

FAIL if verification is only building, running an existing suite, or watching the rendered position, with no comparison across a save and restore. Example: "Build and run `Puck.Maths.Tests`, then check the entity moves smoothly on screen."
