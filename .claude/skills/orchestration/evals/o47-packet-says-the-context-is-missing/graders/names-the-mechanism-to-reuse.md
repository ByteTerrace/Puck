---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to find the existing mechanism (an existing serializer context or options holder) and have the change reuse or extend it, adding no second one.
Example PASS: "If a context already exists, the lane extends it; a second context is a duplication defect."

FAIL if the reply's brief adds a new context without first looking for an existing one to reuse.
Example FAIL: "Brief: add a context for the world document types and register it at boot."
