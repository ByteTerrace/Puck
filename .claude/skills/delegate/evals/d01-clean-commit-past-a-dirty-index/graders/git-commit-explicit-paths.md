---
type: llm
weight: 1
---

Grade what the reply recommends, not whether it ran anything: the reply is advice, so it never makes the commit itself.

PASS if the reply recommends making the commit with porcelain `git commit` limited to the lane's own three files (a pathspec such as `git commit -- <paths>`, `git commit --only`, or staging only those files), so that the generator's staged files are neither committed nor lost.
Example PASS: "Commit only your paths with `git commit -- src/Puck.World/Rules/A.cs ...`; the generator's entries stay staged in the real index."

FAIL if the reply recommends `git commit` over the whole index (`git commit -a`, `git add -A` then commit), unstaging or discarding the generator's files to get a clean index, or offers no porcelain `git commit` route at all.
Example FAIL: "Run `git reset` to clear the index, `git add` your three files, then `git commit`." (the generator's staged state is lost)
