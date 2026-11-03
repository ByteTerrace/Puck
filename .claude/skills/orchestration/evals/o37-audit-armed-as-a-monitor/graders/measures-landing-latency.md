---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply measures the time from a lane's last commit to its landing on the integration branch.
Example PASS: "Measure from the lane's last commit to its landing on the integration branch."

FAIL if the reply measures something else (load, test duration) and not the landing latency.
Example FAIL: "Measure how long builds take."
