---
type: llm
weight: 1
---

The response warns that a mutable rate accumulator held in a `List<T>` element (or a readonly field or get-only property) is handed to `Integrate` as a copy, so its advances are silently lost, and says to keep it in an array slot, a field or a ref local.

PASS if it warns that an accumulator struct stored in a `List<Entity>` element is copied when read, so `Integrate` advances a copy, and says where to keep it instead. Example: "Don't call `Integrate` on a `List<T>` element: you get a copy and the remainder is lost; keep the accumulator in an array element, a field or a ref local."

FAIL if it never mentions the copy hazard, or says a `List<Entity>` element is fine to integrate in place. Example: "Store the accumulator in the `Entity` struct and call `Integrate` on `entities[i].Rate`."
