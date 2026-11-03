---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply starts the dependent lane on the lead machine's local agents.
Example PASS: "The dependent lane starts on a local agent on the lead machine, where the unpushed head is."

FAIL if the reply starts it on a partner that cannot see the unpushed lane.
Example FAIL: "Give it to the cloud partner and have it fetch the other lane."
