---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply checks the partner's failure claim, or answers its contract questions, against the lead's local rewritten contract rather than the remote copy.
Example PASS: "Run their failing case against my local rewrite; if it passes, the failure was the old contract's."

FAIL if the reply reasons about the contract from the remote copy, or accepts the failure without checking it against the local code.
Example FAIL: "The remote shows it failing, so the contract is broken; let the partner finish its fix."
