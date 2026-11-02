---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply explains that review findings usually go back to the lane that wrote the code, which resumes in its own worktree with its context, while a removed worktree forces a fresh lane to rebuild that context.
Example PASS: "Highs usually go back to `rt-cull`, which resumes in place; without its worktree a new lane rebuilds the context from the brief."

FAIL if the reply gives no reason tied to the review's fix round.
Example FAIL: "Keep it in case you need to look at something."
