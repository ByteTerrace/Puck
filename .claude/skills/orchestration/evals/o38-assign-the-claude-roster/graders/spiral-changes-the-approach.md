---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply treats the two failed lanes as a spiral and changes the approach: a `principal` (Fable) lane steps back over the hashing approach with the record of what the earlier lanes tried, whether it then runs the diagnosis itself or hands specified steps to `specialist` or Astra.
Example PASS: "Two lanes failed, so `principal` takes it with both reports: first whether the hash covers what drives play, then the diagnosis on the approach that holds."

FAIL if the reply starts a third lane on the same footing as the two that failed (the same role and approach, without the earlier reports or a change of approach).
Example FAIL: "Give it to another `specialist` lane to reproduce on both machines and bisect again."
