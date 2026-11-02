---
type: llm
weight: 1
---

The response says `Advance(n)` counts generator advances, not calls, so a seek distance has to be computed in advances actually consumed.

PASS example: "`Advance` counts advances, not calls; computing `n` as draws times cost per draw is wrong whenever a bounded draw rejects."

FAIL if it treats `n` as the number of draw calls, or never says what `n` counts. Example: "Count the drops in the segment and call `Advance(dropCount)`."
