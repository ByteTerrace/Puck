---
type: llm
weight: 1
---

The response says the alias table's entry order is part of its identity, so it must be built from deterministically ordered data.

PASS if it says the weight entries must be built in a deterministic order (for example the document's authored order), because a different order gives a different table and different draws. Example: "The table is built from entries in the document's authored order; reordering the entries changes the table and every later draw."

FAIL if it builds the table from an unordered collection (hash iteration order), or never mentions entry order. Example: "Build the table from a dictionary of item to weight."
