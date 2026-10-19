---
type: llm
weight: 1
---

The response warns that a mutable rate accumulator held in a List<T> element (or a readonly field or get-only property) is handed to Integrate as a copy, so its advances are silently lost, and says to keep it in an array slot, a field or a ref local.
