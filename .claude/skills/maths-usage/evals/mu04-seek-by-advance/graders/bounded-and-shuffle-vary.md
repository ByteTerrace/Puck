---
type: llm
weight: 1
---

The response says the bounded `NextUInt32(min, max)` draw and `Shuffle` can reject and consume extra advances, so `Advance`-based seeking over them is not exact.

PASS if it says those draws have a variable number of advances (they can reject and redraw), so a fixed `Advance(n)` cannot match them. Example: "`NextUInt32(min, max)` and `Shuffle` may reject and consume extra advances, so `Advance` over them isn't exact."

FAIL if it treats `Advance(dropCount)` as exact for bounded draws and shuffles, or never mentions rejection. Example: "Each draw is one advance; call `Advance(number of draws)`."
