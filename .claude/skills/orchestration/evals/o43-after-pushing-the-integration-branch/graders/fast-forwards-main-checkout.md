---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to fast-forward the main checkout's branch to the pushed integration branch before launching the lanes.
Example PASS: "Leave the worktree, `git merge --ff-only lead/fundamentals` in the main checkout, come back, then launch."

FAIL if the reply launches without updating the main checkout, or updates only the lead's worktree.
Example FAIL: "You've pushed, so the lanes will pick up the new definitions; launch them."
