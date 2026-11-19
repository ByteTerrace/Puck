---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply gives the security trust-boundary review to Carbon (`gemini-4-carbon`, the frontier model).
Example PASS: "The sign-in and token review takes Carbon, the frontier model and the strongest at security work."

FAIL if the reply gives the security review to a different Gemini 4 model, or leaves its model unnamed.
Example FAIL: "Neon for all three; it is the default."
