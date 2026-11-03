---
type: llm
weight: 1
---

The response gives the loot system its own generator stream derived from a master seed with a small stream id, and does not share one generator with other systems or reseed it to resume.

PASS if it gives loot its own `Pcg32XshRr` stream (a stream id derived from the master seed), persists that stream's state, and restores it rather than reseeding. Example: "Give loot its own stream from the master seed with a small stream id; snapshot its state and restore it on resume."

FAIL if it shares one generator with combat or other systems, or reseeds from the seed to resume. Example: "Use the combat generator for drops and reseed it on load."
