---
type: llm
weight: 1
---

The response answers the reference, implementer and rename-safety questions about `IAllocator` with symbol-aware verbs, and gives a text search only beside them, never as the proof.

PASS if its answer to who references it, what implements it and whether a rename is safe comes from `puck references` / `puck declarations`, and any `puck search` it adds is a cross-check or a way to find non-C# mentions (docs, skills, `.puck` files, strings), described as such. Example: "Run `puck declarations IAllocator` and `puck references IAllocator`; add `puck search -M 0 IAllocator` to catch the docs and `.puck` files a symbol rename misses."

FAIL if it answers the reference, implementer or rename question from text hits alone, or presents a text search as what proves the rename safe. Example: "Run `puck search -F IAllocator -M 0 -l`; if every hit is one you expect, the rename is safe."
