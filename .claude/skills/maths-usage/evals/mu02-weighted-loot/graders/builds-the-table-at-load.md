---
type: llm
weight: 1
---

The response builds the weighted-sampler table once, when the world document's weights are loaded, and reuses it for every drop.

PASS example: "Build it once at world load, not per combat or per drop."

FAIL if it builds the table per draw or per combat, or recomputes cumulative weights at each drop. Example: "On each kill, sum the weights and scan for the drawn value."
