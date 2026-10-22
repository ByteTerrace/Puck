---
type: llm
weight: 1
---

The response says the bake's identity is computed from the code the bake reaches (its call closure, `SdfMeshBakeClosure`), so a new file the bake calls needs no list entry, and that stale meshes after a rebuild mean the new code is not reached by that walk (called only through reflection or a runtime callback on a type the bake never constructs, so it needs a root) or the World is running a stale build.
