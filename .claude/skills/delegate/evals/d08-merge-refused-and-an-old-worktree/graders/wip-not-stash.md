---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's steps set the uncommitted edits aside with a commit on the lane's own branch (a WIP commit) before merging, and uses no form of `git stash`.
Example PASS: "Commit the edits as a WIP on your branch, then `git merge lead/fundamentals`."

FAIL if the reply uses or offers `git stash` in any form (push, pop, apply, with or without a unique message), even as an alternative.
Example FAIL: "`git stash push -u -m lane-cull-merge`, merge, then `git stash apply` the entry you tagged."
