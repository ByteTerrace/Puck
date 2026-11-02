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
   relights per tick, and a day-cycle sweep that completes. The lane also
   takes the owner's night notes ([Lighting and GI](open-items.md#lighting-and-gi)):
   - the jump to night, when stale daytime bounce and slow exposure adaptation
     leave the scene black with glowing canopies;
   - the near-whole-frame changes about every 2.4 s;
   - a look for a captured moment of the dusk glow.

   The settled night that reads as a dim day is fixed as an authored standard
   night style (exposure curve, low-light desaturation and blue shift, the
   moon), with only the mechanisms in the engine.
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
   the Superellipsoid's far field ([certified field contact](open-items.md#certified-field-contact))
   and small radius ([the valley's terrain](open-items.md#the-valleys-terrain));
   the trial's scoring is in [agent tooling](open-items.md#agent-tooling-and-skills).
9. **deploy-fix** (implementer, small). `ReusePublishedContainerAsync` asks the
   registry for the known `<repo>:<commit>` tag and treats only a not-found
   answer as "not published". It never lists the catalog, which the ABAC
   publishing identity is deliberately denied
   ([Delivery](open-items.md#delivery-launcher-and-ci)).
10. **tree-research** (read-only, beside the wave). How good stylized and
    realistic trees get their structure and variety, applied through Puck's
    SDF bake, so a foliage lane starts from references
    ([Debug world content](open-items.md#debug-world-content)).

Later, with their designs done: the [rule-faults](rule-faults.md) steps, the
[transmission](transmission.md) lanes, and [disclosure](disclosure.md) steps
1-6 and 0b.

## Before the owner's go

- The lift waits on the owner's go.
- The skill-eval program finishes first, so the lanes start on proven
  `delegate` and orchestration skills
  ([agent tooling](open-items.md#agent-tooling-and-skills)).
- Design the player menu, player saves and the hub to worlds shape before any
  code ([the owner's rulings](../decisions/play.md#the-game)).
  1. A read-only grounding pass finds what already exists: identity and
     owned worlds, checkpoints and persistence, crossings and transfers, the
     avatar studio, `world.save`, `quit`, the wheel and the UI plan's tree
     ([done](next-wave/ground-hub.md)).
  2. A design memo reasons abstractly through:
     - the hub as the release's launching point;
     - character creation and selection;
     - player-driven hubs and world swapping;
     - what a player save holds, refined from what Puck already persists;
     - true pause only in solo sessions;
     - exit to menu and exit to desktop.

     The memo is [the hub, player saves and the player menu](hub-and-saves.md).
  3. Fable reviews the memo before any lane builds it.

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
