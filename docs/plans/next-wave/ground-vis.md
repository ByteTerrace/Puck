# ground-vis: grounding for the visibility wave

Read-only grounding pass at the main landing. No builds, Worlds or GPU.
Every `file:line` below was read in this tree. Paths are under `src/Puck.SdfVm/` unless they name another root.
R2 has landed: its selection kernel and counted indirect draws are in the current tree, so the visibility plan's
"R2's code is on its lane's branch" (`docs/plans/visibility.md:33-34`) is stale.

Each item below is laid out the same way: what is there, its callers, the invariant and what enforces it, the
mechanisms to reuse, the counter that proves it, and a draft brief. A section at the end says which items can run as
parallel lanes.

---

## 1. Draw identity: one per-record generation (before V1/V2b)

The generation prerequisite is implemented by
[`SdfMeshHistory.Observe`](../../../src/Puck.SdfVm/SdfMeshHistory.cs).
It supplies one CPU seed decision and required packed GPU generation.
Retirement preserves per-cell generation lifetimes; exhausted tokens refuse
reuse rather than wrapping. Mesh or surface-frame changes seed history, while
ordinary matrix motion retains it.

GPU visibility and LOD validate the generation before consuming prior state.
Selection state also belongs to the actual tables owner:
[`SdfMeshOcclusion.Stage`](../../../src/Puck.SdfVm/SdfMeshOcclusion.cs)
clears it when a retained recorder switches tables, including during growth.
Same-owner growth retains the old prefix. This closes the collision between two
tables whose first occupants both use generation 1.

The [renderer contract](../../../src/Puck.SdfVm/README.md) owns current semantics;
[dense-landscape implementation](../dense-landscapes.md#first-implementation-slice)
owns workload/readback evidence. The old duplicate identity comparisons and
mesh-only token source are removed. CPU `sdf.instances` counts actual observations,
seeds, retirements, changed matrices and packed records/bytes. These are not
device-transfer bytes or a per-pass GPU history-reset count.

V1/V2b still owe stable retained instance slots and change-proportional updates.
They should move this existing generation/owner contract into the retained table,
not invent another history identity. The full renumber/portal/nested-view
qualification and GPU reset attribution remain in that work.

---

## 2. V2a and V2b exactly as the plan states them

### The 6-9 ms the settle window measured at the meadow

From `visibility.md:136-140`, M1 leg 1 (as authored) against leg 2 (poses held):

| | Direct3D 12 | Vulkan |
|---|---:|---:|
| Frame avg | 66.9 → 60.5 = **6.4 ms** | 69.1 → 59.7 = **9.4 ms** |
| Produce | 47.9 → 43.5 = 4.4 ms | 49.0 → 43.0 = 6.0 ms |
| Simulate | 18.7 → 16.7 = 2.0 ms | 19.9 → 16.4 = 3.5 ms |
| Root GPU (not the bound) | 21.7 → 17.4 = 4.3 ms | 21.1 → 17.9 = 3.2 ms |

The frame-thread trace at the meadow (`docs/plans/engine-job-system.md:166-171`, Direct3D 12, as authored) attributes:

- Historical profiler label "SdfMeshPathBake.Dress": **3.2 ms**, self 1.7 %, `SdfMeshClaims.Update` 0.7 %, `Enumerable.Contains` 0.5 %.
- `SdfWorldTables.StageChangedDraws` **3.4 ms**.
- `PackTransforms` other than effectors **2.9 ms**.

The traced shares sum to about 9.5 ms, against a measured produce delta of 4.4 ms. The trace inflates (leg 3 is 3 ms
slower than leg 1), and leg 2 still pays some of each. The simulate delta (2.0 / 3.5 ms) is not attributed by any
reading.

**Premise to verify:** which phase packs the placements, and what in simulate depends on the wind cells. Neither V2
step should claim the simulate delta without a split.

### V2a. The rate holds applied to placed animations

**What the plan says** (`visibility.md:288-293`, build order at `:555`): placement roots join the projection- and
consumer-based rate mechanism that bodies use, keeping planted roots, continuous world-phased gusts and independent
tip flutter.

**What it changes:**

- `WorldCreationInstances.Pack.cs:246-251`: `mayHold` requires `live.BodyIndex is not null`. It becomes "any
  registration whose root is rated": bodies, plus animated placement roots.
- `:295-300`: `BodiesPosed`, `RecordPose` and `WidenMotionReach` run only for bodies. They extend to placements.
- `WorldCreationInstances.Rates.cs:193`: `NoteRate` returns early for non-bodies. Placements get a reading named by
  placement id and write their slot rate codes, so `world.debug-view rates` and `world.rates` show them.
- `SdfUpdateRates.cs:276-288`: the counters `sdf.rates.bodies.posed` and `.held` either gain placement twins or are
  renamed to registrations. Pick one (rule 5: rename freely).

**What it reuses, unchanged:**

- `HoldsPose`, `CarryPose`, `ChoosePhase`, `ForgetPhase` and `RecordPose` (`Rates.cs:229-301`).
- `Shown`, which already uses the renderer's own cascade caster cull through `SdfMeshCull.Keeps` (`Rates.cs:121-145`).
- `BodyPixelsPerMetre` and `TierOf`, with `SdfUpdateRateTiers.BodyHertz`: 30 Hz under 30 px/m and 15 Hz under 15 px/m
  (`SdfUpdateRates.cs:43-51`, `:88`).
- The continuity cut on resuming from unseen (`Pack.cs:268-276`).
- `SdfMovedTransforms.Commit`, so a held placement's unchanged slots owe nothing (`Pack.cs:304-312`).

Planted roots hold by construction, because `CarryPose` rides the other slots rigidly with an unmoving root. Gusts stay
continuous because the next full pack evaluates at the current state cells. Tips rated with their tile hold with it.

**Premise to verify: the rate granularity is a registration, which is a whole tile.** The meadow is "sixteen
independently bounded animated tiles" (`worlds/debug/meadow.md:8`). A tile takes the tier of the sphere's *nearest*
point, which puts 30 Hz at roughly 26 m depth and 15 Hz at roughly 52 m at 900 px and 60°. With the camera inside
the patch at `debug.go meadow`, most tiles stay every-frame. Tiles behind the camera fall to the slowest tier rather
than holding, because their shadows are shown.

So V2a may save little *at the meadow stop*. It pays on the lawn's far tiles and wherever the camera stands off the
patch. Read `world.rates` at the stop, counting tiles per tier, before promising frame time. If tiles are too coarse,
the fix is authored: smaller tiles in `windMeadowTile`. It is not an engine special case.

**What V2a does not remove.** Dress still rebuilds the whole list whenever any slot moved (`SdfMeshPathBake.cs:375-397`),
and `StageChangedDraws` still compares every draw (`SdfWorldTables.MeshRegion.cs:278-279`). V2a cuts the posing of
held tiles, the changed records and their upload, and the motion-history copies. It does not cut the rebuild or the
compare.

**Counter that proves it** (all of these exist):

- `sdf.rates.*.posed` and `.held` (extended to placements) per frame.
- `sdf.transforms.packed-rows` and `owed-rows` (`SdfMovedTransforms.cs:41-59`), which fall with held tiles.
- `world.debug-view rates` tints far blades, and `world.rates` lists tiles per tier.
- Beside those, `host.frames` at `debug.go meadow` against M1 leg 1, and a presented burst of eight frames showing
  gusts continuous and roots planted (the plan's check, `visibility.md:555`).

**Draft brief (lane `v2a-rates`, rendering and World client, small):**

> Placement roots join the pose-rate holds.
>
> - Lift the body-only conditions at `WorldCreationInstances.Pack.cs:246-251` and `:295-300` and at
>   `WorldCreationInstances.Rates.cs:193`.
> - Name readings by placement.
> - Count placements in `SdfUpdateRateCounts` (rename to registrations, or add twins).
> - Reuse `HoldsPose`, `CarryPose`, `Shown` and `SdfUpdateRateTiers` unchanged. Add no second tiering.
>
> First read `world.rates` at `debug.go meadow` and on the lawn, and report how many of the tiles fall to a tier. If it
> is few, say so: tile size is an authoring lever in `worlds/standard/foliage.puck`'s `windMeadowTile`, not an engine
> one.
>
> Verify headless that packed and owed rows fall when the camera looks away. GPU leg, granted: `world.debug-view rates`
> capture, a presented burst at the meadow, and `host.frames` against M1 leg 1, which the lead takes in a settle window.
>
> Owns: `WorldCreationInstances.Pack.cs`, `WorldCreationInstances.Rates.cs`, `SdfUpdateRates.cs` (counts).

### V2b. Retained instance updates

**What the plan says** (`visibility.md:294-298`, build order at `:558`): `Dress` stops rebuilding the list. A moved slot
updates its instances' transforms in place, a new or retired instance updates the table and its generation, and
uploads are proportional to what changed. The packet's first cut is to index the frame's instances by slot once and
redress only moved groups (`open-items.md:813-820`).

**What it changes:**

- The historical dressing implementation (`SdfMeshPathBake.cs:358-457`) had four costs; retained topology and
  copy indexing are now in `SdfMeshDresser`, leaving dirty ranges and shared pose derivation ahead:
  - Every frame, its moved scan compares every group's slot pose (`:378-391`).
  - On any move it clears and rebuilds `m_draws` (`:398-433`).
  - It scans `frame.MeshAssetSlots` with LINQ `Contains` (`:404`).
  - For each dynamic asset group it scans every `frame.MeshInstances` entry (`:405-409`), which is O(groups ×
    copies).
- `SdfMeshClaims.Update` runs on every rebuild (`:432`).
- `SdfWorldTables.StageChangedDraws` compares all draws with `SameDraw` (`MeshRegion.cs:278-279`, `:313-342`) and
  rewrites the changed ones.

**What it reuses:**

- **`SdfMovedTransforms`, the existing moved set** (`SdfMovedTransforms.cs:9-33`). It already records, per frame with a
  64-frame history, which slot ranges moved and which were *reseated* (changed owner). It reaches Dress through
  `SdfFrame.MovedTransforms` (`SdfFrame.cs:190`). V2b reads it to know which slots moved, instead of scanning every
  group, and keeps a `slot → draw-index runs` map built at each structural rebuild.
- **The same shape for draw rows.** The residency today learns what changed by comparing (`SameDraw`). V2b should hand
  it the changed draw runs and the reseated runs instead. The draw list's change set is isomorphic to
  `SdfMovedTransforms`: owe a run, reseat a run, or everything. The one-implementation answer is to generalize that
  class into a moved set over the rows of any retained table, used by both the transform table and the draw records.
  Do not write a second history class.
- **Item 1's door.** `Observe` turns into "apply this frame's reseats": the generation bumps on a reseat, not on a
  compare.
- **`GpuUploadRuns` and `GpuRegion.Write` per run.** These already upload in runs (`MeshRegion.cs:302-307`).

**The invariant and its enforcement.** The dressed list's shape changes only inside a single "rebuild" method, which
reports everything as changed. Every in-place change (a matrix of a moved slot's draws, or a copy's pose) goes through
one method that records the index in the frame's draw moved set. The residency stages exactly the owed runs since the
serial it last consumed. A consumer older than the history is owed everything, as `SdfMovedTransforms` already
specifies.

Enforcement: `m_draws` becomes private to a retained draw-table type whose only mutators record the change. No caller
can write a draw without owing it.

**Counter that proves it** (all new):

- `sdf.mesh.draws.rebuilt`: draws a structural rebuild wrote. It is 0 on a motion-only frame.
- `sdf.mesh.records.staged`: records written. It equals the draws of moved slots.
- `sdf.mesh.records.compared`: it goes to 0, because the compare is deleted.

Beside those, a `dotnet-trace` at the meadow shows no list rebuild and no `SameDraw`, as the plan's check states
(`visibility.md:558`), and a `world.reload` of one blade geometry rebakes one mesh and every instance follows.

**Order.** It writes `SdfWorldTables.MeshRegion.cs` and `.Motion.cs`, which item 1 also writes, so it lands after
item 1. The plan's V2b-after-V1b dependency (`visibility.md:569-571`) is answered by M1's reorder: "V2b then builds the
instance table V1b consumes".

**Draft brief (lane `v2b-retained`, rendering, invariant-bearing, design note first):**

> `Dress` updates in place and the residency stages by change.
>
> - Generalize `SdfMovedTransforms` into one moved set over any retained table's rows: owe, reseat, everything, and a
>   64-frame history. Use it for the transform table and for a new retained draw table. Do not write a second class.
> - `SdfMeshDresser.Dress` reads `SdfFrame.MovedTransforms` to find moved slots through a `slot → draw runs` index,
>   updates those draws' matrices through the draw table's one mutator, and rebuilds only on structural change:
>   copies, host draws, slot clips, or finished groups.
> - Index `MeshInstances` by slot once per rebuild. Replace the LINQ `MeshAssetSlots.Contains` with a set.
> - The residency stages the owed runs. `StageChangedDraws`' `SameDraw` compare is deleted, and item 1's `Observe`
>   applies the reseats.
> - Add `sdf.mesh.draws.rebuilt`, `sdf.mesh.records.staged` and `sdf.mesh.records.compared`.
>
> State the invariant (no draw changes without owing its run) and its type-level door in the commit body.
>
> Verify with a `dotnet-trace` at the meadow (lead's settle window) and the counters on a motion-only frame. Run
> `world.reload` of one blade geometry. GPU leg, granted: a meadow presented burst plus `world.debug-view motion` on
> held blades.
>
> Owns: `SdfMeshPathBake.cs` (Dress), `SdfMovedTransforms.cs` (generalized), `SdfFrame.cs`,
> `SdfWorldTables.MeshRegion.cs`, `.Motion.cs`, `.FramePreparation.cs`. Lands after `draw-gen`.

**GPU premise for both V2 steps.** Holding poses dropped the root GPU time by 4.3 ms (Direct3D 12) and 3.2 ms
(Vulkan), but nothing names where. The candidates are:

- about 1 MB of re-staged records a frame (`visibility.md:62-64`);
- motion-history copies (`SdfWorldTables.Motion.cs:66-129`);
- the far cascade's refresh;
- the clipped temporal resolve on moving pixels.

The meadow's root GPU (19.4-21.6 ms) is over the 16.7 ms bar once the CPU is fixed (`engine-job-system.md:132-133`),
so this matters next. Take `world.gpu-timing` per pass in legs 1 and 2 before V2a and V2b claim any GPU saving.

---

## 3. The static-copy shared bake

### What is there

**The stamper.** Every static placement instance emits instances whose bound and transforms are in world space:

- `WorldPlacementStamper.StaticInstanceVisitor.Visit` (`src/Puck.World.Client/WorldPlacementStamper.cs:742-899`) calls `BeginInstance` with
  `boundCenter: instance.Origin` (`:828`, `:848`, `:892`, glass at `:696`).
- Each shape opens its own instance, run-joined by `bakesWithPrevious` (`:806-812`).
- The shapes go through `CreationStampTransform(Origin, Rotation, Scale)`, and `StampPrefix` (Authoring
  `CreationStampEmitter.cs:558-572`) emits `ResetPoint [TransformDynamic(slot)] Translate(origin) Rotate Scale` per
  chain.
- `ReliefChain` composes world positions directly (`CreationStampEmitter.cs:410-426`).

**The plan.** `SdfMeshBakeGroup` doc (`SdfMeshBakeGroup.cs:11-14`): a static group is one instance or a
`BakesWithPrevious` run, "in world space". `PlanGroups` makes the static group (`:299-310`) and records world-space
`Members` (`:313-320`).

**The key.** `SdfMeshGeometryBuilder.AppendInstance` hashes a static instance's world bound center
(`SdfMeshGeometry.cs:197`) and its world-space instructions. Copies that differ only by a rigid transform therefore
never hash alike. That is the open item's 1,368 → 1,671 unique cold bakes for the lawn's 307 rows
(`open-items.md:1065-1080`).

### How the dynamic path shares (the mechanism to reuse)

There are two sharing mechanisms on the dynamic side. They should not be confused.

**(a) Per-copy groups, shared by the geometry key.** This is how the 748 wind-grass parts share. Each copy rides its
own slot and gets its own group, and the library deduplicates bakes by key:

- The geometry builder hashes the group in its slot's frame:
  - the own slot's `TransformDynamic` is hashed as a role, `OwnSlot` (`SdfMeshGeometry.cs:268-276`), and kept at
    identity (`:219-222`);
  - a dynamic bound hashes only its radius (`:197`);
  - other slots are hashed by their pose relative to the frame, rounded (`:277-284`, `:357-375`);
  - materials are hashed by place (`:226-237`).
- `Seal` writes `PoseRows` with the own slot at identity, so the bake samples in the slot's frame whatever the live
  transforms are (`SdfMeshBakeGroup.cs:405-428`).
- `SdfMeshLibrary` keys meshes by `(bake identity, SdfMeshGeometry)`, with material variants
  (`SdfMeshLibrary.cs:43-53`, `TryTake` at `:219`). Equal keys bake once.
- `Dress` draws each group at its slot's matrix, `Scale × Rotation × Translation` (`SdfMeshPathBake.cs:412-420`), with
  the mesh in the slot's own units (`SdfMeshGeometryBuilder.UnitsOf`, `SdfMeshGeometry.cs:384`). A uniform scale is
  therefore admitted.

**(b) Templates and instances**, for rigid bodies: `WorldRigidAssets` (`src/Puck.World.Client/WorldRigidAssets.cs:130-210`).

- It emits one template per definition at an asset slot posed at the identity (`:224`). Each body is an
  `SdfMeshInstance` with a root matrix (`:269-271`).
- `Dress` expands each asset group into one draw per copy (`SdfMeshPathBake.cs:404-410`).
- The copies "own a root pose and identity, never field instructions" (`WorldRigidAssets.cs:11`).

**Recommendation: reuse (a).** Static copies ride a *fixed frame* through the existing `TransformDynamic`, keyed by the
same `OwnSlot` role, deduplicated by the same library and drawn at the frame's matrix:

- The presentation field keeps every copy's instructions. The CPU queries depend on that: the effector probes, the
  chase camera's clearance, and `WorldDiagnostics`' field-versus-collider comparison all read this field.
- No bake kernel change is needed: the bake samples through `PoseRows`, not live rows.
- Per-copy identity, picking (`m_chunkOwners`, `FirstInstance`) and chunking survive.

Option (b) would drop the per-copy field instructions, and the field queries would lose static scenery.

Reusing (a) literally (making static copies *dynamic*) is wrong, because `Dynamic` currently also means "moves":

- dynamic groups never chunk (`SdfMeshPathBake.cs:1898`);
- they have no `Members` refinement and no `BakesWithPrevious` runs (`SdfMeshBakeGroup.cs:292-320`);
- their draws are `IsDynamic`, which the irradiance static scene excludes (`SdfMeshIrradianceScene.cs:207`);
- they ride motion history and rates.

The design therefore splits **"framed"** (the mesh is stored in a slot's frame, role-hashed, drawn at that slot's
matrix) from **"moves"** (motion, rates, irradiance exclusion). Moving implies framed. A static copy is framed and does
not move:

- It keeps static grouping: runs, members expressed in the frame, glass, chunks.
- It keeps static draws (`IsDynamic = false`), so the probe trace still sees it.
- The irradiance scene already supports instanced static meshes, with per-instance `ObjectToWorld` over a shared
  hierarchy (`SdfMeshIrradianceScene.cs:228`, `:511-535`).

### How the bake identity derives

The identity has two parts:

1. **The geometry hash** (`SdfMeshGeometry(Dynamic, Hash)`, `SdfMeshGeometry.cs:12`): instruction words with side-table
   values, materials by place, the bound (radius only when framed), detail, the static box, poses of other slots
   relative to the frame, lanes, the reach, and snap steps (`SdfMeshGeometry.cs:14-37`).
2. **`SdfMeshBakeIdentity`**: the bake kernels' bytecode plus the IL closure of every method the bake reaches
   (`SdfMeshBakeClosure`; rendering skill, "Baked meshes persist across runs").

Notes:

- Today the `Dynamic` bool in the key also selects whether the landed mesh is chunked. A framed static group must key
  apart from a framed moving one, or the chunking condition must go into the hash.
- Under the fixed-frame design, a static copy's bound centre, `Box` and `Members` must be expressed in the frame, not
  in world space. Otherwise they put the placement back into the hash.
- Detail must be in frame units (world detail ÷ scale), or copies at different scales never share.
- Any edit to `SdfMeshBakeGroup`, `SdfMeshGeometry` or `SdfMeshPathBake` moves the bake identity, so the first boot is
  a full cold bake. Check `puck mesh identity --build --against` first.

### What changes for colliders

Nothing in the collider path:

- Static solid colliders are compiled server-side, per placement, in fixed point, from the definition at each row's
  resolved frame (`src/Puck.World.Server/WorldColliderSet.cs:436-505`, `PlacementColliders` at `:531-533`). The same
  goes for the solid field (`server.SolidField`, `WorldCollisionCommandModule.cs:183-195`). Neither reads the render
  program or its bake, so each copy keeps its colliders at its own frame. Make that an explicit non-goal in the brief.
- What does change is the *presentation* field. A copy's instructions become `TransformDynamic(frameSlot)` plus local
  shapes, so every CPU evaluator needs the frame rows.
- Today `EmitStatic` takes no slot base. It has 10 callers, including the CLI's `MotionClearance.cs:99,203` and
  `CreationCommand.StepClamps.cs:22`, the adjacency emitter and the session emitters. Each one either builds an
  evaluator or feeds a composition.
- The door is the program itself: the builder records each fixed frame (slot → rigid pose), and a single method writes
  them into any transform table. A working name is a new `FixedFrames` member on `SdfProgram`, written by `WriteFixedFrames`. The composition
  source writes them on `Everything` frames, and every CPU evaluator takes its table through the same method.
- The frame rows never move, so `SdfMovedTransforms` owes them nothing after the first frame.

**Open design question for the lane: numbering the fixed-frame slots.** Emitters get a slot base before they emit,
but the stamper's instance count (lattices, mirrors, deals) is known only while it visits. There are two options:

- the builder allocates fixed-frame slots past every emitter's slots and patches the `TransformDynamic` operands at
  `Build`; or
- a pre-pass counts the instances.

Decide this in the design note.

### Design it together with the spelling item

The [medium] item at `open-items.md:1081-1095` covers grouping that depends on spelling:

- detail follows group size (`devices.puck:16-22`, `desktop.puck:53`, `hall.puck:497-500`);
- a warped shape bakes alone and splits the next one (now `WorldPlacementStamper.cs:792-806`);
- every text run is its own group;
- one non-union blend scopes the whole creation.

The static-copy design fixes where a copy's group lives and what it hashes. The spelling item fixes which shapes form
a group and at what detail. Both change `PlanGroups` and the stamper's instance emission, so they are one design note,
whose invariant is that grouping and detail derive from meaning. They can be two commits.

### Counters that prove it

These exist:

- `[sdf.mesh-bake] start residency=world` unique cold bakes and groups. The open item's 1,671 bakes and 7,236 groups
  should fall back toward 1,368 or below.
- The library's store hits.

`puck mesh audit --world worlds/debug` must stay clean on the shared groups.

Add a count of framed static groups that took a shared mesh, or read the existing library hit count per residency.

### Draft brief (lane `static-frame`, geometry and bake, principal-grade, design note before code)

> A static copy bakes in its prototype's frame and shares its mesh across rigid copies (uniform scale admitted). It
> does this through the dynamic path's mechanism:
>
> - The copy rides a fixed frame by `TransformDynamic`.
> - Its geometry is role-hashed (`SdfMeshGeometry.cs:268-276`), and its bound, box and members are expressed in the
>   frame.
> - The library deduplicates it.
> - `Dress` draws it at the frame's matrix.
>
> Split "framed" from "moves" in `SdfMeshBakeGroup`, `SdfMeshGeometry` and the draws. A framed static group keeps
> runs, members, chunks, glass and `IsDynamic = false`.
>
> The program carries the fixed frames, and one method writes them into any transform table: the composition's pack,
> and every CPU evaluator, including `MotionClearance` and `StepClamps`. Decide the slot numbering in the design note.
>
> Fold in the spelling item (`open-items.md:1081-1095`): group by meaning, not by row order or splitting. Colliders
> are out of scope: they compile from the definition per placement (`WorldColliderSet.cs:436-505`).
>
> Verify:
>
> - `puck mesh identity` before boot (expect a full cold bake);
> - unique bakes and groups from `[sdf.mesh-bake] start` against the open item's 1,671 and 7,236;
> - `puck mesh audit --world worlds/debug`;
> - a headless boot with `world.debug-view report colliders --json` still comparing (the presentation field is
>   unchanged);
> - GPU leg, granted: lawn and meadow stills compared tolerantly, the irradiance done log, and a windowed Vulkan boot.
>
> Cold-bake thread-seconds against the 384 s ruling go in the lead's settle window.
>
> Owns: `WorldPlacementStamper.cs`, `CreationStampEmitter.cs`, `SdfProgramBuilder.Instances.cs`,
> `SdfInstanceRange.cs`, `SdfProgram*.cs` (fixed frames), `SdfMeshBakeGroup.cs`, `SdfMeshGeometry.cs`,
> `SdfMeshPathBake.cs` (Dress's static-framed draws and the chunk condition at `:1898`), the EmitStatic callers, and
> `SdfCompositionFrameSource.cs` (writing the frames).

---

## 4. The meadow's GPU side: 11 sessions and six nested views

### What the measurement says

From `docs/plans/engine-job-system.md:107-142`:

- At the meadow on Direct3D 12 the root `world` node is 21.3-21.6 ms.
- A frame on which the **eleven screen and portal sessions** (6.1 ms) and **six nested views** (2.7 ms) also submit
  is busy **30.3 ms**.
- Each session is 0.4-0.6 ms on Direct3D 12 and about 0.23 ms on Vulkan. They are a rated burst: when they land on one
  frame they add 3-9 ms to it.

### Which V5 step reaches which part

V5's first step is "shared classification across a residency's views (one read of instance data, per-view compact
lists and per-view history, with no global barrier that serializes independent residencies)" (`visibility.md:407-410`).
R2's shape, which V5 replaces, is that each pass classifies the whole list behind its own global barrier and allocates
three argument phases (`SdfMeshOcclusion.cs:110-116`, `:270-276`). Each view graph instance owns its recorders and
therefore its own `SdfMeshOcclusion` (`SdfWorldPassRecorder.cs:312`).

**The six nested views (2.7 ms): addressed by the shared classification.** They are views of the root residency, so
each one re-reads and re-classifies the whole draw list in every pass it runs. At R2's measured 22 ns per candidate
(`visibility.md:42-45`), a list in the thousands of candidates across two occlusion phases is a large share of the
roughly 0.45 ms each nested view costs.

**Premise to verify:** the per-pass GPU timing of one nested-view node at the meadow (its mesh selection and occlusion
dispatches against its pixel passes), and how many candidates it classifies. If selection is most of the 0.45 ms, V5's
shared classification (and V1a's compaction before it) take it. If the pixel passes dominate, neither does.

**The eleven sessions (6.1 ms): not addressed by the shared classification.** Each session is a **separate residency**:

- `CreateSessionResidency` builds an `SdfWorldResidency` of its own for the feed's world
  (`src/Puck.World/WorldScreenBinder.Device.Views.cs:9-40`, which logs "renders a residency of its own").
- Routed scenes do the same (`WorldScreenBinder.Device.Routed.cs:130-150`).

"Across a residency's views" therefore never shares work between sessions. Of V5, only the "no global barrier that
serializes independent residencies" clause touches them, and that changes how passes overlap on the queue, not how
much GPU time they cost.

The sessions also render at `ReducedQuality`, without AO or soft shadows (`WorldSessionSceneEmitter.cs:89-94`). So
whether they draw cascade passes at all is part of the premise.

What does reach the sessions:

- **V1a's ordered compaction.** It removes the per-candidate empty-command walk in each session's own passes (in each
  session, cost scales with that session's own candidates).
- **The job system's rated-burst spreading** (`engine-job-system.md:139-142`; Next item 7, "spread the rated views").
  It moves the 3-9 ms burst across frames without reducing it.

**Premise to verify:**

- What a session's 0.4-0.6 ms consists of: per-pass timing inside one session node, its candidate count, and whether
  cascades run under `ReducedQuality`.
- Whether any of the 11 are camera screens of the root world that render through the root residency rather than a
  session residency. Those would be V5's.

### Counter that proves it

- `world.gpu-timing timeline`, plus per-pass timing of one session node and one nested-view node.
- `world.counters gpu node <name>` and `pass sdf.world$mesh` in those nodes: dispatches, candidates (V1's budget
  counters, `visibility.md:470-475`) and draws.

These are measurements in the lead's settle window. No lane writes code for item 4 until they are read.

### Draft brief (settle-window measurement, lead)

> At `debug.go meadow`, Direct3D 12, with sessions submitting, read three things:
>
> 1. per-pass GPU time and `world.counters gpu node` for one nested-view node and one session node, with candidates,
>    selection dispatches and pixel passes;
> 2. whether session nodes draw cascades under `ReducedQuality`;
> 3. which of the 11 are separate residencies (the `[world.view: … renders a residency of its own …]` log lines).
>
> The result decides how the 8.8 ms divides: V5's shared classification (nested views), V1a's compaction (both), and
> the job system's burst spreading (sessions).

---

## Parallel lanes and file conflicts

| Lane | Writes | Conflicts with | Can start |
|---|---|---|---|
| `draw-gen` (§1) | `SdfMesh.cs` (`SdfMeshRegion`), `SdfWorldTables.MeshRegion.cs`, `.Motion.cs`, occlusion and selection HLSL, `SdfMeshRecord.cs` doc, 2 tests | `v2b-retained` (same tables files) | now |
| `v2a-rates` (§2) | `WorldCreationInstances.Pack.cs`, `.Rates.cs`, `SdfUpdateRates.cs` | none | now, beside `draw-gen` and `static-frame` |
| `static-frame` (§3), design note first | stamper, `CreationStampEmitter`, `SdfProgramBuilder.Instances`, `SdfInstanceRange`, `SdfProgram*`, `SdfMeshBakeGroup`, `SdfMeshGeometry`, `SdfMeshPathBake` (static bake) and `SdfMeshDresser` (copies and chunks), `SdfCompositionFrameSource` | `v2b-retained` on `SdfMeshDresser.Dress` and `SdfCompositionFrameSource`; Next item 0 (CPU field queries) on the evaluator's transform input | design now; code beside `draw-gen` and `v2a-rates` |
| `v2b-retained` (§2) | `SdfMeshDresser.cs`, `SdfMovedTransforms.cs`, `SdfFrame.cs`, `SdfWorldTables.*` | `draw-gen`, `static-frame` | after `draw-gen` lands, and after `static-frame`'s dressing change lands or is coordinated |
| §4 measurement | none | none | the lead's next settle window |

`draw-gen`, `v2a-rates` and `static-frame` share no files and can run as three parallel lanes. `v2b-retained` goes
last of the four: it consumes `draw-gen`'s door and touches `Dress`, as `static-frame` does.

Next item 0 (the CPU field queries, `open-items.md:38-48`) outranks the visibility steps in the Next list. It
determines the meadow frame more than any of these. Run it beside them, not behind them.
