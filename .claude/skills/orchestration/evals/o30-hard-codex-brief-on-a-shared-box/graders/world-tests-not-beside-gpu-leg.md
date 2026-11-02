---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply keeps the full World test run from running on the box while the granted GPU leg runs, and lets ordinary CPU builds run beside the leg.
Example PASS: "Focused builds can run beside the GPU leg; the full World suite waits until the windowed World exits."

FAIL if the reply schedules the full World tests beside the granted GPU leg, or stops all builds for a correctness leg.
Example FAIL: "Run the full World tests now beside the GPU leg; they are CPU work."
