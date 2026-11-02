---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's steps bring the tip in with a merge into the lane's branch and recommends no rebase.
Example PASS: "`git merge lead/fundamentals` into your branch, then rebuild and re-run."

FAIL if the reply recommends rebasing onto `lead/fundamentals` (or resetting onto it), even as an equal alternative.
Example FAIL: "`git rebase lead/fundamentals` so your commits sit on top of the batch."
