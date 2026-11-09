# Virtual geometry: visibility and detail at scale

The synthesis memo of the visibility-and-detail research round. The owner
asked how good Puck's levels of detail, frustum and occlusion culling are and
what would be better, meshlets "or something even better for Puck". Three
researchers answered from different seats: Codex (Sol) opened the GPU-driven
cluster, cluster-LOD, streaming, shadow and hardware literature and the
checked-out kernels; Argon read the far field, impostors, vegetation and
animated creations from search summaries and the code; Claude (Opus) measured
a creature's bake and argued the Puck-native case. This memo weighs the three
records against the code, resolves where they disagree, decides what Puck's
visibility and detail pipeline becomes, and orders the build as steps each
checked on the debug world. It is written in
[the MMO foundations memo](mmo-foundations/synthesis.md)'s manner: options,
evidence, the attacks each closes, and the ruling each rests on, weighed at the
scale where every zone, character and server is a world
([Puck's purpose](../../AGENTS.md#pucks-purpose)).

**Where this lives.** This is a page of its own rather than a section of
[the visibility plan](visibility.md), because the two own different things:
visibility owns submission (retained instances, compaction, buckets, authored
motion on the GPU, the early-depth variant, multi-view sharing), and this page
owns detail and reach (what a draw's levels are, how a skinned draw is bounded,
what replaces a draw far away, what a cluster is inside one, what the bake
derives on demand, and what the bar devices can run). The two meet at the
selection kernel and at V3, and this page amends V3 where the round's evidence
re-ranks it. [Dense landscapes](dense-landscapes.md) owns the composed forest
workload and consumes the far-field decision here.

Labels: **[record]** is a claim taken from a researcher's findings file, named
by researcher; **[opened]** means this memo reopened the code or document at
the integration tip it was written against and the words are pasted from the
tool output; **[search summary]** is an external fact Argon knew only from a
search summary, kept only where the mechanism is uncontested; **[inference]**
is this memo's reasoning; **[hypothesis]** is a prediction nothing has measured.
A number carries the command that produced it. Durable citations are by
symbol; a `file:line` is a lead at this revision.

## Summary

Puck already selects every draw on the GPU: the box cull, the receiver test,
the one-pixel level choice and the two-phase depth occlusion run in one kernel
for every raster pass, with one counted indirect command per candidate. What
is missing is not GPU selection but three things the per-draw design cannot
reach: a draw's coarsest level is a floor it never falls below (a creature
draws a quarter of a million triangles at any distance), a skinned or
multi-slot draw has no box and no levels, and nothing stands in for a draw or a
forest far away. The three researchers agree on the baseline every bar device
runs (compute selection and ordinary indirect raster) and disagree on what to
build first. The evidence settles it: **levels of detail are derived from the
field, not collapsed from the mesh**, as an unbounded chain the one contouring
makes at coarser cells with error measured against the field; **a skinned draw
is bounded exactly by its members' rest boxes under their live transforms**,
read from the slot table the kernel already has; **a hierarchy above the draw
is contoured from the field too** (one proxy per distant creation, one parent
per chunked union, one parent per stand of static copies), each a closed
replacement the kernel chooses by the same pixel rule, so the forest's far
field is a region of the field and not an impostor atlas or an authored HLOD;
**the finest tier becomes a cache baked on demand** at what the views resolve,
which is virtual geometry from the field; and **clusters inside a draw,
compute micropolygon raster, mesh shaders and virtual shadow maps are
watched**, each with the measurement that admits it, because the floor, the
skinned gap and the far field dominate at MMO scale and none of those four
touches them. Borrowed techniques win where they are better (the two-phase
depth pyramid, order-preserving compaction and buckets from the visibility
plan, meshoptimizer and Nanite's group-replacement contract as the reference
for any cluster hierarchy, the texel rule for shadow levels); the Puck-native
combinations win on detail because the field answers distance exactly where a
triangle simplifier guesses, and because a derived product identified from its
inputs can be baked on demand and cached where a triangle asset cannot.

The first step is offline and load-independent: a field-levels mode in the CPU
bake that `puck mesh audit --cpu` drives, measured against the collapse on the
Moth and one hall union ([G1](#the-build-order)). One decision stays the
owner's and is stated, not decided: whether an on-demand finest tier counts
toward the one-minute cold-bake bar ([owner call](#the-owner-call)).

## Where the pipeline stands

The round's shared context carried two stale premises; the researchers
corrected both from the code, and this memo reopened each fact below.

- **Selection is on the GPU, per draw.** The decision text now says so
  **[opened]** (`docs/decisions/rendering.md`, "A raster pass draws only what
  it can show"): "a world box per draw tested on the GPU by the selection
  kernel (`SdfMeshOcclusion`, `sdf-mesh-selection.hlsli`, with `SdfMeshBox` and
  `SdfMeshCull` as the host-side twin that glass visibility and update rates
  still use)". The kernel's early step keeps a draw by `sdfSelectionKeeps`,
  rejects a caster whose receivers are hidden by `sdfReceiverMayShadow`,
  chooses its level by `sdfSelectionLevel`, and the late step tests the kept
  boxes against the depth pyramid by `sdfOcclusionMaySee`
  (`passes/sdf-mesh-occlusion.comp.hlsl`, `CSMain`).
- **Every phase writes one command per candidate, empty or not** **[opened]**
  (`sdf-mesh-occlusion.comp.hlsl`): "Each phase draws one command a candidate,
  empty or not." and `sdfOcclusionEmit` writes `(emit ? vertices : 0u)` into a
  slot at `((phase * capacity) + candidate)`. Codex's reading stands: this is
  GPU selection with a sparse command list, not a compact list of survivors,
  and multiplying it by a cluster count would multiply the empty-command walk
  (Codex **[record]**). The visibility plan's V1 owns the compaction and
  buckets that fix it.
- **The level choice is a monotonic walk over a short table.** The kernel
  walks `while (chosen < count && error * scale <= 1.0) { chosen++; }` and
  back `while (chosen > 0u && error * scale > direction.w) { chosen--; }`
  **[opened]** (`sdfSelectionLevel`), so a draw past the distance where its
  last level fits stays at that level. `SdfMeshCollapse.CellShares` is
  `[0.25f, 2f]` and `NormalDegrees` is `[2f, 6f]` **[opened]**: two coarser
  levels whose error budgets are a quarter and two of the group's contour
  cell, and `KeptShare = 0.75f` drops a level that saves less than a quarter.
  The collapse never moves or makes a vertex; a corner where three faces meet,
  an open edge and a place whose triangles do not close a ring never move
  (`SdfMeshCollapse` summary **[opened]**).
- **A skinned or multi-slot draw has no box and no levels** **[opened]**:
  `SdfMeshBox.TryOf` returns `false` when `!mesh.Members.IsEmpty` ("those
  vertices ride dynamic-transform slots the GPU reads, so no host box bounds
  them and every pass draws them"), and `SdfMeshCollapse.Levels` returns the
  mesh unlevelled when `!mesh.Members.IsEmpty`. In the kernel `unboxed`
  short-circuits every test: `kept = ... (unboxed || sdfSelectionKeeps(...))`,
  `bool was = unboxed || state.x != 0u;`, `visible = kept && (unboxed ||
  sdfOcclusionMaySee(...))`, and `sdfSelectionLevel` returns 0 for it. Every
  such draw is drawn at level 0 in the early phase of every view and every
  cascade. Each vertex rides exactly one member or the root
  (`SdfMeshPathBake.Skinned` writes `vertexMembers[vertex]` as one member index
  or `SdfMesh.NoMember` **[opened]**), and the vertex resolver carries it
  through that slot's live transform **[opened]** (`frame/sdf-mesh.hlsli`,
  `sdfMeshWorldPosition`):

  ```hlsl
          return sdfMeshCarryPoint(record, (rotatePointByQuaternion((sdfMeshMemberLocal(entry, object, true) * sdfMeshDrawScale(record)), sdfDynamicTransforms[((3u * slot) + 1u)]) + sdfDynamicTransforms[(3u * slot)].xyz));
  ```

- **The host's remaining per-draw cost is the record, not the test.** A draw's
  record is `DrawWords = 64` words holding the matrix, the normal matrix, the
  carry, the clip plane, the level table address and the selection rows
  (`SdfMeshRecord` **[opened]**). `SdfWorldTables.StageChangedDraws` compares
  a list against "A copy of each draw last staged" and restages what changed
  **[opened]**. The frame-bar packet records that staging as "already
  proportional to the draws that changed (`sdf.instances`' `records.changed`
  is almost all of `records.compared`)" **[opened]**
  ([open items](open-items.md#engine-job-system), the CPU item); the lane that
  recorded it read about two thousand records a frame at flight B, a reading
  kept in its commit. The shared context's "about 3,100 draws restaged per
  frame at spawn" is not a reading at this tree (Codex **[record]**).
  A rigid dynamic draw's matrix is rebuilt from its slot's pose by
  `SdfMeshDresser` **[opened]**: `(Matrix4x4.CreateScale(scale) *
  Matrix4x4.CreateFromQuaternion(orientation)) * Matrix4x4.CreateTranslation(position)`,
  so a moved slot restages every group riding it.
- **Posing ignores occlusion.** `WorldCreationInstances.Shown` keeps a body
  posing while any rate view's `camera.MaySee` or any cascade's `cull.Keeps`
  keeps its sphere **[opened]**, and the occlusion result the kernel writes
  per draw (`sdfMeshOcclusionVisibleRW[draw]`, `state.x`) is read back only
  for session views (`SdfPortalSight.Occluded`). The frame-bar packet records
  the consequence ([open items](open-items.md#engine-job-system), the CPU item):
  at flight B the motion gallery's exhibits pose at 60 Hz behind the stairwell
  wall.
- **Chunks are the only spatial partition.** `SdfMeshChunks.Side = 8f`,
  `WholeTriangles = 131_072`, `MostChunks = 64` **[opened]**; every piece's open
  edges "stay fixed through collapse, including every collinear seam vertex,
  so independently chosen levels meet edge for edge". A chunk is a draw with
  its own record, box, levels and history.
- **The creation triangle floor.** Claude's CPU bake of the Moth
  (`Puck.Cli.exe mesh audit --world worlds/debug/avatars/moth.puck --prototype
  moth --cpu [--level 1|2]`): the bake's done line reports
  `triangles=1145385 ... level1=393043 level2=238235` over 180 groups, and 38
  groups have no coarser level at all, holding 62,861 triangles **[record]**.
  The nose (24,045 triangles, 0.5 mm cell) has no level 1 because its contour's
  "1365 folds, 1701 flipped, 356 slivers, 2061 open edges" are locked places.
  Beyond the distance where two contour cells project to a pixel the Moth draws
  at least that level-2 total, 238,235 triangles, in 174 draws whatever the
  distance. This memo reproduced the level-2 run
  ([the reproduction](#the-reproduction)). The far cascade's [high] item
  records the same floor on static content: a chain of two levels "stops at two
  contour cells of error, so a finely contoured mesh ... never coarsens toward
  a far texel", and four collapse levels to 128 cells took the oak only from
  3,052 to 1,440 triangles ([open items](open-items.md#engine-job-system),
  "Other frame-time findings").
- **One contouring, derived caches.** "`SdfSampledLattice` contours a
  GPU-sampled lattice through `SdfDualContouring` over `ISdfContourLattice`;
  never write a second mesher" **[opened]** (the rendering skill's mesh-bake
  reference). A baked mesh is a cache keyed by its geometry
  (`SdfMeshGeometryBuilder`, `SdfMeshGeometry.Hash`) and the bake's code
  identity (`SdfMeshStore.IdentityOf`), derived again on a miss
  ([the derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
- **No mesh-shader or 64-bit-atomic path exists on either backend.** Codex
  read `VulkanShaderCapabilities` and `VulkanLogicalDeviceFactory` and found
  subgroup and draw-parameter capabilities but no `Int64Atomics` or
  `MeshShadingEXT` **[record]**; this memo's search of
  `VulkanShaderCapabilities.cs` for `Int64|Mesh|Subgroup|DrawParameters` found
  only the `DrawParameters` capability lines **[opened]**. An optional mesh or
  compute-raster output stage is backend enablement work before it is shader
  work.

## How the records were weighed

Codex opened primary sources (the Nanite 2021 talk read page by page in memory,
meshoptimizer's `clusterlod.h`, NVIDIA's `vk_lod_clusters` documents, Bevy's
author accounts and kernels, the Direct3D and Vulkan specifications, a Mesa
RADV snapshot and vkd3d-proton's device code) and the checked-out kernels, and
labelled every estimate algebraic rather than measured. Claude ran one
measurement (the Moth audit above) and opened the code it cites. Argon had no
page reader: every external claim is a search summary, which it marked, and
its Puck code quotes were pasted from `read_file`; this memo re-opened each
Puck claim it rests on and kept Argon's external claims only where the
mechanism is uncontested across the three records. Where they disagreed:

1. **What to build first: clusters inside the existing compaction (Codex), the
   field-derived level chain (Claude), or member bounds plus a member-aware
   collapse (Argon).** The code decides it. Clusters remove the hidden parts
   of a visible draw and a member-aware collapse gives skinned draws the same
   two levels, but neither moves the floor: the collapse chain stops at two
   contour cells by construction (`CellShares` **[opened]**) and the Moth's
   level 2 is 238,235 triangles **[opened]**, so a hundred distinct avatars at
   any distance draw over 23 million triangles in over 17,000 draws, which no
   compaction, bucket or cluster test reduces (Claude **[record]**;
   arithmetic). Codex's own decision rule agrees: "if visible triangles remain
   high because levels stop, deepen the group hierarchy" **[record]**. M1
   found the root queue's GPU time well under produce on the 4070
   ([visibility](visibility.md#what-m1-and-m2-showed)), so the 4070 is not
   geometry-bound today; the far cascade's [high] item shows where geometry
   does bind (every caster at the coarsest level whose error fits its texel,
   in two layers). The chain goes first; clusters wait for a measured
   geometry-bound draw ([D4](#d4-clusters-inside-a-draw)).
2. **Whether skinned levels come from a member-aware collapse (Argon) or the
   field (Claude).** Both agree on the bounds ([D2](#d2-bounds-for-skinned-and-multi-slot-draws)).
   On levels, a member-aware collapse inherits the chain's floor and the fine
   mesh's defects; a field level is contoured from the field and skinned by
   the same points round level 0 is ([D1](#d1-the-unit-of-detail)). Argon's
   variant stays as the fallback if G1 rejects field levels.
3. **Whether a coarse envelope costs "zero extra GPU dispatches" (Argon).**
   Argon proposed downsampling the final lattice already in CPU memory. The
   bake's coarse lattices are sampled without gradients
   **[opened]** (`SdfMeshPathBake`: `Lattice(gradients: false, group: group,
   longest: CoarseSamples, minimum: CoarseSamples)`) and the final lattice is
   narrowed to the rows near the surface (the mesh-bake reference, "The coarse
   narrowing keeps the rows a thin plate lies between" **[record]** via
   Claude), so a coarse contour needs crossings and gradients at its own
   surface cells. The claim is unverified and G1 counts the taps.
4. **Whether impostors collide with a "depth-less opaque pipeline" decision
   (Argon).** No such decision exists: `docs/decisions/rendering.md` has no
   `SV_Depth` or early-depth text **[opened]** (a search for
   `SV_Depth|early depth|earlydepth` found nothing), and the visibility plan
   records that the opaque fragment *loses* forced early depth to the stamp
   fade's `clip` and the counter's atomics. Impostors are rejected on other
   grounds ([rejected](#rejected-and-watched)): a second representation for
   shading, picking and reprojection, and baked colour where Puck shades from a
   material identity.
5. **Where the far field comes from.** Codex: a per-prototype cluster DAG has a
   floor of root work per visible instance and "cannot merge a million
   separate trees into one region representation", so a spatial HLOD or
   coverage tier is needed **[record]**. Argon: an SDF envelope level per tree
   and a merged region proxy per cell **[record]**. Claude: a union parent
   contoured from the field over a stand of copies **[record]**. These are one
   mechanism under three names, and this memo adopts it
   ([D3](#d3-a-hierarchy-above-the-draw)). What none has settled is coverage:
   an opaque dilated crown overstates a sparse canopy's partial coverage, which
   is a capture question against the reference, so the stand parent is admitted
   only at sub-pixel error and the hybrid mesh-volume tier Codex and Epic
   describe stays watched.
6. **Hardware reach.** All three agree with the visibility plan: the baseline
   is compute selection and ordinary indirect raster; mesh shaders, 64-bit
   atomics and work graphs are enhancements with automatic fallback, and
   RDNA2's `VK_EXT_mesh_shader` under RADV does not qualify the Deck's Proton
   leg (Codex **[record]**). Decided as [D10](#d10-hardware-reach).
7. **Shadows.** Codex: refine caster selection to cluster or node bounds under
   the existing cascades first, virtualize shadow storage only after that is
   measured **[record]**; the visibility plan's V5 already defers cascade
   caching. Agreed ([D8](#d8-shadows)); the chain gives the far cascade its
   coarse levels first.
8. **Occlusion-aware posing.** Argon and Claude: read the kernel's visible set
   back into the rate decision. Codex's caution: a camera-hidden body may still
   cast a shadow or supply a pose to another consumer **[record]**. Decided as
   one more reason a body is "not shown", under the contract frustum entry
   already has ([D6](#d6-the-hosts-work-per-moved-body)).
9. **The restaging.** Claude: a rigid dynamic draw should read its slot as a
   member does, so a moved body restages slots, not records; Codex: the
   premise of 3,100 is stale and V1's compaction owns the GPU side. Both hold;
   the slot-resolved draw is V2b's design ([D6](#d6-the-hosts-work-per-moved-body)).
10. **The cold-bake bar.** Claude: demand tiers pay only what the spawn views
    resolve, and whether the bar counts on-demand finest tiers is the owner's.
    Stated as [the owner call](#the-owner-call).

## The decisions

### D1. The unit of detail

**What must be decided.** What a baked mesh's coarser levels are: how many,
how each is made, what bounds its error, and what a draw does past its last
level. At MMO scale this is the first-order cost: avatars are mostly unique
geometry, so instancing gives a crowd little, and a floor per draw multiplies
by the population.

**What exists.** Two collapse levels over the merged mesh's own vertices, with
measured error and the locked places above ([where the pipeline stands](#where-the-pipeline-stands)).
The one-pixel rule and its 1.1 return band (`SdfMeshDetail.CoarserPixels`,
`FinerPixels` **[opened]**) and the shadow texel rule are the owner's rulings
([decisions](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)).
The level table holds, per level, its first triangle, vertex count and error
(`SdfMeshRecord.LevelTableWord` **[opened]**), and a counted draw's identity
carries the level above a 24-bit draw index (`SdfKernelInterfaces.MeshLevelShift = 24`
**[opened]**), so the representation addresses up to 256 levels per mesh.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 1a. Field-derived levels: level k is the group's field contoured again by the one contouring on a lattice of cell `c0 * 2^k`, continued until a level holds a handful of triangles or the group vanishes; error measured against the field both ways (mesh to surface by value over gradient at each level vertex, centroid and edge midpoint, as `SdfSampledLattice.PassesBetween` already measures; surface to mesh by each level-0 vertex's distance to the level's triangles through a triangle hierarchy of the kind `SdfMeshIrradianceScene` builds), raised to the finer level's so the kernel's walk stays monotonic | A coarse lattice drops sub-cell parts, closes sub-cell holes and merges near parts by itself, which a collapse refuses at locked places; a level is not inherited from level 0, so the nose's defects do not block it; the field answers distance where a simplifier tracks displacement (Bevy's author calls perceptual simplification "very much an unsolved problem", Claude **[record]**, opened) | Crossings, contour and answer per surface cell of each coarse lattice, about a third of one level's surface work for the whole chain (geometric series; Claude **[record]**, a lead); a re-contoured level carries its own vertices, a geometric tail on the coarse side; a skinned group's coarse vertices need the points round that assigns members | A coarse level point-samples materials at its own cell, so a material region under a cell vanishes (acceptable only where the level's error is sub-pixel); a thin part vanishes at a coarse cell with a vanishing error as large as the part, which the dilated variant answers (1d) |
| 1b. A deeper collapse chain (Argon's `CellShares = 8f, NormalDegrees = 45f`, unlocking corners) | Reuses the landed collapse | Cheap to try | The far cascade item tried four levels to 128 cells and the card back made no level past the second; the oak reached 1,440 **[opened]** (open items); topology never changes, so a tree's branches never merge into a crown |
| 1c. A cluster LOD DAG over the merged mesh (Nanite's group-lock-simplify-recluster, meshoptimizer's `clodBuild`) | Proven at scale for dense static meshes; a complete replacement cut with group-coherent hysteresis (Codex **[record]**, opened) | A builder, a BVH, a cut selector and history keyed by group; a deep hierarchy accumulates boundary density under chunk seam locks; QEM is a priority, not an error (Codex **[record]**) | Aggregates ("riddled with holes ... leaves and grass", Nanite 2021, cited by [visibility](visibility.md#rejected-and-watched)); its error is the simplifier's displacement, not the field's distance; it inherits level 0's defects as 1b does |
| 1d. 1a with a dilated variant: where a level would lose a part whose vanishing error exceeds the dilation `d`, contour `f - d` instead at error `d`, the smaller error winning | An offset surface is one subtraction for a field and a hard problem for a mesh; the field-native analogue of Nanite's aggregate voxels, which switch representation "if doing so would result in lower error than using triangles" (Epic, experimental; Claude **[record]**, opened) | One more contour per level where it applies | A dilated opaque crown overstates coverage where gaps are sub-pixel: whether it reads as the reference is a capture question **[hypothesis]** |

**Favoured.** 1a with 1d, for closed groups first (every creation group and
every unchunked static group); chunked unions keep their collapse levels until
D3's union parent stands above them. **Strongest case against:** the chain's
bake cost on the one-minute bar and its storage are estimates, and the field
is not a true distance, so the measured error is value over gradient, not a
certified Hausdorff distance. Both are what G1 measures before anything lands
in a running World.

**Decided.** A level is contoured from the field, and the chain continues until
nothing is left to draw. The invariant: *every level's recorded error bounds its
distance from the field both ways and is at least the finer level's*, enforced
in one place, the bake's `Land` stage, which measures each level against the
field before it records it and refuses a level that fails; the selection
kernel's monotonic walk consumes that guarantee and nothing else. The guarantee
stops at what finite samples can see: a thin feature between samples is the
audit's uncovered-surface lattice to catch, not the level's. Rests on the
owner's one-pixel ruling (a coarser level is drawn where its recorded error
projects to at most one display pixel), the one-contouring rule (the same
mesher on a coarser lattice is not a second mesher), and the derived-product
ruling (a level is part of the group's cached mesh, keyed by its geometry and
the bake identity). The visibility plan's V3c, "Field-derived levels only as a
bake experiment on large static unions", is superseded by this decision and
amended to point here. Material point-sampling at a coarse cell is admitted
because the level is drawn only where its error is sub-pixel.

### D2. Bounds for skinned and multi-slot draws

**What must be decided.** How a draw whose vertices ride dynamic slots gets a
conservative box the kernel can test, and so a level; the falls' 14 slots of up
to 150,000 triangles each and every avatar draw level 0 in every pass today
([open items](open-items.md#engine-job-system), the far cascade item).

**What exists.** `SdfMeshBox.TryOf` refuses a membered mesh; each vertex rides
one member rigidly ([where the pipeline stands](#where-the-pipeline-stands));
the kernel reads the slot table for member vertices and the box's stretch is a
bound on the matrix's largest singular value (`SdfMeshBox.Stretch` **[opened]**).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 2a. Member rest boxes under live transforms: the bake records each member's local box over the vertices that ride it and the root's over the rest; the box is the union of each member's box carried through its slot's current position and orientation and the draw's scale, then the carry; computed where the kernel runs, from the slot table it already binds, or on the host for the rate twin | Exact for rigid per-vertex skinning, which is Puck's only skinning (Argon and Claude **[record]**, `SdfMeshPathBake.Skinned` **[opened]**); `O(members)` per draw, at most `SdfMesh.MaxMembers` | Per-member boxes in the mesh and the record's members table; the kernel's selection rows become derived from slots rather than staged | A vertex on a member bent far from rest is still inside its member's box, so no failure for rigid skinning; a future blended skin would need a refit |
| 2b. Refit from posed vertices each frame | Tightest | A pass over every skinned vertex per frame | Pays per vertex what 2a pays per member |
| 2c. Bounds over a finite animation bank in a dominant bone's space (NanoMesh, Codex **[record]**, opened) | Avoids a refit | Looser; the guarantee ends at the covered animations | Puck's authored motion, IK and effectors are not a bank |

**Favoured.** 2a. **Strongest case against:** the rate decision's host twin
(`SdfMeshCull`, `WorldCreationInstances.Shown`) also needs the box, so the
member boxes must be readable on the host from the frame's packed transforms,
which the dresser already holds.

**Decided.** A membered draw's box is the union of its members' rest boxes
under their live transforms. The invariant: *every box the kernel tests
encloses every position the vertex resolver can emit for that draw*, enforced
at the one door that builds selection rows (the record writer for rigid draws,
the kernel's slot-carried union for membered ones); `unboxed` ceases to exist
as a flag, so no caller can draw past the test. With D1 the coarse levels of a
skinned group are skinned by the same points round and the draw gains levels.
Rests on the raster decision's "no box at all for a skinned draw whose vertices
the GPU places", which this replaces with a box the GPU places too, and on the
authored-motion ruling of V2c (a bound derived from the program where it can be,
never inflated by guesswork).

### D3. A hierarchy above the draw

**What must be decided.** What stands in for a creation's 174 draws far away,
for a union's chunks, and for a stand of trees at the overlook, so that draws
and records fall with distance as triangles do; and what the dense landscape's
far field is.

**What exists.** Nothing combines draws: `SdfMeshChunks.Bake` only splits
(`SdfMeshPathBake.Land` **[opened]** chooses it for a static, non-glass group
with several instances or past `WholeTriangles`). Static copies share a
prototype's mesh (`WorldRigidAssets`, `SdfMeshInstance`); a static copy's
shapes bake as their union (`SdfMeshPathBake.UnionFinal`); the bake already
answers each vertex's winning slot in a points round and stores
`SdfMesh.Members` and `VertexMembers`.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 3a. Field-contoured parents, chosen by the kernel as alternatives to their children: the **creation proxy** (a creation's groups unioned into one coarse contour at rest, each vertex skinned rigidly to the slot whose shape wins there, so the Moth becomes one draw with the D1 chain's triangles; per-slot proxies as the middle tier); the **union parent** (a chunked union contoured at a coarse cell as one closed draw replacing every chunk at once, so no parent ever meets a chunk edge); the **stand parent** (a region of the field over many static copies, the far field of a forest). The kernel draws a parent when its error projects under a pixel, else its children, with the parent's error raised over its children's coarsest so the cut is monotonic; a parent's record names its children's record range so the kernel skips them without a host walk | Every piece exists: unions over instances, the points round, the pixel rule; a parent is a closed replacement and needs no boundary locking, where a cluster DAG locks group borders (Kapoulkine, Claude **[record]**, opened); Codex's own analysis that a per-prototype DAG cannot merge a region **[record]** | A new group kind spanning instances; a kernel rule for alternatives and a record range; a bake cost per parent of one coarse contour | Rigid per-vertex skinning of a coarse proxy stretches across members as it does across a blend seam today; under a pixel it should not show **[hypothesis]**, capture-judged. Coverage of a sparse canopy (see 3d) |
| 3b. Authored HLOD (Simplygon, UE World Partition HLOD; Argon **[search summary]**) | Shipped in open worlds | Authoring per cell; a second asset the author owns | Puck authors fields, not proxies; the engine would carry a proxy the field could derive, which "builds it twice" (AGENTS.md) |
| 3c. Octahedral impostors or billboard clouds (Argon **[search summary]**; Epic's Impostor Baker cited by [visibility](visibility.md#rejected-and-watched)) | Cheap per instance | An atlas per species, a capture pass, a parallax shader | A second representation for shading, picking, reprojection and the visibility record; baked colour where Puck shades from a material identity and the bake "point-samples a quad's winning material" **[opened]** (decisions); no occlusion pyramid contribution without depth output |
| 3d. A hybrid mesh-volume far tier measuring filtered occupancy, silhouette and normal/material distributions from the field (Codex **[record]**; Epic's experimental voxel clusters) | Represents partial coverage a surface cannot | A second raster bin and shading model | Unqualified; its visual validity is a capture question; "Never treat an SDF's sign as an adequate opacity filter" (Codex **[record]**) |

**Favoured.** 3a; 3d watched with the measurement that admits it.
**Strongest case against:** a stand parent is one opaque surface, and a
canopy seen from the overlook is partly transparent; where the overlook reads
wrong against the reference at sub-pixel error, 3d is the answer, not a looser
pixel rule.

**Decided.** The hierarchy above a draw is contoured from the field: creation
proxy, union parent, stand parent, each a closed replacement the kernel chooses
by the one-pixel rule with monotonic error. The invariant: *a parent and its
children are never both drawn in one pass*, enforced in the kernel's one
selection step, which decides a parent's record and its children's range
together. Static far-field marching stays rejected (the frame "does not
interpret SDF programs", [decisions](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)):
a marched far volume would be a second representation for shading, picking and
reprojection, where a contoured parent keeps the one raster path. Rests on the
one-pixel ruling and on "representations are chosen by measured cost"
([the pipeline foundation](../decisions/rendering.md#the-pipeline-foundation)).
This is the dense-landscape plan's far-field tier; its L4 row consumes it.

### D4. Clusters inside a draw

**What must be decided.** Whether and when a draw's triangles are culled in
clusters (meshlets) rather than whole, and how that fits the existing selection
and the planned compaction.

**What exists.** Chunks are clusters in all but size: each a draw with a box,
levels and history, at 8 m bins; finer chunks multiply records (the world
group's far ground at 8 m made 160,000 draws, `SdfMeshChunks` comment via
Claude **[record]**). The visibility plan's V1 owns stable instance identity,
ordered compaction and (geometry, level) buckets.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 4a. Flat clusters over the already chosen level, culled by a compute step that writes compact lists on V1's path (triangle references for vertex pulling, or fixed-size cluster instances), behind a comparison lever; per-draw level choice kept; cluster bounds and cones from the contour's own cells and crossing normals | Codex's first implementation experiment **[record]**; Bevy 0.14 shipped virtual geometry without mesh shaders by appending triangle references (Codex **[record]**, opened); Puck already vertex-pulls | A cluster product in the bake, cluster metadata (at 64 bytes a cluster, a million clusters is 64 MB before levels; Codex **[record]**), a second classification per view | Not automatically a win for tiny instanced parts, where metadata and tests can exceed the raster removed (Codex **[record]**); turns a hidden-wall saving into GPU work that M1 says is not the bottleneck on the 4070 |
| 4b. A cluster DAG with a BVH, group-coherent hysteresis and a complete cut (1c's runtime) | The established answer for huge dense meshes | Everything in 4a plus the builder, BVH, cut and history contracts | The reasons in D1's 1c |
| 4c. Smaller chunks | No new path | Records multiply | 160,000 draws at 8 m already |

**Favoured.** 4a, admitted by a measurement: a large draw partly in view (a
terrain sector, a union chunk, a creation seen from inside) found
geometry-bound on the 2060 or at the Deck's resolution after D1 to D3, with
V1's compaction landed so a cluster list has a home. **Strongest case
against:** none of the three bar devices is shown geometry-bound today, so a
cluster path built now would be a second implementation of culling waiting for
its workload.

**Decided.** Watched, with its first experiment written: flat clusters of the
chosen level, compact lists on V1's buckets, counts of candidates, survivors,
pyramid tests, rejected triangles and reference writes, captures on moving,
cut, portal-clipped and bent geometry. The bounds invariant Codex states is
adopted for the day it lands: *every cluster bound encloses every position its
resolver can emit*, enforced at the derived cluster product and one bound-refit
door; cones are disabled under deformation, two-sidedness or glass until
proven, and never copied into the shadow entry, exit or optical passes. Normal
cones are watched for the cascades, which have no occlusion of their own
(Claude **[record]**). Rests on "fundamentals, driven by features" (a feature
that stresses a large mesh partly in view admits the step) and on "one
implementation" (clusters extend `SdfMeshOcclusion`'s selection, never a second
indirect path).

### D5. Occlusion and history under changing cuts

**What must be decided.** How two-phase occlusion keeps its guarantee when the
set of things drawn changes shape under it: a parent replacing children, a
level appearing, a tier landing, a page evicted.

**What exists.** Phase one draws last frame's visible set at current
transforms, a pyramid of the farthest forward depth is built, phase two tests
the kept boxes and draws the newly visible in the same frame; history is
validated by the staged generation (`if (state.z != c.w) { state = uint4(0u,
0u, c.w, 0u); }` **[opened]**).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 5a. Keep the visible-set approach; reconcile previous visibility onto the current cut by stable identity (a parent inherits its children's visibility and a child its parent's, conservatively), unmatched incarnations go to the late phase | Keeps Puck's approach, no previous pyramid, no reprojection holes (the visibility plan rejected the previous-frame pyramid for this reason) | After a coarse inheritance the early phase may draw more for a frame | Nothing silent: the late phase covers every rejected current candidate |
| 5b. Test the current cut against the previous frame's pyramid with the previous camera, retest rejections against the current pyramid (Nanite's approach, NVIDIA's reject-list variant; Codex **[record]**, opened) | The established approach when streaming invalidates explicit sets | A previous pyramid per view, reject queues | Reprojection holes a camera cut exposes; a change of approach for a problem 5a handles |

**Favoured.** 5a. **Decided.** The invariant Codex states: *previous
visibility or previous depth may choose early occluders, never be the final
reason to omit a currently visible selected surface*; the late phase covers
every rejected current candidate, overflow never drops one silently, a cut or
missing history draws conservatively, and glass, stamp-fade holes and portal
clips occlude only where they wrote depth. Enforced in the one selection step,
which owns both phases. History is keyed by stable identity and generation
(V1's contract), never by a list position; a parent-child transition is one
history record, not two independent hystereses (which could select
incompatible alternatives; Codex **[record]**). The late phase's cost on a cut
frame is the price already paid today ([visibility](visibility.md#what-serves-authors),
"Camera cuts").

### D6. The host's work per moved body

**What must be decided.** What the host pays when a body moves (its records,
its posing) and what evidence lets it pay less.

**What exists.** About two thousand records restaged a frame at flight B,
nearly all compared records changed (the frame-bar packet's CPU item, its
reading in the commit that recorded it); a
rigid dynamic draw's matrix duplicates its slot's pose
(`SdfMeshDresser` **[opened]**); the record has a `FrameSlotWord`
(`SdfMeshRecord` **[opened]**); the rate decision keeps a body posing while any
frustum or cascade keeps its sphere, without occlusion
(`WorldCreationInstances.Shown` **[opened]**); the kernel's visible state is
per draw per pass and is read back for session views (`SdfPortalSight`).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 6a. Rigid dynamic draws read their slot: a draw riding a slot resolves its vertices through the slot's live and previous transforms as a member does, and the kernel moves a slot-frame rest box by that transform; a moved body restages its slots' transform rows, not its groups' records (the Moth's 174 records become 42 slots) | The mesh pass already binds the slot table and reads live and previous transforms for members (`sdfMeshWorldPosition` **[opened]**); V2b's "a moved slot updates its instances' transforms in place" with the transform's one home on the GPU (Claude **[record]**) | A shader change in the three vertex resolvers; the selection rows derived from slots (D2's door); picking and reprojection read the slot | Unverified: whether transform rows upload by change, and the carry, scale and clip words of a body straddling a portal **[record]** (Claude); the straddle adds instances carrying the other clip plane under V1, which keeps the slot shared |
| 6b. Occlusion as one more reason a body is not shown: a body whose draws' boxes no pass kept or saw last frame (camera views by `state.x`, cascades by receiver and entry visibility) and whose pose nothing else read (`PoseReadPack`) is "not shown" for `RateOf`, exactly as a body outside every frustum is today, so it takes the tier `BodyHertz` gives an unshown body | The evidence exists per pass on the GPU; a bit per draw per pass is small (Argon **[record]**, arithmetic); the frustum case already accepts a one-frame-late first pose and the tiers already bound it by part speed (`SdfUpdateRateTiers.BodyHertz` **[opened]**) | A readback of visible bits per pass through the slot-completion seam `SdfPortalSight` uses | Codex's caution: a camera-hidden body may cast a visible shadow or supply a pose to a part consumer; both are already in the decision (`Shown` tests cascades; `PoseReadPack` records consumers) and the occlusion evidence only narrows "kept by a cascade" to "kept and not rejected by its receivers or entry depth" |
| 6c. Leave posing to frustum evidence; let the job system hold unseen work | The job system's first value is "holds unseen work" ([plans](README.md)) | None here | "Unseen" needs the evidence 6b supplies; without it the scheduler holds nothing behind a wall |

**Favoured.** 6a and 6b. **Decided.** A rigid dynamic draw reads its slot, and
occlusion joins frustum and cascade evidence in the one rate decision. The
invariant for 6b: *a held body is one no pass showed any draw of last frame and
no consumer read*, enforced in `RateOf`, the one door every rated body passes;
the one-frame lag at reappearance is the same the frustum case has and the
tiers bound. Rests on the update-rate ruling (a reduced rate switches only
where a held update jumps by at most two pixels for the speed it is sized for)
and on V2b. The frame-bar packet's CPU item owns 6b's landing.

### D7. The detail budget

**What must be decided.** What bounds the bake's detail: today the finest cell
is derived from authored geometry alone (round sections over three cells,
`WorldCreationInstances.BodyDetail`; floored at `WorldPlacementStamper.FinestDetail = 0.002f`
**[opened]**), so the nose bakes at 0.5 mm whether a view can resolve it, and
nothing reads the screen.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 7a. Demand-bounded tiers: a group's tier is the coarsest power of two at which its finest cell stays under half a display pixel at the nearest demand seen (`SdfMeshDemand` holds each view's eye and pixels a metre at one metre **[opened]**); the tier joins the geometry key as a fact (`SdfMeshGeometryBuilder.AppendFact` **[opened]**), so each tier is its own cache entry, prewarmable in a player's build; the frame draws the finest tier baked so far, a counter reports detail owed, and the demand-first queue bakes the next tier; the D1 chain hangs under each tier | Virtual texturing applied to geometry; the bake pays what views resolve; no finest-cell ceiling for a mirror-close view (Claude **[record]**: the nose's cell is a sixth of a pixel at the chase camera and over three pixels at 0.3 m on 2160 lines, arithmetic) | A tier per group in the key; a residency counter; the bake queue reads demand it already has | Whether the owner's cold-bake bar counts on-demand tiers ([the owner call](#the-owner-call)); a view outrunning the bake sees a coarser tier for a moment, reported, never refused |
| 7b. A per-creation triangle or byte cap | Simple | A refusal by count | Contradicts "no scene ceiling is sized for a worst case ... Detail follows projected size (the one-pixel rule), never a body's index" **[opened]** (decisions) |
| 7c. Spatial refinement: an octree of field-contoured nodes each at its depth's cell, nodes of one depth partitioning one lattice | Refines the nose without baking the back of the head | Seams between depths need D1's seam answers (skirts, or a parent above) | Later; the group tier needs no seam work and comes first |

**Favoured.** 7a, then 7c. **Decided.** Detail is budgeted by demand, never by
a cap: the finest tier is a cache tier identified from its inputs (group
geometry, tier), baked on demand and reported as owed. The invariant: *what the
frame draws is always a complete tier of the group*, enforced by the residency's
publication door (a tier publishes whole or not at all). The budget surface is
`world.budget` per creation: triangles per level and tier, bytes resident,
tiers owed, beside `puck creation stats`. Rests on the derived-product ruling
and the no-ceiling decision.

### D8. Shadows

**What must be decided.** How casters are selected and levelled per cascade,
and whether shadow storage is virtualized.

**What exists.** The far cascade uses the camera-depth receiver mask and
two-phase light-space entry depth through the same kernel; exit and optical
lists never depend on entry visibility; a cascade takes the coarsest level
whose error is within one of its texels (`SdfMeshDetail.Of(cascade, texels)`);
the filter reach is `SdfMeshCull.ShadowFilterTexels = 18f` **[opened]**; with
box-only selection the far cascade draws nearly every caster at the chain's
coarsest level in two layers ([open items](open-items.md#engine-job-system),
the [high] far-cascade item).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 8a. The D1 chain and D3 parents under the existing texel rule, per cascade; D2 boxes let membered casters cull; receiver and entry culling extended to other cascades where measured (V5) | The far cascade's limit is the chain's stop at two cells **[opened]** (open items); every cascade already selects by texel | None beyond D1 to D3 | Nothing new |
| 8b. Cluster or node caster rejection against receiver pages (Codex **[record]**) | Tighter than draw boxes | D4's product | Waits for D4 |
| 8c. Virtual shadow maps: page tables, dirty masks, caching, all three layers (depth, exit, optical) paged consistently | Cache hits avoid redraw for static content (Epic; Codex **[record]**, opened); Timberdoodle's resolution-matched VSM (Codex **[record]**, non-peer-reviewed) | Pools, page tables, invalidation keyed by light, geometry incarnation, level and layer; a moving sun and wind erase the cache (Codex **[record]**); a cached 2048² D32 layer is 16 MiB a cascade ([visibility](visibility.md#v5-multi-view-and-shadow-work)) | The day program and the meadow's wind, which the debug world runs by default |

**Favoured.** 8a now, 8b with D4, 8c rejected for now. **Decided.** Shadow
levels come from the chain by the texel rule; caster culling tightens with D2
and D4; no shadow cache or page pool is built while the sun moves and the
meadow blows. What would change it: a cascade's measured work dominated by
static redraw with unchanged projections ([visibility V5](visibility.md#v5-multi-view-and-shadow-work)),
and a page identity that includes light projection, geometry and deformation
incarnation, level and layer (Codex **[record]**). The transmission plan's
identity peel is unaffected.

### D9. Streaming

**What must be decided.** How detail that is not resident arrives: tiers,
levels and parents under a byte budget, across views and portals.

**What exists.** Input-derived identities (`SdfMeshStore`), a library with an
age-ordered byte budget (`SdfMeshLibrary` **[opened]**), a region arena with
keyed placement (`SdfMeshRegionArena`), background landing with
completion-gated publication, near-first demand (`SdfMeshDemand`), and the
[residency decision](../decisions/residency.md)'s rule that only presentation
follows the observer.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 9a. Tiers, levels and parents as entries of the existing store, library and arena, with demand from every view and cascade that renders (a visible portal demands detail the root camera does not), coarse coverage resident while finer tiers land, eviction establishing a coarser complete tier first | Codex's residency invariant and NVIDIA's streaming design (groups as the topology-safe unit, pages as the I/O unit, install before publish; **[record]**, opened) map onto homes Puck has; "Do not add a second cache with manually versioned payload validity" (Codex **[record]**) | Group and tier addressing in the library and arena; request priority by display error | A camera pose must never enter a geometry's identity because that camera requested detail (Codex **[record]**): the tier is a fact about resolution, not about a view |
| 9b. A separate cluster-page streamer | Purpose-built | A second cache and a second lifetime | Rule 7 |

**Favoured.** 9a. **Decided.** Streaming is the residency's existing lifecycle
extended to tiers and parents. The invariant (Codex's, adopted): *every
GPU-visible address and every selected cut names a fully installed product and
stays valid until every reader finishes*, enforced by the residency's
publication and fence-lifetime door extended to tiers; GPU feedback is
asynchronous, bounded, deduplicated and prioritized by display error, and the
host never waits on it. Rests on the residency decision and the host-primitives
ruling (no spinning on feedback).

### D10. Hardware reach

**What must be decided.** What the baseline every bar device runs is, what is
an enhancement, and how an enhancement falls back.

**What exists.** Compute selection, counted indirect draws, vertex pulling and
the visibility record on both backends; no mesh-shader or 64-bit-atomic
enablement ([where the pipeline stands](#where-the-pipeline-stands)).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 10a. Baseline: compute instance, node and cluster selection; ordinary vertex and fragment raster with the depth attachment and the existing visibility resolve; bounded GPU queues; compaction and buckets; 32-bit synchronization | Every bar device runs it; the lowest-risk path under Proton (Codex **[record]**); Bevy proved the concept without mesh shaders (opened); NanoMesh rasterizes into a 32-bit visibility buffer on mobile (Codex **[record]**, opened) | None new | Nothing on the bar devices |
| 10b. Mesh and amplification shaders as a measured output adapter over the same cluster product | Turing and Ada support them; RADV enables `VK_EXT_mesh_shader` on GFX10.3+ (Codex **[record]**, Mesa source opened), but the Deck's installed stack and vkd3d-proton's tier mapping are unqualified | Backend feature discovery, enablement, pipeline admission and recorder support on both backends; vendor tuning disagrees (64v/126t Turing, 128v/256t AMD; Codex **[record]**) | A task shader that launches almost no surviving work loses to the compute list (AMD; Codex **[record]**) |
| 10c. Compute micropolygon raster with a packed 64-bit depth-and-id atomic | Nanite, Bevy 0.15 (opened) | Int64 image or buffer atomics queried and enabled per backend; a second rasterizer reproducing portal clips, stamp fade, skin, displacement, winding, identity and shadow layers (Codex **[record]**) | Unmeasured on any bar device; "Never emulate a 64-bit depth+ID update as two unrelated 32-bit atomics" (Codex **[record]**) |
| 10d. Work graphs | 4070 only ([visibility](visibility.md#rejected-and-watched)) | A second implementation | The 2060 and the Deck |

**Favoured.** 10a as the baseline; 10b, 10c and 10d as enhancements that read
the same data, give the same result within the still tolerance, are measured to
pay on their hardware and fall back automatically. **Decided.** Nothing in D1
to D9 depends on a capability the 2060, the 4070 and the Deck do not all have;
every enhancement is a second implementation that earns its place by the
upper-hardware ruling (AGENTS.md, "Upper hardware enhances; the baseline is
cross-platform"), and each begins with backend capability discovery and
enablement, not a shader. Qualification is three legs: Direct3D 12, native
Vulkan, and Vulkan under Proton on the Deck; a 4070 at the Deck's resolution
qualifies nothing on the Deck.

## Puck-native combinations

The owner's ruling asks what Puck can do that others cannot. This round found
the detail problem is where the source pays:

- **The field as the error oracle and the level maker.** A triangle importer
  has only its tessellation; Puck can ask the field again at any cell, so a
  level's error is a distance and a level is a contour, not a collapse (D1).
  Meshoptimizer and Nanite measure displacement; Bevy's author calls perceptual
  simplification unsolved (opened). Puck's one contouring does both jobs at
  once.
- **Derived products identified from inputs make the finest tier a cache.**
  Virtual texturing's idea applied to geometry (D7): a tier is a fact in the
  geometry key, so it is baked on demand, cached, prewarmed in a player's build
  and never recorded as truth. A triangle asset's finest level is its import.
- **Rigid per-vertex skinning makes exact bounds and proxies cheap.** Each
  vertex rides one slot, so a member box under a live transform is exact (D2),
  and a creation proxy is skinned by the points round that skins level 0 (D3).
- **A parent is a closed region of one field.** A union parent or a stand
  parent needs no boundary locking because it replaces its children whole; a
  cluster DAG locks every group border (D3).
- **Authored motion is declarative, so its bounds are derived**, not
  inflated: V2c's swept box and D2's member boxes come from the program and the
  slots, where a material's world-position offset cannot be bounded
  ([visibility](visibility.md#puck-native-combinations)).

Where the borrowed technique simply wins: the two-phase depth pyramid over any
field-inscribed occluder ([visibility](visibility.md#puck-native-combinations));
order-preserving compaction and buckets (V1); Nanite's and meshoptimizer's
group-replacement contract as the reference whenever D4's clusters grow levels
of their own; the texel rule for shadow levels; and NVIDIA's streaming
discipline (install before publish, coarse fallback resident) as D9's model.

## Rejected and watched

| Technique | Verdict | Reason | What would change it |
|---|---|---|---|
| Deeper collapse chain, member-aware collapse | Rejected as the chain; member-aware collapse kept as G1's fallback | The collapse stops at locked places and two cells (`CellShares` **[opened]**; the card back and the oak, open items) | G1 rejecting field levels on error, defects or cost |
| Nanite-style cluster LOD DAG | Watch (was watched in [visibility](visibility.md#rejected-and-watched)) | Aggregates, a second builder beside the contouring, displacement as error; D1's chain reaches the same end from the field | D4's experiment showing visible triangles high because a draw's levels stop while it is partly in view |
| Meshlets inside a draw (flat clusters) | Watch, first experiment written (D4) | No bar device is shown geometry-bound; tiny instanced parts can cost more in metadata than they save | A large partly visible draw measured geometry-bound on the 2060 or at the Deck's resolution after D1 to D3, with V1 landed |
| Normal-cone culling | Watch | Nanite found it not worthwhile after occlusion; meshoptimizer and NanoMesh keep it (Codex **[record]**, opened) | Clusters adopted; the cascades, which have no occlusion of their own, are the first place to measure |
| Mesh and amplification shaders | Enhancement, not baseline (D10) | No enablement on either backend; Deck Proton leg unqualified | A measured win over the compute list on each device, with automatic fallback |
| Compute micropolygon raster, 64-bit atomics | Enhancement, last (D10) | A second rasterizer; unmeasured on any bar device | Residual fine triangles shown to dominate setup after D1 to D4 and V4 |
| Work graphs | Enhancement on the 4070 only | [visibility](visibility.md#rejected-and-watched) | Unchanged |
| Virtual shadow maps, cascade caching | Rejected for now (D8) | The day program and wind erase the cache; three layers to page consistently | Static redraw with unchanged projections measured dominating a cascade |
| Octahedral impostors, billboard clouds | Rejected (D3) | A second representation for shading, picking and reprojection; baked colour against material identity shading | Nothing; a field-contoured parent is the far field |
| Authored HLOD proxies | Rejected (D3) | The field derives what an author would hand-build | Nothing |
| Field marching for the far field | Rejected (stands) | [decisions](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs) | Nothing |
| Hybrid mesh-volume far tier (filtered occupancy, voxel clusters) | Watch | Partial coverage of a sparse canopy is real; the representation and its raster bin are unqualified | The overlook's stand parents failing the reference at sub-pixel error |
| Transvoxel-style transition cells between chunk levels | Rejected (Claude **[record]**) | Marching-cubes tables; a transition depends on the neighbour's level, which a per-draw GPU choice does not know at bake time | Nothing; the union parent (D3) is the seam answer |
| Previous-frame pyramid in place of the visible set | Rejected (stands, [visibility](visibility.md#rejected-and-watched)) | D5's reconciliation keeps the visible set | Nothing |
| Vertex animation textures, animation banks for bounds | Rejected | Puck's motion is an authored program, not a sampled bank (D2) | Nothing |
| A per-creation triangle or byte cap | Rejected (D7) | The no-ceiling decision | Nothing |

## The build order

Each step is one lane with a check on the debug world at the highest quality.
Counted work stands beside every frame time, and frame times are read in the
lead's settle windows, never by a lane under load. G1 is offline and needs no
GPU; G2 onward run on Direct3D 12 with a windowed Vulkan leg. Steps in the
visibility plan keep their names there; this order interleaves with V1 and V2
as noted.

| Step | Lands | Check on `worlds/debug` | Success line |
|---|---|---|---|
| **G1. Field-levels audit mode** (offline; brief-ready below) | A field-levels mode in the CPU bake that `puck mesh audit --cpu` drives (`MeshCpuBake`, `SdfMeshPathBake.Land`), printing per group and level: triangles, the two-sided field error, open edges and flips from the audit's own measures, samples and taps, and the land thread's cycles | `puck mesh audit --world worlds/debug/avatars/moth.puck --prototype moth --cpu --field-levels` and the same on one chunked union of `worlds/debug/hall.puck`, against the collapse on the same groups | Adopt field levels for closed groups when (a) at equal or fewer triangles every level's error is no worse and the chain continues past level 2, the Moth's coarsest total falling by an order of magnitude from 238,235; (b) no level adds open edges or flips beyond level 0's; (c) the chain's samples and land cycles do not exceed the collapse's. Any failed clause names the fallback (Argon's member-aware collapse for skinned groups) or the fix |
| G2. Field levels in the bake and region | Levels contoured from the field for closed groups, their own vertices laid after the merged ones (a representation change to `SdfMeshLevel`), skinned groups' levels through the points round, `SdfMeshCollapse` kept only for chunked unions, the dilated variant where it wins | `world.counters gpu pass sdf.world$mesh` vertices with the Moth at boom 1.5, 4.2 and 12 m and at 50 and 100 m under `world.detail auto`, against today's build; captures from every side at each distance; `puck mesh audit` clean on the levelled groups; cold-bake stage seconds per backend | Vertices fall at every distance past 4.2 m with captures meeting the reference; the cold bake stays within the bar or the owner's call on tiers is taken first |
| G3. Member boxes and `unboxed` retired | Per-member rest boxes in the mesh and record; the kernel's union under live slots; the host twin for the rate decision; `unboxed` deleted | Camera facing away from the falls and the garden crowd: membered draws' vertices in `world.counters gpu` fall to zero when off-frustum or occluded, and they select levels 1 and 2 at distance; `world.view.pick` on a bent limb still names it; a cascade still shadows a hidden body onto a visible receiver | No membered draw drawn in a pass that cannot show it; no missed surface in a presented burst of a walking Moth and a portal straddle |
| G4. Slot-resolved rigid draws (V2b's design) | A rigid dynamic draw reads its slot's live and previous transforms; selection rows derived from the slot's rest box; transform rows uploaded by change | `dotnet-trace` of the frame thread at flight B: `sdf.instances records.changed` falls from about two thousand to the moved slots' count; `world.debug-view motion` still on a moving body; a straddling body clipped on both sides at `debug.go portals` | Records restaged per moved body equal its slots, not its groups |
| G5. Occlusion-aware rate hold (the frame-bar CPU item) | A visible bit per draw per pass read back through the slot-completion seam; `RateOf` treats an unshown-by-every-pass body as not shown | Flight B facing the motion gallery through the stairwell wall: `world.rates` lists the exhibits at `Held`; stepping into the gallery shows them posed on their first visible frame within the tiers' bound | Held bodies behind the wall; no pose jump beyond two pixels at reappearance in a presented burst |
| G6. Creation proxy and union parent | The alternatives rule in the kernel (a parent's record names its children's range), proxies contoured at rest and skinned by the points round, union parents above chunked unions | The Moth at 50 and 100 m draws one record; `world.counters gpu` draws and vertices at the garden crowd from the lawn; the hall's unions from the forest pose at (47.80, 0.02, -116.84); captures against the reference; a parent and its children never both drawn (a count per pass) | Draws per distant creation fall to one; the hall's far draws fall to its parents; captures meet the reference |
| G7. Stand parents for the forest (dense landscapes L4's far field) | A bake group over many static copies contoured as one region, with the dilated variant where coverage asks | `view.override layout three-quarter` then `view.override camera forest-overview`, and `body.pose spawn:forest 0` looking south: draws, vertices and cascade vertices against G6's build; captures from the overlook and the road against the reference; the nested forest view's own counts | The overlook's far belt draws as parents at sub-pixel error and reads as a forest; if it does not, the hybrid far tier is briefed, not the pixel rule loosened |
| G8. Demand-bounded tiers | The tier fact in the geometry key, the finest tier baked on demand, `world.residency` reporting tiers owed, `world.budget` per creation | Spawn in the studio and walk to the Moth's face at boom 1.5 m: the next tier lands and the counter clears; the cold bake from an empty store on both backends | Cold-bake seconds against today's with the owner's answer on what counts; no view ever refused, only owed |
| G9. Clusters inside a draw (D4's experiment) | Flat clusters of the chosen level on V1's buckets, behind a lever | The terrain sector and the hall shell from inside, on the 2060 or at the Deck's resolution: candidates, survivors, pyramid tests, rejected triangles, reference writes; moving, cut, portal-clipped and bent geometry captured | Kept only if it removes measured geometry-bound work without a missed surface, a swapped pick or a stale bound |

G1 and G3 share no files and run beside V2a and V1a; G2 waits on G1; G4 is
V2b and lands where the visibility plan places it; G5 waits on nothing but the
readback seam; G6 waits on G2 and G3; G7 on G6; G8 on G2; G9 on V1b and a
measurement. The visibility plan's table keeps V3a and V3b; V3c is replaced by
G1 and G2.

### G1, the first step, as a brief

Offline, load-independent, no GPU, no lease. Add a field-levels mode to the CPU
bake under `puck mesh audit --cpu` (the audit drives `MeshCpuBake`, which runs
`SdfMeshPathBake` on the CPU evaluator). In `Land`, after the planar merge, for
a closed group: contour the group's field again at `c0 * 2^k` for `k = 1, 2,
...` through `SdfSampledLattice` and `SdfDualContouring` (no second mesher),
until a level holds under a few dozen triangles or is empty; measure each
level's error both ways against the field (value over gradient at level
vertices, centroids and edge midpoints; level-0 vertex distance to the level's
triangles through a triangle hierarchy); raise each error to the finer level's;
run the audit's open-edge and flip measures on each level; count samples, taps
and the land thread's cycles. Print per group and level. Run it on the Moth and
on one chunked union of the hall, beside the collapse's levels on the same
groups. The success line is the table's: (a), (b) and (c) together adopt field
levels for closed groups; a failed clause names what it rejects. Nothing lands
in a running World in G1; its deliverable is the measurement and the mode.
Systems it reuses, by name: `SdfSampledLattice`, `SdfDualContouring`,
`SdfSampledLattice.PassesBetween`, `SdfMeshAudit`, `SdfMeshIrradianceScene`'s
triangle hierarchy, `SdfMeshPathBake.Land`, `MeshCpuBake`. What it must not do:
write a second mesher, change the selection kernel, or touch the store's
identity (the mode is offline).

### The reproduction

This memo re-ran the Moth's forced level-2 CPU audit from its own CLI copy to
confirm the floor the ranking rests on:

```
dotnet .claude/scratch/geom-synthesis-cli/Puck.Cli.dll mesh audit --world worlds/debug/avatars/moth.puck --prototype moth --cpu --level 2
```

Summed over the 180 `group` lines (an `awk` over the `triangles,` field) the
forced level 2 holds 238,235 triangles, and the done line prints
`level2=238235`; the face's groups print `nose: 24045 triangles`, `eye-left:
9143`, `eye-right: 9102`, `face: 2218` and `pupil-left: 562`, Claude's table
exactly. Claude's own sums ran 449 higher at every level (a counting
difference in its `awk`, not a bake difference); the levels are a pure function
of the mesh, so the two runs agree where they count the same lines. The run's
full output is in the lane's commit.

## The owner call

**Does an on-demand finest tier count toward the one-minute cold-bake bar?**
Under D7 the cold bake pays each group's coarsest tiers and the finest tier
only where a spawn view resolves it; a view that later comes closer bakes the
next tier on demand, counted as owed and never refused. The bar's words are
"the debug world's meshes bake from an empty store in one minute at most per
backend on the lead machine". Two readings:

- *The bar counts what the spawn views resolve.* The cold bake is what a player
  waits for at spawn; a tier a later view demands is streaming, as a store load
  is. Under this reading D7 shortens the cold bake and G8's check reads the
  spawn's bake alone.
- *The bar counts every tier any view in the debug world can demand.* The bake
  is "the debug world's meshes", all of them, at their finest. Under this
  reading D7 changes nothing about the bar and the chain's and parents' costs
  must fit beside today's finest cells.

This memo does not decide it. Until the owner does, G8 reports both numbers
(the spawn's cold bake, and the cold bake of every tier a tour of the places
demands) and nothing lands that depends on the first reading alone. What brings
the call back: G2's cold-bake stage seconds.

## Found while reading

Defects the round surfaced that stand on their own, each a small fix for
whichever lane next touches its file:

- A chunk of a displaced draw carries the whole height field's band.
  `SdfMeshDresser` sets each draw's `Displacement = group.Displacement` whether
  or not the group is chunked, and `SdfMeshBox.TryOf` widens the box by
  `displacement.Low` to `displacement.High` **[opened]**, so a valley-floor
  chunk's box reaches the peak's height and the pyramid and receiver tests
  keep it where a per-chunk column range would reject it (Argon's hypothesis,
  supported by the code). Tighten the band to the chunk's own columns at the
  one place the chunk's box is built.
- The common context, the rendering skill's prose and `SdfMeshBox.TryOf`'s
  summary describe the host as the place the box test runs; the decision text
  is corrected and the skill's later dispatch section is right (Codex
  **[record]**). The `TryOf` summary and any remaining skill sentence saying
  "host" change with G3, which deletes the host-only reading.

## What would change the answer

- G1 finding the chain's error worse than the collapse's at equal triangles, or
  its samples and cycles above the collapse's, sends skinned levels to Argon's
  member-aware collapse and static levels to V3b's faster simplifier, with D3's
  parents still contoured from the field.
- A bar device measured geometry-bound on a large partly visible draw after
  G2 to G7 admits G9; one bound on triangle setup after G9 and V4 admits the
  compute rasterizer as an enhancement.
- The overlook's stand parents failing the reference at sub-pixel error admits
  the hybrid far tier, briefed with filtered occupancy measured from the field.
- A cascade dominated by static redraw with unchanged projections reopens
  cascade caching (V5) and, behind it, page-based shadow storage.
- The owner's reading of the cold-bake bar sets what G8 must fit.

## Sources

Opened by Codex: Karis, Stubbe and Wihlidal, "Nanite: A Deep Dive", SIGGRAPH
2021 Advances, <https://www.advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>
(pages 16–19, 30–50, 56–61, 68–77, 81–85, 119, 122–140); meshoptimizer,
<https://meshoptimizer.org/>, its bounds contract
<https://raw.githubusercontent.com/zeux/meshoptimizer/master/src/meshoptimizer.h>
and `clusterlod.h`
<https://raw.githubusercontent.com/zeux/meshoptimizer/master/demo/clusterlod.h>;
NVIDIA `vk_lod_clusters`, <https://github.com/nvpro-samples/vk_lod_clusters>,
its LOD generation, two-pass culling and streaming documents; Bevy 0.14, 0.16
and 0.17 release and author accounts,
<https://bevy.org/news/bevy-0-14/>, <https://jms55.github.io/posts/2024-06-09-virtual-geometry-bevy-0-14/>,
<https://jms55.github.io/posts/2024-11-14-virtual-geometry-bevy-0-15/>,
<https://jms55.github.io/posts/2025-03-27-virtual-geometry-bevy-0-16/>,
<https://bevy.org/news/bevy-0-17/>, and its `cull_bvh.wesl`, `asset.rs` and
`mod.rs`; Kubisch, "Introduction to Turing Mesh Shaders",
<https://developer.nvidia.com/blog/introduction-turing-mesh-shaders/>; the
Direct3D mesh shader specification
<https://github.com/microsoft/DirectX-Specs/blob/master/d3d/MeshShader.md> and
SM 6.6 int64 atomics
<https://microsoft.github.io/DirectX-Specs/d3d/HLSL_SM_6_6_Int64_and_Float_Atomics.html>;
the `VK_EXT_mesh_shader` proposal
<https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_mesh_shader.html>
and the two atomic feature pages; Burns and Hunt, "The Visibility Buffer",
<https://jcgt.org/published/0002/02/04/paper.pdf>; Cao, "NanoMesh", SIGGRAPH
2024, <https://advances.realtimerendering.com/s2024/content/Cao-NanoMesh/AdavanceRealtimeRendering_NanoMesh0810.pdf>;
AMD meshlet compression and mesh-shader best practices,
<https://gpuopen.com/learn/mesh_shaders/mesh_shaders-meshlet_compression/>,
<https://gpuopen.com/learn/mesh_shaders/mesh_shaders-optimization_and_best_practices/>;
Epic, virtual shadow maps
<https://dev.epicgames.com/documentation/unreal-engine/virtual-shadow-maps-in-unreal-engine?application_version=5.5>,
Nanite foliage <https://dev.epicgames.com/documentation/unreal-engine/nanite-foliage>
and Nanite-enabled skeletal content
<https://dev.epicgames.com/documentation/unreal-engine/working-with-naniteenabled-content>;
Sakmary, "Resolution-Matched Virtual Shadow Maps", CESCG 2025,
<https://cescg.org/wp-content/uploads/2025/04/Sakmary-Resolution-Matched-Virtual-Shadow-Maps.pdf>
and Timberdoodle <https://github.com/Sunset-Flock/Timberdoodle>; Valve's Deck
specifications, NVIDIA's Vulkan driver page, a Mesa RADV snapshot
<https://chromium.googlesource.com/external/gitlab.freedesktop.org/mesa/mesa/+/7a3a8748c95cc61b73d9b8c0aa93622f554396c4/src/amd/vulkan/radv_physical_device.c>
and vkd3d-proton's `device.c`.

Opened by Claude: Kapoulkine, "Billions of triangles in minutes",
<https://zeux.io/2025/09/30/billions-of-triangles-in-minutes/>; Bevy 0.14's
author account (above); Epic's Nanite foliage (above); Lengyel, Transvoxel,
<https://transvoxel.org/>. Known from a catalogue record only: Schaefer, Ju and
Warren, "Manifold Dual Contouring", IEEE TVCG 13(3), 2007,
<https://people.engr.tamu.edu/schaefer/research/dualsimp_tvcg.pdf>.

Argon's external sources were search summaries (HLOD practice, octahedral
impostors, billboard clouds, Ghost of Tsushima's grass, CDLOD, Unreal's update
rate optimization, Planet Coaster's crowds, vertex animation textures); this
memo keeps from them only the mechanisms the other records or the code
corroborate, and rests no ruling on an Argon-only external claim.

---

[Plans](README.md) · [Visibility and submission](visibility.md) · [Dense landscapes](dense-landscapes.md) · [Rendering](rendering.md) · [Decisions](../decisions/rendering.md)
