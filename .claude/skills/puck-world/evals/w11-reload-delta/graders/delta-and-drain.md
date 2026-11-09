---
type: llm
weight: 1
---

The response identifies WorldDocumentBasis.Diff/TryMerge and WorldReloadDelta rather than proposing a second diff, a cap increase, or chunked whole-document sends. It names authority version and base/result content pins, preparation and apply-time base checks, and world.reload.stale-base or world.reload.base-mismatch. It keeps ApplyRebuild's state semantics distinct from InstallKindOf's presentation mutation and acknowledges remaining whole-document allocation/validation work. Its own headless debug-world proof edits and restores the palette, reads it through world.row, checks the success echo's payload bytes, stops its World, and queues an immediate read after world.reload before waiting on the deferred answer when driving open stdin. It makes no unmeasured edit-to-running timing claim.
