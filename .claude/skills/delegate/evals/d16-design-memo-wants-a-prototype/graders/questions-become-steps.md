---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to turn the two open measurements into steps for another lane, each named with what it would show, and says the design lane does not prototype or measure them itself.
Example PASS: "Don't prototype. Write 'measure GPU vs CPU level selection on the 2060' and 'time compaction against 0.3 ms' as steps for an implementer, each with what it decides."

FAIL if the reply endorses the afternoon prototype or measurement by the design lane, even a small one.
Example FAIL: "A quick prototype is the fastest way to settle it; measure, then finish the memo."
