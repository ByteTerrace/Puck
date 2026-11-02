---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says that a specific concern the lane has noticed goes under **Found** in the hand-back (with its location) rather than into an extra round of changes.
Example PASS: "If you suspect an unguarded path, list it under Found with its file and line."

FAIL if the reply says nothing about where noticed concerns go, or tells the lane to fix them in more rounds before handing back.
Example FAIL: "Fix anything the self-review turns up, then hand back."
