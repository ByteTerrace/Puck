---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the test run has already ended, citing what shows it (the leg `lod-hysteresis-band` crashing, the log's closing summary, or the job's exit code 3), so no `PASS` line is coming.
Example PASS: "The test is over. The log ends with `lod-hysteresis-band` crashing with 0xC0000005 and '7 passed, 1 crashed, 4 not run'; the job exited 3. The Monitors are waiting for a line that will never be written."

FAIL if the reply says the test is still running, still queued or still flushing output, or treats the exit code 3 as only the lease wrapper's.
Example FAIL: "The heavy lease is still queued behind other lanes; the test will print PASS once it gets its turn."
