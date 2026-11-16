# Streaming and memory

Residency is how Puck exists at the scale it aims for: when every zone,
character and server is a world, a player's machine holds what that player can
observe and little else. [Residency follows what is observed](../decisions/residency.md)
settled the design; this plan builds it, with the memory work inside it rather
than beside it, and states how the frame, bake and memory bars are read so
that quality and performance stop pulling against each other.

The owner's direction: streaming has not had "the proper focus/priority"; memory
usage and handling "could be SIGNIFICANTLY improved if we simply tried"; the
bars are "a good forcing function, but they end up hurting us sometimes too".
The owner agreed to this plan's shape: a research round, then streaming's
remaining steps with the memory work folded in.

## How the bars are read

Most of the conflict between the quality and performance bars has been
measurement, not trade-off: a comparison of two kinds of run, a machine loaded
by other lanes, a display that had turned off and dropped the GPU to idle
clocks. So:

- **One recipe for both bars.** The same build state, a quiet machine
  (`puck lease quiet`), the display on and composing, the same camera path,
  and the frame, bake and memory readings taken in the same window. A quality
  change and its cost are judged together, like with like.
- **A regression is a design problem, never a reason to drop quality.** When a
  look costs too much, the cost moves to where a viewer looks: detail demanded
  by what a view resolves ([bake demand tiers](bake-demand-tiers.md)), not
  detail removed.
- **Budgets per system.** Each place and system owns a share of the frame, the
  bake and memory, read through `world.budget` and `host.memory`. A change that
  needs more finds it within its system or makes its case; a regression has an
  owner instead of blocking every lane.
- **Gates where readings are stable, trends where they drift.** Frame time at
  fixed stops and bytes per entity are stable enough to gate on. The
  whole-world cold bake and peak memory drift with content and machine state;
  they are watched as trends, and the gated form of each is per unit of work
  (bake time per million samples, resident bytes against the budget).

## Where residency stands

The decision's order of work, as built:

| Step | State |
|---|---|
| Measure | Partial: time until near groups land and bytes by level are read (`world.residency`); no device memory budget is read (no DXGI `QueryVideoMemoryInfo` or `VK_EXT_memory_budget` in `src/`) |
| Near-first ordering | Built: `SdfMeshDemand`, and detail bounded by what each view resolves (`SdfMeshTier`) |
| Lighting on the same demand points | Not built: the probe window follows the first view |
| Demand mapped through portal faces | Not built: an unseen window renders at the least share while its world's meshes arrive (`WorldViewGraphHost.ReserveArrivals`), the stand-in this step replaces |
| Eviction under a byte budget | Not built: the mesh region coarsens under pressure, nothing is evicted by priority, and a refused growth still ends the World |
| Authored areas | Not built |
| Terrain tiles, then the moons | Not built |

`MemoryAccount` budgets three quarters of installed memory and sees only mesh
payloads and bake workspaces: not the GPU's share, the managed heap, `GpuRegion`
host copies, or the staging heaps. On a unified-memory device it cannot see
half of what it is meant to bound. The owners a settled warm World holds, and
the leads on each, are in the open items'
[memory on the Deck tier](open-items.md#memory-on-the-deck-tier).

## The campaign

Each step lands on its own measurement and is taken in this order unless a
reading reorders it.

1. **Research round.** Several families, one focus each, cited sources: MMO
   zone and world streaming as shipped (World of Warcraft and its successors),
   GPU virtual geometry and virtual texturing with page streaming and
   feedback, and memory budgets on unified-memory handhelds. The synthesis
   weighs the Puck-native options beside them: caches identified from their
   inputs, routed worlds, demand mapped through portal faces, and detail
   demanded by what a view resolves. An adversarial review from another family
   precedes the first build lane.
2. **One byte authority with device readings.** Read the device's usage and
   budget (DXGI and Vulkan, two independent sources each), make `MemoryAccount`
   the one budget authority across native, managed, device and staging bytes,
   and make the shader pipeline's budget a reservation from it.
3. **Attribution at fixed stops.** Every resident gigabyte named, cold and warm,
   at the sweep's stops on both backends: `host.memory collect`, the GPU ledger,
   a page walk and a suspended heap walk. The open items' owner table becomes
   counters a window reads.
4. **Cut duplicate copies.** A CPU copy is released once its GPU copy is
   resident wherever it can be rebuilt or re-read: mesh payloads, the lighting
   trace scene's CPU hierarchy (once probe placement reads the GPU trace), the
   composed-document store after boot. The lighting traces mesh levels whose
   error fits the probe spacing instead of full detail.
5. **Eviction by priority under the budget.** The selector's priorities decide
   what leaves, coarse levels own compacted vertices so dropping a fine level
   frees its vertices, and a refused growth lowers the limit and coarsens
   instead of ending the World. A miss degrades only the look and is counted.
6. **Demand mapped through portal faces.** A window's destination is demanded
   from the viewer's eye taken through the face, and the least-share renders of
   unseen windows are retired with it.
7. **Lighting on the same demand points.** The probe window follows every
   view, the trace scene and its pending gate cover what the window reaches,
   and the probe cache is byte-bounded.
8. **Authored areas on the gallery's floors**, then **terrain tiles**, then the
   moons through routed residency, as the decision orders them.
9. **Managed churn.** A warm boot's collections (hundreds pausing seconds in one
   window) are attributed with an allocation trace and their sources pooled or
   streamed, judged by collections and allocated bytes in the arrival window.

## What each step is judged by

Read in one window under the recipe above, on both backends:

- time until the occupied area has landed, cold and warm;
- resident bytes against the device budget, by owner;
- evictions, misses and coarsened groups, counted;
- frame time at the sweep's stops, and the gallery's exhibits reading as their
  plaques say.

The Steam Deck target (the debug world at or under 10 GB) becomes a qualification
on the same mechanism once a Deck reading exists; until then the lead machine's
budget is read against the device's own reported budget.
