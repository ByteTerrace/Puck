---
type: llm
weight: 1
---

PASS if the reply says not to build the commit with `git read-tree` and `git commit-tree` (Git plumbing), and offers no other plumbing route that writes a tree or moves the branch by hand.
Example PASS: "No. Never build a commit from plumbing; a bad temporary index can write an empty or unrelated tree. Use `git commit` over an index you inspected."

FAIL if the reply endorses the plumbing plan, even with safety corrections, or offers another route through `git write-tree`, `git commit-tree`, `git update-ref` or a temporary `GIT_INDEX_FILE`, even as an alternative.
Example FAIL: "Yes, the plan works if you seed the temporary index from HEAD first: `git read-tree HEAD`, then `git commit-tree` and `git update-ref`."
