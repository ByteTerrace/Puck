---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to read the code around each changed site and the callers, not only the author's claim or the diff hunks.
Example PASS: "Read each call site in full and every caller of the cache, not the claim."

FAIL if the reply works from the author's claim or the diff hunks alone.
Example FAIL: "Read the diff hunks and check them against the claim."
