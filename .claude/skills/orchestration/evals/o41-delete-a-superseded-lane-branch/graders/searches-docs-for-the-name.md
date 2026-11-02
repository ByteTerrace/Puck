---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to search the repository's documents (`docs/`, the plans and open items, `.claude/`) for the branch's name before deciding, because a search of the code cannot show a plan that keeps the branch as prior art.
Example PASS: "Search `docs/` and `.claude/` for `hzb-cull-v1` first: a plan may keep it as prior art even though no code references it."

FAIL if the reply decides on the code search and Git state alone (merged or not, `git cherry`, open lanes) without searching the documents.
Example FAIL: "Nothing references it and no lane uses it, so it's safe to delete."
