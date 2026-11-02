---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the lead reads the lane's diff and what the lane ran to show the change working (its commit body or the hand-back check) before merging.
Example PASS: "Read the diff and the commit body's evidence of what it ran; run `puck landing handback` before it lands."

FAIL if the reply merges on the message, or names no inspection of the diff or of what the lane ran.
Example FAIL: "It says all green, so merge it and look at the diff later."
