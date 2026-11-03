---
type: llm
weight: 1
---

The response adds only the one Default-tier law row and does not add a Smoke sentinel row or a Deep mirror row.

PASS if it lists a single law row at `Default` tier (silence about other tiers passes). Example: "Add one row to `laws/<family>.json` at `Default` tier, with its binding in `LawRegistry`."

FAIL if it adds a Smoke sentinel row or a Deep row that repeats the Default law with more operands. Example: "Add the Default row, a Smoke sentinel and a Deep mirror row drawing more random operands."
