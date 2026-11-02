---
type: llm
weight: 1
---

The response says text matches of `IAllocator` do not establish who references it, what implements it, or whether a rename is safe; any text search it gives is a first pass or cross-check beside the semantic answer.

PASS if it says so in any words. Example: "This text search only shows where the name appears. It doesn't prove anything about references; `puck references` and `puck declarations` answer that."

FAIL if it answers the reference, implementer or rename question from text hits alone, or presents a text search as the proof. Example: "Run `puck search -F IAllocator -M 0 -l`; if every hit is one you expect, the rename is safe."
