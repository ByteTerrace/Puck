---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply gives the fully specified rename to Hydrogen (`gemini-4-hydrogen`), the fast execution tier for specified work.
Example PASS: "The rename is fully specified, so Hydrogen, the fast execution tier, takes it."

FAIL if the reply gives the specified rename to Carbon, Neon or Argon, or leaves its model unnamed.
Example FAIL: "Carbon, to be safe on a repository-wide change."
