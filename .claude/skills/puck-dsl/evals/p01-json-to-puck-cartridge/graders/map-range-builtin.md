---
type: llm
weight: 1
---

The response collapses the 20-element all-zero array to map(range(0, 20), i => 0) or an equivalent range/map builtin, not 20 literal zeros.
