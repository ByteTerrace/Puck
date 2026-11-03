---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply merges the landed head into the dependent lane again once the other lane lands.
Example PASS: "After the first lane lands, the dependent lane merges the landed head again."

FAIL if the reply leaves the dependent lane on the old unpushed head after the other lane lands.
Example FAIL: "The dependent lane keeps its base; it needs nothing after the landing."
