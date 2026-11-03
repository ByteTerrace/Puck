---
type: llm
weight: 1
---

The response does not tell the reader to commit on their own inference: the task did not mention commits.

PASS if it says not to commit (or stage) unless asked, to leave the edits uncommitted, or that a commit happens only if the brief or user asks for one. Example: "The task didn't ask for a commit, so leave the edits in the working tree; if your brief does call for one, make a docs-only commit."

FAIL if it tells the reader to commit the prose edits as a matter of course, with a commit message or staging steps, without a request for a commit. Example: "Commit the two prose edits on your branch with a `docs:` subject and run `puck docs links` first."
