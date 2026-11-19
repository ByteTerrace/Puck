---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says a red result from the batch window goes back to the lane that owns the failing change, as ordinary work, rather than reverting the batch or treating the landing as blocked.
Example PASS: "If the benchmark reads a regression on the shader, `rim-light` gets it back as a fix; the batch stays landed."

FAIL if the reply says nothing about what happens to a red window result, or says a red result un-lands the batch or blocks the next one.
Example FAIL: "If the window goes red, back out the whole batch and re-land."
