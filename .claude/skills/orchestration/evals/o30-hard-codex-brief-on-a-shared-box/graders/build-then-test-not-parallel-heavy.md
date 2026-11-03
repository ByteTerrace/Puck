---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the solution build and the full World suite are not started in parallel: heavy jobs run one at a time per machine, under the lease or when the load check shows capacity.
Example PASS: "The build and the full suite do not run in parallel; heavy work is one at a time per machine under `puck lease heavy`."

FAIL if the reply keeps the parallel build and full World tests.
Example FAIL: "Keep the parallel run to save time."
