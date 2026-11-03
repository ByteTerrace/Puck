---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the leg fails because the comparison mixes units: `door.speed` is now per second (24.0 per second is 0.4 per tick at 60 Hz, under the threshold) while `latch.engage-speed` is still a per-tick value (0.5), so the latch never engages.
Example PASS: "Units. 24.0/s is 0.4 per tick, which would latch, but it's compared against 0.5, a per-tick threshold. The threshold needs to be 30 per second."

FAIL if the reply names another cause (timing of tick 120, load in the test, a flake) or does not explain why the latch stays open.
Example FAIL: "The leg is probably flaky under load; re-run it once alone."
