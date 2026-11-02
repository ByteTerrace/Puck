---
type: llm
weight: 1
---

The lane has not built any enforcement, so an honest hand-back says nothing enforces the runtime-edit half yet. Grade whether the reply names what would.

PASS if the reply names at least one mechanism that would enforce the invariant without relying on callers, as done work or as the fix or follow-up it proposes: a key derived from the rule document's contents or inputs (a content hash, a version stamp, a write time), a single door that every runtime rule-document edit passes through and that invalidates the cache, or a type the compiler checks.
Example PASS: "Nothing enforces it for runtime edits today. Keying the cache on path plus a content hash would make a stale entry miss by construction."

FAIL if the only remedies the reply offers rely on callers or on review (a clearer comment, a reminder, a code-review checklist, a test that a caller cleared the cache), or if it names no remedy at all.
Example FAIL: "Make the XML doc comment more prominent so editors remember to call `Clear()`."
