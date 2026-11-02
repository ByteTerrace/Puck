---
type: llm
weight: 1
---

Grade what the reply says the lane should do; the reply is advice and cannot itself edit anything.

PASS if the reply says that a failure the rename or move caused (a link to the old `Fields/` path, a citation of `FieldScopeTable`) is the lane's to fix in this change, before its final commit, even when it sits in a page the lane never edited.
Example PASS: "Fix every failure your change introduced. A link to the old path is yours even in a page you didn't edit."

FAIL if the reply treats every failure in an untouched page as someone else's (reports or hands back all of them, including ones the rename caused), or defers the rename's own breakage to a later change.
Example FAIL: "Pages you never touched aren't your lane's; list every failure in them in your hand-back and commit."
