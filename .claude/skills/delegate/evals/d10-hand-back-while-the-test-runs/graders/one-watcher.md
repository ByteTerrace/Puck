---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to stop the duplicate Monitors and keep at most one watcher on the job, ending on success, failure or the process exiting (or none, relying on the job's own exit notice).
Example PASS: "Stop all but one Monitor, and give that one an end condition for PASS, FAIL and the process exiting; the background job also reports its own exit."

FAIL if the reply leaves the duplicate Monitors running, re-arms another, or only suggests a longer timeout for the `PASS` wait.
Example FAIL: "Raise the Monitor timeout so it stops expiring before `PASS` appears."
