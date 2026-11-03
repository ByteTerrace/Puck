---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply treats the collision as a real conflict between two contracts, not a textual merge conflict resolved by taking one side.
Example PASS: "This is two contracts that cannot both hold, so it needs a ruling, not a conflict-marker resolution."

FAIL if the reply resolves it as a merge conflict by taking one side.
Example FAIL: "Take the incoming side of the conflict and move on."
