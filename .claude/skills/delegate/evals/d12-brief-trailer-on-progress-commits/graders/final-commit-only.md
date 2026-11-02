---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's commit messages put the `Brief:` trailer on the third, final commit only, with no `Brief:` line on the WIP or the fix commit.
Example PASS: "WIP: no trailer. Fix: no trailer. Docs commit, the last one: `Brief: bars-and-pick done`."

FAIL if the reply puts a `Brief:` line on the WIP or the fix commit (with `done` or any other value, such as `progress` or `wip`), or on none of the three.
Example FAIL: "End the WIP with `Brief: bars-and-pick wip` and the other two with `Brief: bars-and-pick done`."
