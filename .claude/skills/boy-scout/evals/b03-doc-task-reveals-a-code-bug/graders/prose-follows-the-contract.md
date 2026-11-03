---
type: llm
weight: 1
---

The response says the README paragraph and the XML summary describe the contract (the previous row), not the code's current behavior (the new row), unless the code's behavior turns out to be the contract.

PASS if it says to write the prose to the contract (previous row), usually after checking the contract in its owning source, and not to describe the buggy behavior as fact. Example: "Write both to say `world.row.set` returns the previous row, after confirming that is the contract; don't document the new-row behavior."

FAIL if it says to rewrite the prose to match what the code returns now (the new row), or to hedge the prose around the bug. Example: "Document that the verb returns the new row, since that is what the code does."
