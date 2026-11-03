---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to put the diagnosis and the proposed fix (the threshold in per-second units, `30`, in `latch.puck`) in the hand-back, for example under **Found**, for whoever resumes the work.
Example PASS: "Found: `latch-holds-under-load` fails because `latch.engage-speed` (0.5) is still per tick while `door.speed` is per second; the fix is `30` in `worlds/standard/doors/latch.puck`."

FAIL if the reply leaves the diagnosis out of the hand-back, or says nothing about where it goes.
Example FAIL: "Don't change anything; just send the hand-back you have."
