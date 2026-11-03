---
type: llm
weight: 1
---

The response says the rewrite must preserve observable behaviour, including element order where simulation state or deterministic output depends on it, and does not require keeping the pipeline's deferred execution or exact exception types.

PASS if it says to keep the same order, filtering and results (and determinism where state depends on it), and either says eager evaluation or different exception types are fine or says nothing about them. Example: "The loop must produce the same elements in the same order; eager evaluation is fine."

FAIL if it requires the loop to keep deferred execution or the same exception types, or says order does not matter for simulation state. Example: "Keep the same exceptions on null and the same deferred execution, so nothing observable changes."
