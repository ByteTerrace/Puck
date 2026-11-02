---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to look first for a recorded contract the answer follows from (`docs/decisions/`, the plan's requirements, the rule and journal semantics already decided) before treating it as the owner's question.
Example PASS: "Check `docs/decisions/` for the firing's atomic-scope rule first; the answer may already be implied."

FAIL if the reply goes straight to the owner, or has the lane pick an answer, without looking for a recorded contract.
Example FAIL: "Queue it for the owner tomorrow and have the lane work on something else meanwhile."
