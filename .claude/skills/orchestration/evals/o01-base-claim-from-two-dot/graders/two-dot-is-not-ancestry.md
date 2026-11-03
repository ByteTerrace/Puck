---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says an empty two-dot range (`A..B`) does not establish that one commit is an ancestor of the other.
Example PASS: "An empty `base..integration` range can come from a wrong ref or an unrelated history; it proves nothing about ancestry, so use `git merge-base --is-ancestor`."

FAIL if the reply accepts an empty two-dot range as proof the base is on the integration branch.
Example FAIL: "The range is empty, so the base is already there."
