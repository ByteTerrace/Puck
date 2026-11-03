---
type: llm
weight: 1
---

The response says the structural leg's citation must state what the XML documentation claims (rounds half up) as well as what the code does.

PASS if it says the leg's citation records the documentation's claim (half up) beside the code's behavior (ties to even). Example: "The `DOC GAP` citation says the XML claims half-up rounding while the code rounds ties to even."

FAIL if it records only the code's behavior, or no citation, or does not say what the doc claims. Example: "Add a leg noting that the code rounds ties to even."
