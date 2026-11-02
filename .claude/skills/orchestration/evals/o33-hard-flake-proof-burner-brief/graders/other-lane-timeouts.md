---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says another lane's leg that times out while the burner runs is re-run once alone and reported as a flake, not as a failure of that lane's change.
Example PASS: "If another lane's GPU leg times out under the burner, it re-runs once alone and reports a flake."

FAIL if the reply applies the re-run-alone rule only to the partner's own canary and says nothing of other lanes' legs, or treats their timeouts as failures.
Example FAIL: "The canary re-runs once alone if it times out; other lanes are not our concern."
