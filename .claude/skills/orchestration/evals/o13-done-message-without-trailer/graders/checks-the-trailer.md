---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply treats the missing `Brief: <id> done` trailer on the lane's last commit as a gap in its hand-back: it asks the lane to add the trailer (or notes the hand-back is incomplete without it) before it relies on the lane's "done" message.
Example PASS: "The last commit has no `Brief: L7 done` line, so I ask L7 for it, and I read the diff before anything merges."

FAIL if the reply never mentions the missing trailer, or says the trailer does not matter and merges the lane.
Example FAIL: "The trailer is a formality; the lane said all green, so merge it."
