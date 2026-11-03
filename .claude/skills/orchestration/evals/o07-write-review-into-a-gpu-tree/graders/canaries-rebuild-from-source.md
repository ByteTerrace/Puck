---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply gives the reason: canaries rebuild from source, so edits to the tree during the run change what it measures.
Example PASS: "The canary rebuilds from the worktree, so an editing pass in the same tree changes what the run measures."

FAIL if the reply gives a different reason or none.
Example FAIL: "The review pass is read-heavy, so it is probably fine."
