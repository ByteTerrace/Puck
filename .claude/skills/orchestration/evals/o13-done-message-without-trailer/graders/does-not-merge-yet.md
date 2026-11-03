---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply does not merge on the lane's message alone.
Example PASS: "Not on the message: I check the commit, the diff and what the lane ran first."

FAIL if the reply merges the lane because it reported done and green.
Example FAIL: "It says all green, so merge it."
