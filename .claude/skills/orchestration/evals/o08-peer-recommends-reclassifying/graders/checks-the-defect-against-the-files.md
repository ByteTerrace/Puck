---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply checks the reported defect against the current files before ruling.
Example PASS: "I read the cited file and line and reproduce the case before I decide whether it is a blocker."

FAIL if the reply rules from the review text or the peer lane without looking at the code.
Example FAIL: "The peer is closer to the code, so take its word."
