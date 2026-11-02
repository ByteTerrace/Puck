---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the brief names the existing mechanisms the change must reuse (found while checking the premise) and says no second one may be added.
Example PASS: "Name the existing serializer context in the brief and say the lane extends it; no second context."

FAIL if the reply's brief names no existing mechanism to reuse.
Example FAIL: "Brief: add a context for the world document types and register it at boot."
