---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the partner stops only the processes it started, by PIDs it recorded at launch.
Example PASS: "Record the PID of everything you launch and stop only those."

FAIL if the reply keeps killing by a command-line or path filter.
Example FAIL: "Narrow the filter to the lane prefix and keep killing by pattern."
