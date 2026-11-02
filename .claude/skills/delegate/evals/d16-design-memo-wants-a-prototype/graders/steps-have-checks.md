---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the memo's implementation steps each carry a mechanical check that shows the step worked.
Example PASS: "Each step gets a check, for example 'counter X equals the CPU's chosen levels on the hall stop'."

FAIL if the reply gives steps with no checks, or no implementation steps at all.
Example FAIL: "List the implementation phases in order; reviewers will judge when each is done."
