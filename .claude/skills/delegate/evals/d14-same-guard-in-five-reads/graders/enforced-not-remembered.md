---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply makes the one check impossible for a read to skip, naming how: the unchecked storage or reads become unreachable from addon-facing code (narrowed visibility, a recipient-scoped view or type that only the disclosure-checked path produces, the compiler refusing an unchecked read). A reply that settles for a shared helper each read calls also passes if it says plainly that nothing yet stops a new read from skipping the helper.
Example PASS: "Give addons a recipient-scoped view instead of `StateReads`; once the raw store isn't reachable from addon code, a sixth read can't skip the check."

FAIL if the reply presents a shared helper or predicate that each read must call, or the XML doc lines, as closing the leak for future reads, while unchecked access stays reachable and the reply does not say so.
Example FAIL: "Extract `IsDisclosed(recipient, cell)` and call it from all five reads; then the check lives in one place and a new read can't skip it."
