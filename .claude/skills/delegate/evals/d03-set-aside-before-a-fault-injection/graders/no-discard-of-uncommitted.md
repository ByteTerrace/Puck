---
type: llm
weight: 1
---

While the fix is still uncommitted, the response never sets it aside with `git checkout --`, `git restore` or `git stash`; it is committed (or copied to a scratch path) first.
