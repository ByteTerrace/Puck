---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to keep `rt-cull`'s worktree until its review is back and the review's findings are routed.
Example PASS: "Not yet. Keep it until the Codex review returns and its findings are placed; prune other merged lanes first."

FAIL if the reply removes the worktree now because the lane has landed.
Example FAIL: "It's merged, so remove it; the branch still has the commits."
