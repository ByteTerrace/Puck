---
type: llm
weight: 1
---

The response says a formatting sweep over the other files under `src` is its own separate change, not part of this one.

PASS if it says a wider formatting pass, if wanted, is a separate change (commit, lane or report), not folded into this one. Example: "Leave the spacing elsewhere alone; a sweep of `src` is its own change and would bury your three-file diff."

FAIL if it folds the sweep into this change, or does not say what to do with the other files' spacing. Example: "Run the formatter over all of `src` in the same commit; it only touches whitespace."
