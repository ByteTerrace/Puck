---
type: llm
weight: 1
---

PASS if, in the steps the reply gives, the uncommitted fix is committed (or copied to a scratch path) before anything takes it out of the working tree, and the reply never sets it aside with `git checkout --`, `git restore` or `git stash` while it is still uncommitted.
Example PASS: "Commit the fix and the check as a WIP first. Then put the old `RateAccumulator.cs` back from the commit before it, run the check, and return the file to the WIP commit's version." (once the fix is committed, restoring a file from a commit discards nothing)

FAIL if any step removes the uncommitted fix with `git checkout --`, `git restore` or `git stash` (push, pop or apply), or if the reply offers one of those as an alternative.
Example FAIL: "`git stash push src/Puck.Maths/RateAccumulator.cs`, run the check, then `git stash pop`."
