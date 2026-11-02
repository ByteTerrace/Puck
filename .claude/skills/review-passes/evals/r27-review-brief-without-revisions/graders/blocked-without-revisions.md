---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the review is blocked because the brief names no base and head, and stops: it reports that (in the findings file or the hand-back) and asks the lead for the exact revisions instead of reviewing.
Example PASS: "The brief names no revisions, so the review is blocked. Write that to the findings path, name the range you need, and stop."

FAIL if the reply picks a range itself from the log (the culling commits, the last two days, a guessed base) and reviews it.
Example FAIL: "Take the oldest culling commit's parent as the base and `lead/fundamentals` as the head, then review."
