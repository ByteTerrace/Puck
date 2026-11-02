---
type: llm
weight: 1
---

The response says an alias-table sample consumes exactly two generator advances (a fixed cost), in prose or in a table.

PASS example: "Alias-table sample: exactly two advances per sample, so it is safe to skip with `Advance`."

FAIL if it gives another count, calls the alias sample variable-cost, or never states its cost. Example: "The alias sample may reject like the bounded draw."
