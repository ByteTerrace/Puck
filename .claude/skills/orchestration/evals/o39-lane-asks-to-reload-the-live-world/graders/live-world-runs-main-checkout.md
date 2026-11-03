---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the live World runs the main checkout's build and sources, so reloading it would not show the lane's worktree edit.
Example PASS: "The live World runs the main checkout, so `world.reload` there does not show an edit in the lane's worktree."

FAIL if the reply says the reload would show the lane's edit, or does not say what the live World runs.
Example FAIL: "Reload it; the live World picks up the lane's shader."
