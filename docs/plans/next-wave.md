# The next wave

The lane plan for [Next](open-items.md#next) items 0 to 3, ready to launch. It
starts only on the owner's explicit word (the owner's ruling). Three read-only
grounding passes checked every premise against the code at the main landing; each
holds a draft brief for its lanes:
[the frame thread](next-wave/ground-frame.md),
[memory](next-wave/ground-memory.md) and
[visibility](next-wave/ground-vis.md). Re-check a cited line before briefing
from it, since the tree moves. Delete this page and its folder once the wave
has consumed them.

## Lanes

Parallel lanes, with no shared files except where an order is named:

1. **field-caches** (specialist). Replace the effector probe ring and the
   camera clearance cache with one cache pattern. Fieldless probes skip the
   ring, and so do probes for planted feet and failed conditions
   (`WorldCreationInstances.Effectors.cs:82-98`, `:419-424`). Clearance is
   keyed with a stated tolerance. Batching the gradient's six taps
   (bit-identical per point, shared with physics) is a design step, not this
   lane. Proof: ring hit rate, probes per frame and march steps (counters),
   plus one short GPU leg at the meadow.
2. **relight-lockstep** (specialist). Under lockstep, quantize the sun on a
   fixed grid, never against the last capture (that comparison makes the image
   depend on when a run started). The sky term is under lockstep too. Proof:
   relights per tick, and a day-cycle sweep that completes.
3. **memory-deck** (specialist). Attribution first: about 8.4 GB is
   unattributed. Then:
   - the mesh library keeps claimed meshes findable and budgets only
     cached-only bytes;
   - the GI probe cache is bounded;
   - device-memory readings (DXGI `QueryVideoMemoryInfo`,
     `VK_EXT_memory_budget`) feed one budget authority, folding
     `ShaderPipelineMemoryBudget` and `MemoryAccount`.

   Budget: 10 GB on the Deck tier. Proof: a GPU leg and one budgeted cold bake.
4. **draw-identity, then V2b** (specialist). One per-index generation
   (`SelectionWord+11`) and one Observe method, honoured by the occlusion
   kernel; this folds the two identity comparisons.
5. **v2a** (implementer or specialist). Rate holds per registration; it pays
   on the lawn's far tiles. Pre-read `world.rates` first.
6. **static-copy** (a principal design note, then specialist). Static copies
   ride a fixed frame through `TransformDynamic`, keyed by their own slot's
   role and deduplicated by the mesh library, together with "grouping depends
   on spelling". The design note decides the slot numbering.
7. **dress** (specialist, after or coordinated with static-copy and V2b, which
   share `SdfMeshPathBake.Dress`). An unboxed motion identity, slot-indexed
   instances, and no `ToArray` per rebuild.
8. **Antigravity coding trial**: the `HeapAllocator` dedupe (five copies) and
   the Superellipsoid far field and small radius
   ([agent tooling](open-items.md#agent-tooling-and-skills)).

Later, with their designs done: the [rule-faults](rule-faults.md) steps, the
[transmission](transmission.md) lanes, and [disclosure](disclosure.md) steps
1-6 and 0b.

## Unlanded branches to triage first

Two finished-looking branches were found only on the lead machine and are
backed up on `origin`:

- `lane/companion-build` (its last commit says `Brief: companion-build
  done`): 61 files and about 2,800 lines. It carries:
  - role bindings and role-checked reservations;
  - a companion that follows its player across worlds and comes home as
    itself;
  - the pre-landing review's eleven findings fixed;
  - derived role-driver authority, and HMAC custody tokens for home
    re-adoption.

  It was merged with `lead/catch-up` at the time and has never reached
  integration. Likely [Next](open-items.md#next) item 10's companion steps:
  diff it against [the custody protocol](companion-redesign.md), merge the
  integration tip and land it, or record why not.
- `rtx4070/item2-wip`: two old WIP commits (a growable
  bake table with caps lifted, a display share). Probably superseded by the
  bake work since; confirm, then delete it.

## The settle window that closes the wave

It measures:

- the frame bar at every stop, on both backends;
- memory against the Deck budget;
- the meadow's session and nested-view per-pass costs;
- `world.rates` at the meadow and the lawn;
- GPU timing with poses moving and with poses held;
- the cold bake against its bar.

The settle-2 sweep baseline lives at
`%LOCALAPPDATA%/Puck/sweep-baselines/settle-2/` on the lead machine only;
another machine re-runs the sweep for its own.
