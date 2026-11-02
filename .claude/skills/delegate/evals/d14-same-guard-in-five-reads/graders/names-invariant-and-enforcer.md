---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the commit body or hand-back names the invariant (no read returns a cell its recipient may not see) and the one place that enforces it.
Example PASS: "In the commit body: invariant, no read returns an undisclosed cell; enforced by the one read door every read goes through."

FAIL if the reply names neither the invariant nor what enforces it, or offers the XML doc lines as the enforcement.
Example FAIL: "The XML doc on each method documents the guarantee for callers."
