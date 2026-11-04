---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says two full World test runs never run at once on one machine.
Example PASS: "Only one full `Puck.World.Tests` run at a time on that box; the others wait or queue."

FAIL if the reply lets several lanes run full World test runs together.
Example FAIL: "All five agents run their full World tests at once."
