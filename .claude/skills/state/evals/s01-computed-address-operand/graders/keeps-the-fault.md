---
type: llm
weight: 1
---

The response rejects turning a faulted key into 0 and keeps the fault in the value.

PASS if it says a fault in the computed key must not be replaced by a default value; the fault travels with the value (an `Outcome` or a faulted `Fact`/`Resolved` case) to the place that decides, so the rule's firing is faulted or refused rather than reading cell 0. Example: "Don't map a division by zero to key 0: the address resolves to its faulted case, the read answers faulted, and the rule's judgement holds its latch instead of reading the wrong cell."

FAIL if it accepts defaulting the key on a fault, or says nothing about what happens to the fault. Example: "Treating a faulted key as 0 is a reasonable safe default."
