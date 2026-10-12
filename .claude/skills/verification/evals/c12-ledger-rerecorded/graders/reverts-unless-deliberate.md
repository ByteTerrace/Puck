---
type: llm
weight: 1
---

The response reverts the rewritten files unless the change deliberately moved the schema, in which case it reviews the diff and commits it in the same change with the reason.
