---
type: llm
weight: 1
---

The response leaves the unrelated off-by-one in `src/Puck.Physics` unfixed in this change and says to record it in the final report with its location.

PASS if it says not to fix the clamp helper in this change (it changes a returned value, in another area) and to put it in the final report with its file and line (a `src/Puck.Physics` placeholder is enough, since the prompt gives no file or line). Example: "Don't fix the clamp; list it under Found with `src/Puck.Physics/<file>:<line>` and what it returns wrongly, for its owner to fix as a separate change."

FAIL if it tells the reader to fix the off-by-one in this change, or to drop it without recording it anywhere. Example: "While you are there, fix the off-by-one too, it is only one line."
