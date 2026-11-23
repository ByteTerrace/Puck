# Virtual geometry: visibility and detail at scale

The owner asked how good Puck's levels of detail, frustum and occlusion culling
are and what would be better, meshlets "or something even better for Puck".
This page decides what Puck's detail and reach pipeline becomes and orders the
build as steps each checked on the debug world. It weighs three researchers'
records (Codex on the GPU-driven cluster, streaming, shadow and hardware
literature; Argon on the far field and animated creations; Claude on a
creature's bake and the Puck-native case) against the code, in
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

Labels: **[record]** is a claim taken from a researcher's findings, named by
researcher and not reopened here; **[search summary]** is an external fact
known only from a search summary, kept only where the mechanism is
uncontested; **[inference]** is this page's reasoning; **[hypothesis]** is a
prediction nothing has measured. Citations into the code are by symbol.

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
build first. Where each stands:

- **The finest tier is a cache baked on demand** at what the views resolve,
  with the detail it owes bounded and reported: virtual geometry from the
  field. It is built ([D7](#d7-the-detail-budget), `SdfMeshTier`); what remains
  of its arrival contract belongs to
  [the streaming plan](streaming-and-memory.md).
- **Coarser levels contoured from the field at an outward offset are not
  adopted.** Three offline experiments measured them against the collapse
  ([D1](#d1-the-unit-of-detail)): they do not lower the floor by the order of
  magnitude the design needed, and they fail on carved and thin parts. The
  collapse stays the level source, the audit mode that measures a field level
  stays as an instrument, and the search for a level source past the collapse's
  floor is parked with the criteria that reopen it.
- **A skinned draw's box must cover its blend**, not only its members' rest
  boxes ([D2](#d2-bounds-for-skinned-and-multi-slot-draws)): a vertex can ride
  two members through a normalized dual-quaternion blend, so the box is the
  members' boxes under their live transforms widened by the blend's admitted
  envelope, which [moving blends](moving-blends.md) owns.
- **A hierarchy above the draw is contoured from the field** (per-member
  pieces and, where the motion program bounds the pose, one proxy per distant
  creation; one parent per chunked union; one parent per stand of static
  copies), one parent per render class, each a closed replacement the kernel
  chooses by the same pixel rule and each triangle naming the child it came
  from. The forest's far field is then a region of the field and not an
  impostor atlas or an authored HLOD, kept where it beats the shared prototype
  path on bytes, bake work and frame cost ([D3](#d3-a-hierarchy-above-the-draw)).
  A parent faces the two clauses the field levels failed and is measured
  against them before it is built.
- **Clusters inside a draw, compute micropolygon raster, mesh shaders and
  virtual shadow maps are watched**, each with the measurement that admits it,
  because the floor, the skinned gap and the far field dominate at MMO scale
  and none of those four touches them.

Borrowed techniques win where they are better (the two-phase depth pyramid,
order-preserving compaction and buckets from the visibility plan, meshoptimizer
and Nanite's group-replacement contract as the reference for any cluster
hierarchy, the texel rule for shadow levels). The Puck-native combination that
has paid is the demand tier: a derived product identified from its inputs can
be baked on demand and cached where a triangle asset cannot.

## Where the pipeline stands

- **Selection is on the GPU, per draw.** The decision text now says so
  (`docs/decisions/rendering.md`, "A raster pass draws only what
  it can show"): "a world box per draw tested on the GPU by the selection
  kernel (`SdfMeshOcclusion`, `sdf-mesh-selection.hlsli`, with `SdfMeshBox` and
  `SdfMeshCull` as the host-side twin that glass visibility and update rates
  still use)". The kernel's early step keeps a draw by `sdfSelectionKeeps`,
  rejects a caster whose receivers are hidden by `sdfReceiverMayShadow`,
  chooses its level by `sdfSelectionLevel`, and the late step tests the kept
  boxes against the depth pyramid by `sdfOcclusionMaySee`
  (`passes/sdf-mesh-occlusion.comp.hlsl`, `CSMain`).
- **Every phase writes one command per candidate, empty or not**
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
  (`sdfSelectionLevel`), so a draw past the distance where its
  last level fits stays at that level. `SdfMeshCollapse.CellShares` is
  `[0.25f, 2f]` and `NormalDegrees` is `[2f, 6f]`: two coarser
  levels whose error budgets are a quarter and two of the group's contour
  cell, and `KeptShare = 0.75f` drops a level that saves less than a quarter.
  The collapse never moves or makes a vertex; a corner where three faces meet,
  an open edge and a place whose triangles do not close a ring never move
  (`SdfMeshCollapse` summary).
- **A skinned or multi-slot draw has no box and no levels**:
  `SdfMeshBox.TryOf` returns `false` when `!mesh.Members.IsEmpty` ("those
  vertices ride dynamic-transform slots the GPU reads, so no host box bounds
  them and every pass draws them"), and `SdfMeshCollapse.Levels` returns the
  mesh unlevelled when `!mesh.Members.IsEmpty`. In the kernel `unboxed`
  short-circuits every test: `kept = ... (unboxed || sdfSelectionKeeps(...))`,
  `bool was = unboxed || state.x != 0u;`, `visible = kept && (unboxed ||
  sdfOcclusionMaySee(...))`, and `sdfSelectionLevel` returns 0 for it. Every
  such draw is drawn at level 0 in the early phase of every view and every
  cascade. Moving-blends steps 1–3 supersede the original one-member premise:
  `SdfMeshSkin.Build` supplies two-owner influences, and `sdfMeshWorldVertex`
  resolves their group-relative dual quaternion and corrected normal. Rigid
  one-member vertices retain the fast path. A future bound must cover the
  admitted blend's envelope, not just the union of member rest boxes.

- **The host's remaining per-draw cost is the record, not the test.** A draw's
  record is `DrawWords = 64` words holding the matrix, the normal matrix, the
  carry, the clip plane, the level table address and the selection rows
  (`SdfMeshRecord`). `SdfWorldTables.StageChangedDraws` compares
  a list against "A copy of each draw last staged" and restages what changed
 . The frame-bar packet records that staging as "already
  proportional to the draws that changed (`sdf.instances`' `records.changed`
  is almost all of `records.compared`)"
  ([open items](open-items.md#engine-job-system), the CPU item); the lane that
  recorded it read about two thousand records a frame at flight B, a reading
  kept in its commit. The shared context's "about 3,100 draws restaged per
  frame at spawn" is not a reading at this tree (Codex **[record]**).
  A rigid dynamic draw's matrix is rebuilt from its slot's pose by
  `SdfMeshDresser`: `(Matrix4x4.CreateScale(scale) *
  Matrix4x4.CreateFromQuaternion(orientation)) * Matrix4x4.CreateTranslation(position)`,
  so a moved slot restages every group riding it.
- **Posing ignores occlusion.** `WorldCreationInstances.Shown` keeps a body
  posing while any rate view's `camera.MaySee` or any cascade's `cull.Keeps`
  keeps its sphere, and the occlusion result the kernel writes
  per draw (`sdfMeshOcclusionVisibleRW[draw]`, `state.x`) is read back by
  nothing. The selection marks (one stage in eight records which draws a
  phase emitted and at which level, `SdfWorldTables.ObserveSelectionMarks`)
  are per-draw submission evidence, read only as counters so far, and late
  by a few frames. What else is read back is `SdfPortalSight`'s own queries:
  the kernel's sight step tests "Four rows per screen" against the same
  pyramid through `sdfOcclusionMaySee` and writes one word per query at
  `SDF_SIGHT_COUNT_BASE + id.x`; `SdfMeshOcclusion.ReadCounts` hands that range
  back once the slot completes, and `SdfPortalSight` applies a result to the
  camera it was captured for, or to a later eye inside the reach its second
  bit proved (`sdfOcclusionHiddenWithin`; "Unknown, changed and retired
  observations always keep demand"). It is a reusable per-query box test with
  a completion seam, not per-draw evidence; D6 builds on it
  as such. The frame-bar packet records the consequence of posing without
  occlusion ([open items](open-items.md#engine-job-system), the CPU item): at
  flight B the motion gallery's exhibits pose at 60 Hz behind the stairwell
  wall.
- **Chunks are the only spatial partition.** `SdfMeshChunks.Side = 8f`,
  `WholeTriangles = 131_072`, `MostChunks = 64`; every piece's open
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
  distance (`puck mesh audit --world worlds/debug/avatars/moth.puck --prototype moth --cpu --level 2`;
  the evaluator's later correction lowered that floor to about half). The far
  cascade's [high] item
  records the same floor on static content: a chain of two levels "stops at two
  contour cells of error, so a finely contoured mesh ... never coarsens toward
  a far texel", and four collapse levels to 128 cells took the oak only from
  3,052 to 1,440 triangles ([open items](open-items.md#engine-job-system),
  "Other frame-time findings").
- **One contouring, derived caches.** "`SdfSampledLattice` contours a
  GPU-sampled lattice through `SdfDualContouring` over `ISdfContourLattice`;
  never write a second mesher" (the rendering skill's mesh-bake
  reference). A baked mesh is a cache keyed by its geometry
  (`SdfMeshGeometryBuilder`, `SdfMeshGeometry.Hash`) and the bake's code
  identity (`SdfMeshStore.IdentityOf`), derived again on a miss
  ([the derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
- **No mesh-shader or 64-bit-atomic path exists on either backend.** Codex
  read `VulkanShaderCapabilities` and `VulkanLogicalDeviceFactory` and found
  subgroup and draw-parameter capabilities but no `Int64Atomics` or
  `MeshShadingEXT` **[record]**; this memo's search of
  `VulkanShaderCapabilities.cs` for `Int64|Mesh|Subgroup|DrawParameters` found
  only the `DrawParameters` capability lines. An optional mesh or
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
   contour cells by construction (`CellShares`) and the Moth's
   level 2 is 238,235 triangles, so a hundred distinct avatars at
   any distance draw over 23 million triangles in over 17,000 draws, which no
   compaction, bucket or cluster test reduces (Claude **[record]**;
   arithmetic). Codex's own decision rule agrees: "if visible triangles remain
   high because levels stop, deepen the group hierarchy" **[record]**. M1
   found the root queue's GPU time well under produce on the 4070
   ([visibility](visibility.md#what-the-measurements-showed)), so the 4070 is not
   geometry-bound today; the far cascade's [high] item shows where geometry
   does bind (every caster at the coarsest level whose error fits its texel,
   in two layers). The level chain was therefore measured first, and the field
   chain did not deliver it ([D1](#d1-the-unit-of-detail)); clusters still wait
   for a measured geometry-bound draw ([D4](#d4-clusters-inside-a-draw)).
2. **Whether skinned levels come from a member-aware collapse (Argon) or the
   field (Claude).** Both proposed member boxes for the bounds; both predate
   blended skins, which [D2](#d2-bounds-for-skinned-and-multi-slot-draws) now
   covers. On levels, a member-aware collapse inherits the chain's floor and
   the fine mesh's defects, and a field level per member was measured and not
   adopted ([D1](#d1-the-unit-of-detail)). A skinned group has no coarser level
   today; the member-aware collapse is one of the candidates D1 names.
3. **Whether a coarse envelope costs "zero extra GPU dispatches" (Argon).**
   Argon proposed downsampling the final lattice already in CPU memory. The
   bake's coarse lattices are sampled without gradients
   (`SdfMeshPathBake`: `Lattice(gradients: false, group: group,
   longest: CoarseSamples, minimum: CoarseSamples)`) and the final lattice is
   narrowed to the rows near the surface (the mesh-bake reference, "The coarse
   narrowing keeps the rows a thin plate lies between" **[record]** via
   Claude), so a coarse contour needs crossings and gradients at its own
   surface cells. The field-levels audit counted them: a level's contour costs
   about the bake's own lattices.
4. **Whether impostors collide with a "depth-less opaque pipeline" decision
   (Argon).** No such decision exists: `docs/decisions/rendering.md` has no
   `SV_Depth` or early-depth text (a search for
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
   a per-body sight query on the swept reach box, whose evidence lowers a body
   to the slowest nonzero tier and never holds it, with every shadow layer's
   demand read from the receiver test ([D6](#d6-the-hosts-work-per-moved-body)).
9. **The restaging.** Claude: a rigid dynamic draw should read its slot as a
   member does, so a moved body restages slots, not records; Codex: the
   premise of 3,100 is stale and V1's compaction owns the GPU side. Both hold;
   the slot-resolved draw is V2b's design ([D6](#d6-the-hosts-work-per-moved-body)).
10. **The cold-bake bar.** Claude: demand tiers pay only what the spawn views
    resolve, and whether the bar counts on-demand finest tiers is the owner's.
    [The cold-bake bar under demand tiers](#the-cold-bake-bar-under-demand-tiers)
    points at the bar's one definition.

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
`FinerPixels`) and the shadow texel rule are the owner's rulings
([decisions](../decisions/rendering.md#a-raster-pass-draws-only-what-it-can-show)).
The level table holds, per level, its first triangle, vertex count and error
(`SdfMeshRecord.LevelTableWord`), and a counted draw's identity
carries the level above a 24-bit draw index (`SdfKernelInterfaces.MeshLevelShift = 24`
), so the representation addresses up to 256 levels per mesh.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 1a. Field-derived levels: level k is the group's field contoured again by the one contouring on a lattice of cell `c0 * 2^k`, at the iso-value that offsets the surface outward by the level's budget (so a level grows and never retreats), continued until a level is empty; its error measured in world units against the field's surface both ways by bracketed witnesses and raised for every material or normal region the level loses and every part that vanishes ([the error contract](#the-error-contract)), then raised to the finer level's so the kernel's walk stays monotonic; a skinned group's levels contoured per member, each piece riding one slot | A coarse lattice drops sub-cell parts, closes sub-cell holes and merges near parts by itself, which a collapse refuses at locked places; a level is not inherited from level 0, so the nose's defects do not block it; the field answers distance where a simplifier tracks displacement (Bevy's author calls perceptual simplification "very much an unsolved problem", Claude **[record]**); an offset is one iso-value for a field and a hard problem for a mesh | Crossings, contour and answer per surface cell of each coarse lattice (about a third of one level's surface work for the chain's contours, a geometric series; Claude **[record]**, a lead), plus the oracle's bracket walks (several field evaluations per witness) and the reverse hierarchy per level, which the first draft left out of the estimate; a re-contoured level carries its own vertices, a geometric tail on the coarse side; per-member pieces draw interior surfaces where members overlap | The offset closes a deliberate sub-pixel gap at the distance where it is sub-pixel; a level that loses a material stripe or a corrugation's normals is held back until that region is sub-pixel, which is pessimistic for shading detail no geometric level can carry ([watched](#rejected-and-watched), appearance filtering) |
| 1b. A deeper collapse chain (Argon's `CellShares = 8f, NormalDegrees = 45f`, unlocking corners) | Reuses the landed collapse | Cheap to try | The far cascade item tried four levels to 128 cells and the card back made no level past the second; the oak reached 1,440 (open items); topology never changes, so a tree's branches never merge into a crown |
| 1c. A cluster LOD DAG over the merged mesh (Nanite's group-lock-simplify-recluster, meshoptimizer's `clodBuild`) | Proven at scale for dense static meshes; a complete replacement cut with group-coherent hysteresis (Codex **[record]**) | A builder, a BVH, a cut selector and history keyed by group; a deep hierarchy accumulates boundary density under chunk seam locks; QEM is a priority, not an error (Codex **[record]**) | Aggregates ("riddled with holes ... leaves and grass", Nanite 2021, cited by [visibility](visibility.md#rejected-and-watched)); its error is the simplifier's displacement, not the field's distance; it inherits level 0's defects as 1b does |
| 1d. 1a without the offset, dilating only where a level would lose a part, and recording the dilation `d` as that level's error | The field-native analogue of Nanite's aggregate voxels, which switch representation "if doing so would result in lower error than using triangles" (Epic, experimental; Claude **[record]**) | One more contour per level where it applies | Two touching closed groups levelled independently can each retreat under a pixel and open a slit wider than one between them; `d` is an offset in field units, not world units, so for a field that is no true distance the recorded error is wrong by the field's slope (F03: `SdfSampledLattice.PassesBetween`'s own comment names "a scope, a flare, a non-uniform scale" as fields that change "by less, or more, than a step's length over a step"); a dilated opaque crown overstates a sparse canopy's coverage **[hypothesis]** |

#### The error contract

Value over gradient is a first-order root estimate that bounds nothing for a
nonlinear field, and `SdfSampledLattice.PassesBetween` is a boolean
sheet-or-gap heuristic over neighbouring falls, not a distance. The contract
below is what the field-levels audit measures for a level, in world units, so
that the field's scale never enters a recorded error:

- **Mesh to surface.** The witnesses are the level's vertices, triangle
  centroids and edge midpoints, and over any triangle larger than the level's
  cell a grid of points at that cell's spacing. From each witness the oracle
  walks along the field's gradient direction toward the surface in steps no
  longer than the level's budget until the field changes sign, then bisects
  the bracket to the tolerance `τ` (a sixteenth of the group's finest cell).
  The surface passes through the bracket, so the distance from the witness to
  the bracketed point is an upper bound on its distance to the surface by
  construction, whatever the field's slope. A witness that finds no crossing
  within twice the budget refuses the level. The program's Lipschitz factor
  (`SdfProgram`'s Lipschitz analysis) gives `|f| / L` as a certified lower
  bound, printed beside the upper bound as a check on the bracket; the
  first-order estimate is used for nothing.
- **Surface to mesh.** The witnesses are level 0's vertices and a grid over
  its triangles at the level's cell spacing, each bracketed onto the field's
  surface the same way, so level 0's own contour error does not enter. Each
  witness's distance to the level's nearest triangle is the exact
  point-to-triangle distance through the triangle hierarchy
  `SdfMeshIrradianceScene.TryNearestSurface` already walks. A
  witness inside the group's whole field by more than `τ` lies on an interior
  surface (a per-member piece's face inside another member) and is excluded.
- **Enclosure.** A level is contoured at the iso-value that offsets the
  surface outward by its budget, with the iso-value converted from world
  units by the lattice's measured slope (`SdfSampledLattice.SurfaceSlope`
 ) only to aim the contour; the check is geometric: a surface
  witness that lies outside the level's mesh (on the nearest triangle's outer
  side) by more than `τ` refuses the level. A level therefore never retreats
  inside the true surface, so two closed groups that touch or overlap at level
  0 still touch or overlap at every level, whichever levels the kernel picks
  for each. The residual at a joint is overlap, hidden
  inside both solids; a deliberate gap narrower than the budget closes, at
  the distance where it is sub-pixel.
- **Attribute error.** Each surface witness carries level 0's material there;
  its distance to the nearest level triangle *of that material* is its
  material error, as the collapse measures ("within the level's error of a
  triangle of its own material", `SdfMeshCollapse`). A witness
  whose normal differs from the level's interpolated normal at the nearest
  point by more than the collapse's widest angle (`SdfMeshCollapse.NormalDegrees`'
  last entry) is normal-mismatched. Witnesses of one material the level does
  not carry within the budget, and normal-mismatched witnesses, are grouped
  into connected runs over level 0's triangle adjacency, and each run's
  half-diagonal is the error it contributes. A flat sign loses its stripe only
  where the stripe is sub-pixel; a corrugation whose ridges are under the cell
  but whose slopes exceed the angle holds its level back until the whole
  corrugated face is sub-pixel. That pessimism is real and
  is the price of shading detail no geometric level carries; the remedy is
  appearance filtering at coarse levels ([watched](#rejected-and-watched)),
  and the audit prints the three components apart so a reader sees which
  binds.
- **Vanished parts and the terminal level.** A connected component of level 0
  with no level triangle within the budget of any of its witnesses contributes
  its own half-diagonal, not its distance to whatever surface remains (a tree
  beside a wall is as wrong as the tree is tall, not as far as the wall is).
  The chain continues to the first empty lattice, or until the cell exceeds the
  group's extent and the lattice is `FinalMinimum` samples across the box, where
  an empty level is appended; the empty level records the group's half-diagonal
  as its error and zero vertices, so `sdfSelectionLevel` selects it by the same
  walk and `sdfOcclusionEmit` writes its zero-vertex command as it writes any
  rejected candidate's. A group is drawn as nothing exactly
  where its whole extent projects under a pixel; the stand parent (D3) stands
  above a forest's trees long before that.
- **Skinned groups.** A multi-member group's level is contoured per member:
  the member's own shapes, sampled through a lattice whose selector names them
  alone, in the member's slot frame, so every vertex of the piece rides that
  one slot. A rigid transform with the draw's scale preserves distances up to
  `SdfMeshBox.Stretch`, so the error measured at rest between the piece and
  the member's level-0 surface holds in every pose for a vertex that rides one
  member. A recontoured union skinned by the points-round winner has no such
  property: a hand resting on the torso at rest is assigned to the torso and
  left behind when the arm extends, at a recorded error that stays sub-pixel.
  A smooth blend's fillet between two members belongs to neither piece and is
  measured as the pieces' error against the union surface; under a blended
  skin its posed error is bounded by the blend's admitted envelope
  ([D2](#d2-bounds-for-skinned-and-multi-slot-draws)), not by the rest
  measurement.
- **Recorded error.** The largest of the two geometric directions, the material
  runs, the normal runs and the vanished components, raised to the finer
  level's.
- **Where the guarantee stops.** At the witness spacing (the level's cell), the
  bracket tolerance `τ`, and features thinner than level 0's own sampling,
  which no witness of either direction sees. The audit's missing-crossings
  measure (`SdfMeshAudit`, "where the field's zero set has no triangle near
  it", its `MissingCrossings`) runs on every level and is part of
  the acceptance, not a separate lattice the level may ignore.

**Measured, and not adopted.** Option 1a was the favoured design and was
measured offline before anything reached a running World, on the Moth, the
`hall-stairs` union and the `shape-trims` face
(`puck mesh audit --cpu --field-levels`; the readings are in the commits that
ran them). Three experiments, each against the collapse on the same groups:

1. *The field chain in place of the collapse.* At no more triangles a field
   level's recorded error exceeds the collapse's on most groups, because each
   level carries its outward offset and a dual contour tiles faces that the
   planar merge and the collapse keep coarse. Coarse levels of carved and thin
   parts flip, and the coarsest leave surface outside them. The chain does
   reach an empty level where the collapse stops.
2. *Field levels appended below the collapse's coarsest level*, with
   near-coplanar faces flattened and components that would flip vanished
   instead. The Moth's coarsest total falls by about a third, not the order of
   magnitude the design needed. Smooth closed parts gain levels down to the
   empty one; carved and thin parts are refused by enclosure and by witnesses
   that find no crossing within twice the budget.
3. *The same, after the evaluator's Lipschitz bound was corrected at its
   cause.* The correction halved the collapse's own floor (level zero lost
   most of its flips) and moved the field chain little. The same parts block
   it, with the normal component of the error contract dominating the rest.

**The ruling.** The outward-offset contour is rejected as the route past the
collapse's floor. `SdfMeshCollapse` stays the one level source for every
group. The audit mode stays (`SdfMeshFieldLevels`, with
[the error contract](#the-error-contract) as what it measures) because it is
the instrument that judges any later level source. The bake pays for no field
level. Step G2 is parked.

**What reopens it.** A level source that encloses carved and thin parts at
coarse cells and brackets every witness there. The candidates, none measured:
option 1d (a field level without the offset, dilated only where a part would be
lost), a member-aware collapse for skinned groups, and the faster simplifier of
the visibility plan's V3b for static ones. Two findings shape whichever is
tried: the normal component binds the error contract on nearly every level
measured, the collapse's included, so appearance filtering at coarse levels
([watched](#rejected-and-watched)) is what would let any chain go deeper; and
much of the Moth's floor is fine cells spent where no view resolves them, which
[D7](#d7-the-detail-budget)'s demand tiers remove without any coarser level.
The question is taken up again when D7 has been measured on a rebuilt creation
and a place still shows a floor the collapse cannot lower.

That measurement came from the memory side, on the whole debug world: a tier
ladder that ends at each group's unrefined lattice leaves most groups at that
floor and most of the mesh region in it
([the streaming plan's decision 5](streaming-and-memory.md#decision-5-the-unit-is-a-tier-and-the-coarsest-tier-is-the-floor)).
The answer built is D7's, not a level: the ladder continues past the floor as
tiers whose lattices are coarser, each its own product baked on demand, judged
by the half-pixel demand rule and not by the error contract above. It lowers
what a far group holds, not the level a drawn mesh falls to, so this ruling
stands for levels. The outward offset this decision measured is the candidate
for what those coarse tiers still lose, a part thinner than a cell, and option
1d's dilation is how the streaming plan proposes to apply it.

What the visibility plan calls V3c, field-derived levels as a bake experiment,
is this measurement and is closed with it.

### D2. Bounds for skinned and multi-slot draws

**What must be decided.** How a draw whose vertices ride dynamic slots gets a
conservative box the kernel can test, and so a level; the falls' 14 slots of up
to 150,000 triangles each and every avatar draw level 0 in every pass today
([open items](open-items.md#engine-job-system), the far cascade item).

**What exists.** `SdfMeshBox.TryOf` refuses a membered mesh, so the kernel
draws it `unboxed` in every pass at level 0. A vertex rides one member rigidly
or two members through a normalized dual-quaternion blend
(`SdfMeshSkin.Build`, `sdfMeshSkinPose`), so a vertex in a blended seam lies on
neither member's rigid image. The kernel reads the slot table for member
vertices, and the box's stretch is a bound on the matrix's largest singular
value (`SdfMeshBox.Stretch`). [Moving blends](moving-blends.md) admits a blend
only inside a motion envelope computed at compile time: a relative rotation of
at most 60 degrees and a separation `D` of the two sides' motions over the seam
box of at most half the blend's smooth radius; past it the group is split at
that blend and bakes as a crease.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 2a. Member boxes under live transforms: the bake records, per member, the local box of every vertex that member owns first, and the root's over the rest; the draw's box is the union of each member's box carried through its slot's current position and orientation and the draw's scale, then the carry, widened by the group's admitted separation `D` where the group holds a blend; computed where the kernel runs, from the slot table it already binds, or on the host for the rate twin | Exact for a vertex that rides one member. For a blended vertex the two rigid images are at most `D` apart by the envelope's definition, and the blend moves the vertex along the screw motion between them, so it lies within `D` of its first owner's image **[inference]**; `O(members)` per draw, at most `SdfMesh.MaxMembers` | Per-member boxes and the group's `D` in the mesh and the record's members table; the kernel's selection rows become derived from slots rather than staged | A group whose envelope has no finite bound cannot blend at all (it is split), so no draw is left without a margin; a blend rule that changed how a blended vertex travels between its two images would need the inference proven again |
| 2b. Refit from posed vertices each frame | Tightest | A pass over every skinned vertex per frame | Pays per vertex what 2a pays per member |
| 2c. Bounds over a finite animation bank in a dominant bone's space (NanoMesh, Codex **[record]**) | Avoids a refit | Looser; the guarantee ends at the covered animations | Puck's authored motion, IK and effectors are not a bank |

**Favoured.** 2a. **Strongest case against:** the rate decision's host twin
(`SdfMeshCull`, `WorldCreationInstances.Shown`) also needs the box, so the
member boxes must be readable on the host from the frame's packed transforms,
which the dresser already holds.

**Decided.** A membered draw's box is the union of its members' boxes under
their live transforms, widened by the blend envelope the group was admitted
under. The invariant: *every box the kernel tests encloses every position the
vertex resolver can emit for that draw*, enforced at the one door that builds
selection rows (the record writer for rigid draws, the kernel's slot-carried
union for membered ones). It lands in two parts, and `unboxed` is retired only
by the second:

- a draw whose every vertex rides one member takes the exact union at once;
- a draw with a blended vertex stays `unboxed` until its margin is proven: the
  inference above is checked by posing the skin at the envelope's extremes and
  testing every resolved vertex against the box, in the posed audit moving
  blends already runs for its field error, and the margin moving blends
  publishes is the one the box reads. Moving blends owns the skin and its
  envelope; this page consumes them.

The box bounds where a vertex can be, never how far a coarser level's surface
is from the detailed one. A skinned group has no coarser level today, and one
it gains must measure its error posed, against the same envelope.
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
(`SdfMeshPathBake.Land` chooses it for a static, non-glass group
with several instances or past `WholeTriangles`). Static copies share a
prototype's mesh (`WorldRigidAssets`, `SdfMeshInstance`); a static copy's
shapes bake as their union (`SdfMeshPathBake.UnionFinal`); the bake already
answers each vertex's winning slot in a points round and stores
`SdfMesh.Members` and `VertexMembers`.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 3a. Field-contoured parents, chosen by the kernel as alternatives to their children, one parent per render class: the **creation proxy** (the middle tier is D1's per-member pieces at a coarser cell, one draw per slot, so the Moth's 174 draws become its slots' count; the top tier, one rigid draw of the creation's union at rest, is admitted only where the motion program bounds how far any part moves from the root, and records that bound plus its rest error, so a creation with no derived bound stops at its per-member pieces); the **union parent** (a chunked union contoured at a coarse cell as one closed draw replacing every chunk at once, so no parent ever meets a chunk edge); the **stand parent** (a region of the field over many static copies, the far field of a forest). A parent is contoured over children of one render class (opaque with opaque, glass with glass; a displaced group never joins a parent), so a glass dome over an opaque dial yields an opaque parent that keeps the dial and a glass parent that keeps the dome. Every parent triangle names its source child in a per-triangle table beside `TriangleMaterials`, answered by the same points round that answers materials, so a pick on a parent resolves to the child. The kernel draws a parent when its error projects under a pixel, else its children, with the parent's error raised over its children's coarsest so the cut is monotonic; a parent's record names its children's record range so the kernel skips them without a host walk | Every piece exists: unions over instances, the points round, the pixel rule, the render-class partition (`SdfMeshBakeGroup` already refuses to merge a static run with a glass instance or a displacement, and `WorldRigidAssets` emits a mixed creation's glass and opaque shapes as separate surfaces); a parent is a closed replacement and needs no boundary locking, where a cluster DAG locks group borders (Kapoulkine, Claude **[record]**); Codex's own analysis that a per-prototype DAG cannot merge a region **[record]**; the surface pass already names the triangle a pixel shows, which `sdfMeshMaterial(draw, triangleIndex)` reads | A new group kind spanning instances; a kernel rule for alternatives and a record range; a source table per parent and a pick resolver that takes the triangle beside the identity (`SdfDressedPickMap.Resolve` takes the packed identity alone today); a bake cost per parent of one coarse contour per render class; a stand parent is unique geometry derived from its arrangement, so it trades the prototype sharing V1 compacts for fewer draws, and a moved tree re-derives its cell's parents | The one-draw creation proxy needs a derived motion bound (V2c) and is not admitted without one; a stand parent can lose to shared coarse prototypes on bytes, bake work or frame cost at the Deck, which G7 measures and which falls back to the shared path; coverage of a sparse canopy (see 3d) |
| 3b. Authored HLOD (Simplygon, UE World Partition HLOD; Argon **[search summary]**) | Shipped in open worlds | Authoring per cell; a second asset the author owns | Puck authors fields, not proxies; the engine would carry a proxy the field could derive, which "builds it twice" (AGENTS.md) |
| 3c. Octahedral impostors or billboard clouds (Argon **[search summary]**; Epic's Impostor Baker cited by [visibility](visibility.md#rejected-and-watched)) | Cheap per instance | An atlas per species, a capture pass, a parallax shader | A second representation for shading, picking, reprojection and the visibility record; baked colour where Puck shades from a material identity and the bake "point-samples a quad's winning material" (decisions); no occlusion pyramid contribution without depth output |
| 3d. A hybrid mesh-volume far tier measuring filtered occupancy, silhouette and normal/material distributions from the field (Codex **[record]**; Epic's experimental voxel clusters) | Represents partial coverage a surface cannot | A second raster bin and shading model | Unqualified; its visual validity is a capture question; "Never treat an SDF's sign as an adequate opacity filter" (Codex **[record]**) |

**Favoured.** 3a; 3d watched with the measurement that admits it.
**Strongest case against:** a stand parent is one opaque surface, and a
canopy seen from the overlook is partly transparent; where the overlook reads
wrong against the reference at sub-pixel error, 3d is the answer, not a looser
pixel rule. And a stand parent is unique geometry: thousands of irregularly
placed trees with real gaps between them make a parent of many disconnected
components that keeps much of the trees' triangles while duplicating each
species' coarse geometry across every region and parent level, where the
shared prototype path draws one coarse mesh many times through V1's buckets.
Fewer draws is not the bar; the dense-landscape plan's L5 closes "frame,
memory, bake and edit bars", and the far field must pass them.

**Decided.** The hierarchy above a draw is contoured from the field: creation
proxy, union parent, stand parent, each a closed replacement the kernel chooses
by the one-pixel rule with monotonic error. Three invariants, each enforced at
one door: *a parent and its children are never both drawn in one pass*, in the
kernel's one selection step, which decides a parent's record and its children's
range together; *a parent replaces children of one render class and never
hides a surface of another class*, in the bake's grouping,
which forms a parent over one class's children as it forms a static run today;
and *every parent triangle names the child it came from*, in
the parent's own mesh (its source table), so a pick resolves to the child
whether or not the child's product is resident, and a seam triangle names the
child the points round's winner at its centroid belongs to. The far field is
admitted by cost as well as by look: G7 compares the stand
parent against the shared prototype path at D1's coarsest levels under V1's
compaction, on resident bytes (parents and children together, under the
library's one budget), cold and incremental derivation (one moved tree), per
pass classification work and raster vertices, and frame cost on the Deck's
leg; where the parent loses, the far field is the shared path, and a parent
over a *repeated* arrangement (the dense landscape's authored clusters) is
shared like a prototype, a Puck-native assembly hierarchy that keeps sharing
where arrangements repeat. The creation proxy's one-draw tier rests on V2c's
derived motion bound and is never admitted on a measured reach alone, which
is a lower bound on motion, not a bound. Static far-field marching stays
rejected (the frame "does not interpret SDF programs",
[decisions](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)):
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
| 4a. Flat clusters over the already chosen level, culled by a compute step that writes compact lists on V1's path (triangle references for vertex pulling, or fixed-size cluster instances), behind a comparison lever; per-draw level choice kept; cluster bounds and cones from the contour's own cells and crossing normals | Codex's first implementation experiment **[record]**; Bevy 0.14 shipped virtual geometry without mesh shaders by appending triangle references (Codex **[record]**); Puck already vertex-pulls | A cluster product in the bake, cluster metadata (at 64 bytes a cluster, a million clusters is 64 MB before levels; Codex **[record]**), a second classification per view | Not automatically a win for tiny instanced parts, where metadata and tests can exceed the raster removed (Codex **[record]**); turns a hidden-wall saving into GPU work that M1 says is not the bottleneck on the 4070 |
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
proven, and never copied into the shadow exit or optical passes. The first
draft said normal cones were worth measuring first on "the cascades, which
have no occlusion of their own" (Claude **[record]**); that premise is false
at this revision: the far cascade rejects casters whose
receivers are hidden (`sdfReceiverMayShadow`) and runs the two-phase entry
test against its own pyramid (`sdfOcclusionMaySee` in the late step), both
 in `sdf-mesh-occlusion.comp.hlsl`, and its exit and optical
layers are selected by box and receiver alone by design. The population a
cone could thin is the entry layer of the near and second cascades, which
have box-only selection; the exit layer draws faces turned from the light and
would lose what it exists to hold. Rests on "fundamentals, driven by features" (a feature
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
0u, c.w, 0u); }`).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 5a. Keep the visible-set approach; reconcile previous visibility onto the current cut by stable identity (a parent inherits its children's visibility and a child its parent's, conservatively), unmatched incarnations go to the late phase | Keeps Puck's approach, no previous pyramid, no reprojection holes (the visibility plan rejected the previous-frame pyramid for this reason) | After a coarse inheritance the early phase may draw more for a frame | Nothing silent: the late phase covers every rejected current candidate |
| 5b. Test the current cut against the previous frame's pyramid with the previous camera, retest rejections against the current pyramid (Nanite's approach, NVIDIA's reject-list variant; Codex **[record]**) | The established approach when streaming invalidates explicit sets | A previous pyramid per view, reject queues | Reprojection holes a camera cut exposes; a change of approach for a problem 5a handles |

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
(`SdfMeshDresser`); the record has a `FrameSlotWord`
(`SdfMeshRecord`); the rate decision keeps a body posing while any
frustum or cascade keeps its sphere, without occlusion
(`WorldCreationInstances.Shown`); the kernel's visible state is
per draw per pass and is read back for session views (`SdfPortalSight`).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 6a. Rigid dynamic draws read their slot: a draw riding a slot resolves its vertices through the slot's live and previous transforms as a member does, and the kernel moves a slot-frame rest box by that transform; a moved body restages its slots' transform rows, not its groups' records (the Moth's 174 records become 42 slots) | The mesh pass already binds the slot table and reads live and previous transforms for members (`sdfMeshWorldPosition`); V2b's "a moved slot updates its instances' transforms in place" with the transform's one home on the GPU (Claude **[record]**) | A shader change in the three vertex resolvers; the selection rows derived from slots (D2's door); picking and reprojection read the slot | Transform rows already upload by change: `SdfWorldTables.FramePreparation` packs "the rows the frame's moved set owes" and `WriteDynamicTransform` owes a row only when the region's `Write` reports it changed, so the first draft's doubt is closed from the source (the review confirmed the same). Still unverified: the carry, scale and clip words of a body straddling a portal **[record]** (Claude); the straddle adds instances carrying the other clip plane under V1, which keeps the slot shared |
| 6b. Occlusion as evidence that a body is shown nowhere, read per body, not per draw: each rated body submits one query per view, its reach box (`RateReach`: the rest reach widened by `MotionReach`, the farthest any pack has posed a part) swept by its part speed over the readback latency, through the same sight step `SdfPortalSight` dispatches today, and a cascade's evidence is its receiver test on that box; a body no view's query sees, no cascade's receivers keep and no consumer read (`PoseReadPack`) takes the slowest nonzero body tier (`SdfUpdateRateTiers.Tiers` ends at 5), never `HeldHertz` | The sight step is exactly this test for portal slabs and already has the completion seam and the stale-result policy ("Unknown, changed and retired observations always keep demand"); the reach box is already what the frustum test rates by (`BodyPixelsPerMetre` over "the sphere it reaches"); the tier floor bounds how stale a hidden body's pose can be to the tier's period plus the latency, where a zero-Hz hold is unbounded and wakes with a continuity cut (`WorldCreationInstances.Rates`: a held body's "next full pack is a continuity cut, as a teleport's is") | A growable query list in the sight step (its capacity is `MaxScreenSurfaces` today), a receiver-box query in the cascade's kernel, and one word per body per view read back on slot completion | Codex's caution: a camera-hidden body may still contribute to a cascade's exit or optical layer, which the kernel emits from `kept` alone ("Entry visibility (was/visible) must never remove a potential second surface"), so the first draft's "receiver and entry visibility" was wrong evidence; receiver-kept is the right evidence for every layer. The hold's staleness: the pyramid is a completed earlier frame's, so a result is applied only to the camera it was captured for, a missing or stale result keeps demand, and the swept reach box, not the held pose's tight member boxes, is what the pyramid tests, so a limb that would swing out of cover makes its box visible before it does |
| 6c. Leave posing to frustum evidence; let the job system hold unseen work | The job system's first value is "holds unseen work" ([plans](README.md)) | None here | "Unseen" needs the evidence 6b supplies; without it the scheduler holds nothing behind a wall |

**Favoured.** 6a and 6b. **Decided.** A rigid dynamic draw reads its slot, and
occlusion joins frustum and cascade evidence in the one rate decision, as a
tier floor rather than a hold. The invariant for 6b: *a body rated below its
frustum tier by occlusion is one whose swept reach box no view's completed
query saw, no cascade's receivers kept and no consumer read, and it is rated
at the slowest nonzero tier, never held*, enforced in `RateOf`, the one door
every rated body passes, which reads sight results only through
`SdfPortalSight`'s camera-or-reach and epoch match. What the two-pixel rule does and
does not cover: the tiers bound an update's step by part
speed for a body something shows; an occluded body's pose is at most one
tier period plus the readback latency stale when it reappears, and that first
frame shows a pose that old, as a camera cut shows a held body's held pose
for one frame today. The latency is counted in G5 (frames between a query's
submission and its result), and `HeldHertz`, whose wake is a continuity cut,
stays reserved for the evidence that already earns it (no frustum, no
cascade, no consumer). Rests on the update-rate ruling, on `SdfPortalSight`'s
existing stale-result policy, and on V2b. The frame-bar packet's CPU item owns
6b's landing.

### D7. The detail budget

**What must be decided.** What bounds the bake's detail: with authored geometry
alone deciding the finest cell (one rule for every shape, `WorldShapeDetail.Of`,
its round sections at `SdfSampledLattice.RoundCell`, floored at
`WorldShapeDetail.FinestDetail`), the nose bakes at its own spacing
whether a view can resolve it, and nothing reads the screen. Built as
[bake detail bounded by demand](bake-demand-tiers.md): `SdfMeshTier`, the tier
fact in the key, `SdfMeshPathBake.Retier` and the `world.residency` columns.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 7a. Demand-bounded tiers: a group's tier is the coarsest power of two at which its finest cell stays under half a display pixel at the nearest demand seen (`SdfMeshDemand` holds each view's eye and pixels a metre at one metre); the tier joins the geometry key as a fact (`SdfMeshGeometryBuilder.AppendFact`), so each tier is its own cache entry, prewarmable in a player's build; the frame draws the finest tier baked so far, a counter reports detail owed, and the demand-first queue bakes the next tier; the D1 chain hangs under each tier | Virtual texturing applied to geometry; the bake pays what views resolve; no finest-cell ceiling for a mirror-close view (Claude **[record]**: the nose's cell is a sixth of a pixel at the chase camera and over three pixels at 0.3 m on 2160 lines, arithmetic) | A tier per group in the key; a residency counter; the bake queue reads demand it already has | On-demand tiers sit outside the cold-bake bar ([the bar under demand tiers](#the-cold-bake-bar-under-demand-tiers)); a view outrunning the bake sees a coarser tier for a moment, reported, never refused |
| 7b. A per-creation triangle or byte cap | Simple | A refusal by count | Contradicts "no scene ceiling is sized for a worst case ... Detail follows projected size (the one-pixel rule), never a body's index" (decisions) |
| 7c. Spatial refinement: an octree of field-contoured nodes each at its depth's cell, nodes of one depth partitioning one lattice | Refines the nose without baking the back of the head | Seams between depths need a rule of their own, as source tiles have ([the seam rule](streaming-and-memory.md#the-source-tile-and-its-seam-rule)), or a parent above | Later; the group tier needs no seam work and comes first |

**Favoured.** 7a, then 7c. **Decided.** Detail is budgeted by demand, never by
a cap: the finest tier is a cache tier identified from its inputs (group
geometry, tier), baked on demand and reported as owed. The invariant: *what the
frame draws is always a complete tier of the group*, enforced by the residency's
publication door (a tier publishes whole or not at all). That invariant keeps
holes and dangling addresses out; it bounds no debt, so the
debt has its own contract: the *owed error* of a group is its resident tier's
cell in display pixels at the nearest demanding view, and the *owed interval*
is the frames from the demand to the tier's publication; `world.residency`
reports both per group and their worst over a window, and the admitted
degradation is one tier below demand (two cells per pixel) for at most the
landing budget's frames, which G8 measures on a cut, an approach and a retreat,
and with a portal's view demanding at once. Demand is read ahead: each view's
eye is advanced by the camera program's bound over the landing interval (V2c's
derived bound where the camera is authored, the measured speed otherwise), and
a visible portal's view demands as the root does, so a tier is usually resident
before the view resolves it. A demand that leaves a view's reach before it
lands is cancelled, a tier is demanded only after its resolution has held for
a few frames (no thrash at a boundary), and the in-flight set (bakes running,
landings staged, old and new products live together) is bounded by the
library's one byte budget beside the cache claims, never by a second account.
The budget surface is `world.budget` per creation: triangles per level and
tier, bytes resident, tiers owed, owed error and interval, beside
`puck creation stats`. Rests on the derived-product ruling and the no-ceiling
decision.

### D8. Shadows

**What must be decided.** How casters are selected and levelled per cascade,
and whether shadow storage is virtualized.

**What exists.** The far cascade uses the camera-depth receiver mask and
two-phase light-space entry depth through the same kernel; exit and optical
lists never depend on entry visibility; a cascade takes the coarsest level
whose error is within one of its texels (`SdfMeshDetail.Of(cascade, texels)`);
the filter reach is `SdfMeshCull.ShadowFilterTexels = 18f`; with
box-only selection the far cascade draws nearly every caster at the chain's
coarsest level in two layers ([open items](open-items.md#engine-job-system),
the [high] far-cascade item).

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 8a. The D1 chain and D3 parents under the existing texel rule, per cascade; D2 boxes let membered casters cull; receiver and entry culling extended to other cascades where measured (V5) | The far cascade's limit is the chain's stop at two cells (open items); every cascade already selects by texel | None beyond D1 to D3 | Nothing new |
| 8b. Cluster or node caster rejection against receiver pages (Codex **[record]**) | Tighter than draw boxes | D4's product | Waits for D4 |
| 8c. Virtual shadow maps: page tables, dirty masks, caching, all three layers (depth, exit, optical) paged consistently | Cache hits avoid redraw for static content (Epic; Codex **[record]**); Timberdoodle's resolution-matched VSM (Codex **[record]**, non-peer-reviewed) | Pools, page tables, invalidation keyed by light, geometry incarnation, level and layer; a moving sun and wind erase the cache (Codex **[record]**); a cached 2048² D32 layer is 16 MiB a cascade ([visibility](visibility.md#v5-multi-view-and-shadow-work)) | The day program and the meadow's wind, which the debug world runs by default |

**Favoured.** 8a now, 8b with D4, 8c rejected for now. **Decided.** Shadow
levels come from a group's levels by the texel rule; caster culling tightens
with D2 and D4; no shadow cache is built while the sun moves and the meadow
blows. Caching and sparse paging are two benefits with two admissions: a cache
pays only where static redraw with unchanged
projections dominates a cascade ([visibility V5](visibility.md#v5-multi-view-and-shadow-work)),
with a page identity that includes light projection, geometry and deformation
incarnation, level and layer (Codex **[record]**), and the day program and
wind refuse it today; a page pool that allocates and rasterizes only the
texels receivers demand pays wherever demand is sparse, whether or not every
page is dirty, and a moving sun does not refuse it. The demand is every view's
covered camera samples reprojected into light-space occupancy cells, a mask the
per-view far cascade once computed (`sdf-caster-receivers.hlsli` in git
history) before the world's shared, cached entries (`SdfWorldShadows`)
replaced it. The admission measure
for a paging experiment is the occupied share of each cascade's texels, read
from that mask under the moving sun, the meadow's wind, a narrow portal view
and a sparse-receiver view (the lead's measurement window, no build); a far
cascade whose receivers occupy a small share of its map in representative
views briefs the experiment, which pages all three layers together with the
filter footprint's margin (eighteen texels, the widest search disk and the normal offset) and is judged by
texels written and bytes against the full-layer cascade. Until that measure is
read, 8c stays rejected. The transmission plan's identity peel is unaffected.

### D9. Streaming

**What must be decided.** How detail that is not resident arrives: tiers,
levels and parents under a byte budget, across views and portals.

**What exists.** Input-derived identities (`SdfMeshStore`), a library with an
age-ordered byte budget (`SdfMeshLibrary`), a region arena with
keyed placement (`SdfMeshRegionArena`), background landing with
completion-gated publication, near-first demand (`SdfMeshDemand`), and the
[residency decision](../decisions/residency.md)'s rule that only presentation
follows the observer.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 9a. Tiers, levels and parents as entries of the existing store, library and arena, with demand from every view and cascade that renders (a visible portal demands detail the root camera does not), coarse coverage resident while finer tiers land, eviction establishing a coarser complete tier first | Codex's residency invariant and NVIDIA's streaming design (groups as the topology-safe unit, pages as the I/O unit, install before publish; **[record]**) map onto homes Puck has; "Do not add a second cache with manually versioned payload validity" (Codex **[record]**) | Group and tier addressing in the library and arena; request priority by display error | A camera pose must never enter a geometry's identity because that camera requested detail (Codex **[record]**): the tier is a fact about resolution, not about a view |
| 9b. A separate cluster-page streamer | Purpose-built | A second cache and a second lifetime | Rule 7 |

**Favoured.** 9a. **Decided.** Streaming is the residency's existing lifecycle
extended to tiers and parents. The invariant (Codex's, adopted): *every
GPU-visible address and every selected cut names a fully installed product and
stays valid until every reader finishes*, enforced by the residency's
publication and fence-lifetime door extended to tiers; GPU feedback is
asynchronous, bounded, deduplicated and prioritized by display error, and the
host never waits on it. Arrival quality is D7's contract (owed error and
interval, lookahead, cancellation, one in-flight budget), measured by G8, since
the address-lifetime invariant here says nothing about latency. A tier's
identity stays input-only: the camera that demanded it
never enters the key. Rests on the residency decision and the host-primitives
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
| 10a. Baseline: compute instance, node and cluster selection; ordinary vertex and fragment raster with the depth attachment and the existing visibility resolve; bounded GPU queues; compaction and buckets; 32-bit synchronization | Every bar device runs it; the lowest-risk path under Proton (Codex **[record]**); Bevy proved the concept without mesh shaders; NanoMesh rasterizes into a 32-bit visibility buffer on mobile (Codex **[record]**) | None new | Nothing on the bar devices |
| 10b. Mesh and amplification shaders as a measured output adapter over the same cluster product | Turing and Ada support them; RADV enables `VK_EXT_mesh_shader` on GFX10.3+ (Codex **[record]**, Mesa source opened), but the Deck's installed stack and vkd3d-proton's tier mapping are unqualified | Backend feature discovery, enablement, pipeline admission and recorder support on both backends; vendor tuning disagrees (64v/126t Turing, 128v/256t AMD; Codex **[record]**) | A task shader that launches almost no surviving work loses to the compute list (AMD; Codex **[record]**) |
| 10c. Compute micropolygon raster with a packed 64-bit depth-and-id atomic | Nanite, Bevy 0.15 | Int64 image or buffer atomics queried and enabled per backend; a second rasterizer reproducing portal clips, stamp fade, skin, displacement, winding, identity and shadow layers (Codex **[record]**) | Unmeasured on any bar device; "Never emulate a 64-bit depth+ID update as two unrelated 32-bit atomics" (Codex **[record]**) |
| 10d. Work graphs | 4070 only ([visibility](visibility.md#rejected-and-watched)) | A second implementation | The 2060 and the Deck |

**Favoured.** 10a as the baseline; 10b, 10c and 10d as enhancements that read
the same data, give the same result within the still tolerance, are measured to
pay on their hardware and fall back automatically. **Decided.** Nothing in D1
to D9 depends on a capability the 2060, the 4070 and the Deck do not all have;
every enhancement is a second implementation that earns its place by the
upper-hardware ruling (AGENTS.md, "Upper hardware enhances"), and each begins with backend capability discovery and
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
  simplification unsolved. Puck's one contouring does both jobs at
  once.
- **Derived products identified from inputs make the finest tier a cache.**
  Virtual texturing's idea applied to geometry (D7): a tier is a fact in the
  geometry key, so it is baked on demand, cached, prewarmed in a player's build
  and never recorded as truth. A triangle asset's finest level is its import.
- **A skin with an admitted envelope makes bounds cheap.** A vertex that rides
  one slot is inside its member's box under the live transform exactly, and a
  blended vertex is inside it widened by the envelope the blend was admitted
  under (D2). The bound comes from the document, not from a refit.
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
| Outward-offset field levels | Rejected after three offline experiments (D1) | No order-of-magnitude fall in the floor; carved and thin parts fail enclosure and bracketing | A level source that encloses those parts at coarse cells |
| Deeper collapse chain, member-aware collapse | A deeper chain is rejected; the member-aware collapse is a candidate D1 names | The collapse stops at locked places and two cells (`CellShares`; the card back and the oak, open items) | D7 measured on a rebuilt creation still showing a floor the collapse cannot lower |
| Nanite-style cluster LOD DAG | Watch (was watched in [visibility](visibility.md#rejected-and-watched)). The [state-of-the-art audit](state-of-the-art/audit.md#4-level-of-detail-is-two-discrete-levels) found levels a generation behind and proposed a Puck-native DAG: coarse nodes re-contoured from the exact field (`SdfMeshPathBake.ContourLevel` does this offline), certified nodes, and spatially varying tiers. | Aggregates, a second builder beside the contouring, displacement as error; D1's chain reaches the same end from the field. Today's two levels (`SdfMeshCollapse.CellShares`) switch whole per draw at about a pixel, with no morphing, none for skinned draws, fixed chunk seams and one tier per group, so a draw partly in view cannot show a fine level near the eye and a coarse one far away | D4's experiment showing visible triangles high because a draw's levels stop while it is partly in view; or a draw measured to need spatially varying tiers (a terrain sector or a union chunk seen along its length). Then the DAG's nodes are D3's parents and D1's levels contoured per region, so it extends those steps and does not add a second builder |
| Meshlets inside a draw (flat clusters) | Watch, first experiment written (D4) | No bar device is shown geometry-bound; tiny instanced parts can cost more in metadata than they save | A large partly visible draw measured geometry-bound on the 2060 or at the Deck's resolution after D1 to D3, with V1 landed |
| Normal-cone culling | Watch | Nanite found it not worthwhile after occlusion; meshoptimizer and NanoMesh keep it (Codex **[record]**) | Clusters adopted; the first place to measure is the near and second cascades' entry layers, which have box-only selection (the far cascade already rejects by receivers and entry depth, and the exit and optical layers must keep faces turned from the light; F14) |
| Appearance filtering at coarse levels (normal variance into roughness, material coverage into a blend) | Watch | D1's attribute error holds a level back until a region of lost normals or material is sub-pixel, which is correct and pessimistic for shading detail no geometry carries | G1 showing the normal or material component binding the chain on real content (a corrugated or striped face held at level 0 past where its geometry allows), which briefs a filtered-attribute level whose shading, not its geometry, carries the detail |
| Mesh and amplification shaders | Enhancement, not baseline (D10) | No enablement on either backend; Deck Proton leg unqualified | A measured win over the compute list on each device, with automatic fallback |
| Compute micropolygon raster, 64-bit atomics | Enhancement, last (D10) | A second rasterizer; unmeasured on any bar device | Residual fine triangles shown to dominate setup after D1 to D4 and V4 |
| Work graphs | Enhancement on the 4070 only | [visibility](visibility.md#rejected-and-watched) | Unchanged |
| Shadow cascade caching | Rejected for now (D8) | The day program and wind erase the cache; three layers to page consistently | Static redraw with unchanged projections measured dominating a cascade |
| Sparse shadow pages without caching | Rejected until measured (D8) | Page tables, pools and indirection for all three layers against an unmeasured saving | The receiver mask's occupied share of a cascade's texels measured small under the moving sun, wind, a portal view and sparse receivers |
| Octahedral impostors, billboard clouds | Rejected (D3) | A second representation for shading, picking and reprojection; baked colour against material identity shading | The stand parent and the shared prototype path both failing the dense landscape's L5 bars on the Deck, and the hybrid far tier failing them too |
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
lead's settle windows, never by a lane under load. The steps run on
Direct3D 12 with a windowed Vulkan leg (RTX 4070 first; RTX 2060 and Steam
Deck legs run once the RTX 4070 meets both bars). Steps in the
visibility plan keep their names there; this order interleaves with V1 and V2
as noted.

**One error budget along one chain of meshes.** Today tiers (`SdfMeshTier.PixelShare`
= 0.5) and collapse levels (`SdfMeshDetail.CoarserPixels` = 1,
`SdfMeshCollapse.CellShares` at 0.25 and 2 cells) disagree and measure screen
size differently: the tier rule uses straight-line distance over the focal
length (`SdfMeshDemand.PixelAt`), the level rule depth along the view direction
stretched for the view's corners (`SdfMeshDetail.Of`). Four consequences
follow. The finest mesh is almost never drawn: at the tier it asked for, a
group's first coarser level always qualifies and its second about half the
time, so level 0 is drawn only for boxes touching the eye plane, skinned and
glass draws, and groups already at their finest tier. It still occupies memory:
level 0's index and material data are about 1.1 GB of a 3 GB mesh region at 71
million triangles (reasoned), and building the other levels takes 28.7% of the
bake's CPU time. One demanding copy sets the detail of every copy: a shared
prototype is baked once, at its largest placement scale (`WorldRigidAssets`)
and at the finest tier any copy asks for from any view (`CopiesPixel` in
`SdfMeshPathBake`), so one fern beside the player sets the detail of every
fern. And nothing stands in far away: the levels stop at a two-cell error.
Unify them around one error budget (display pixels for views, texels for shadow
maps; the recorded one-pixel and one-texel rulings stay the engine defaults and
authored quality tables may choose others, the one look value whose default is
a ruling and not a neutral value) in five steps:

1. **Count drawn triangles per level and tier, and unselected-level bytes.**
   Built by the first wave's `floor` lane, because they size a floor and a
   detail tier: one stage in eight of each raster pass marks the draws it
   selects with their level (`SdfWorldTables.ObserveSelectionMarks`), and
   `world.residency`'s selection line prints the triangles each pass draws by
   level and by tier and the placed mesh bytes no pass selected. Its first
   readings are owed to a GPU leg. Two records disagree on what the levels save
   (the mesh-bake reference and the handbook say 11% and 6% of the stored
   triangles; a later cold bake read 29% and 19%), and the counter settles it.
2. **Unify screen-size projection in one function.** Replace all four separate
   screen-size calculations (`SdfMeshDemand.PixelAt`, `SdfMeshDetail.Of`,
   `WorldCreationInstances`, `WorldSeatViewports.PixelsPerMetre`) with one
   function using straight-line distance so turning in place never shifts
   detail; pass the scale to shaders as data. Then either re-pitch tiers and
   levels against each other or store level-0 data so it can be evicted once
   lighting stops tracing it ([illumination](illumination.md#after-the-gpu-trace)).
3. **Audit every tier offline** (`puck mesh audit --cpu --tier`) before
   building the chain.
4. **Build one chain of rungs per shape.** Hold the requested tier's levels,
   the streaming floor, and tiers other copies ask for so each copy and cascade
   selects the coarsest qualified rung for its budget.
   - *Recorded deviation over authored bounds.* Every rung records its
     bidirectional deviation from the authored field via
     `SdfMeshFieldLevels.Measure` (covering lost parts, materials and normals)
     sampled over the authored group's own bounds, stored under the rung's mesh
     identity. Never qualify a rung with `SdfMeshAudit`, whose grid starts from
     the output mesh's box: if one of two parts vanishes the box shrinks to the
     part left and the check never samples where the other was, so the audit
     stays a diagnostic. The measure samples, so a feature thinner than its
     sampling, or two surface crossings between two samples of the same sign,
     can be missed; a group with such features is marked not qualified so
     consumers keep the finer rung. Every rung is measured against the one
     reference because the errors of two tiers cannot be put in order without
     measuring both: a collapse's error only grows along one contour's
     simplifications (`SdfMeshCollapse.Levels`), a level can be missing, a
     tier contoured afresh starts from different geometry, and the floor's
     acceptance test certifies coverage, not deviation.
   - *Stepping down and switching.* Step down only when recorded deviation fits
     the consumer's budget, budgeting switches on the sum of both rungs'
     deviations. A coverage-only floor draws only for missing meshes and is
     never stepped down to by choice. Rungs coarser than the floor wait for
     appearance filtering (normal variance into roughness, lost materials into
     a blend). Cap the bake-time audit cost: if recording deviations adds more
     to the cold bake than chain draws save, stop the chain at the floor.
   - *Checks:* walking up to one tree changes no distant tree's triangles; a
     large distant copy and a small nearby copy each draw their own rung; thin
     and offset contours hold across tier changes in camera and shadow views;
     a rung exceeding budget is never taken; a two-part group losing a part at
     a coarse rung is marked not qualified.
5. **Thin ground cover with distance** (after [visibility V1b](visibility.md#the-build-order)).
   Give each placed plant a stable rank derived from its occurrence identity
   ([dense landscapes](dense-landscapes.md)) and evaluate an authored curve row
   (`WorldGaitDrivers.CurveHeight`; default keeps everything) to drop whole
   plants cleanly (never fading pixels), while the ground's shading program
   covers lost density. Check: no popping in a presented walk, in the camera's
   view or in shadows.

The floor then does three jobs: it shows when a mesh has not arrived, it is
what a far cascade draws, and it is where a distant copy ends up; the lighting
reads it only where [illumination's lighting check](illumination.md#after-the-gpu-trace)
passes. Nanite keeps its coarsest page resident for the same reason, and the
debug world's coarsest tier has about a twentieth of its finest's triangles
(measured once), so distant draws save much. Puck has an advantage a mesh-only
engine lacks: every rung is checked against the authored field, so no
separately authored stand-in is needed. Keeping several tiers costs bytes from
the mesh library's one budget, a rung that fails its audit keeps its group at a
finer tier than the chain promised, re-pitching the rules changes the look,
and the full mesh cannot leave memory before the lighting stops tracing it.
Still to find out: how many triangles each level and tier draws, whether
recording a rung's deviation costs too much bake time, and whether switching
tiers per copy pops on thin parts in motion
([what would change the answer](rendering-research/step-designs.md#what-would-change-the-answer)).
Options not taken: keeping both rules; more collapse levels, already measured
and rejected (four levels down to 128 cells took an oak only from 3,052
triangles to 1,440, and three field-built experiments are [D1](#d1-the-unit-of-detail)'s),
so the far shadow map gets a coarser tier instead; one mesh over many copies
(D3, G7) and clusters inside a draw (D4, G9) stay measured exceptions against
the chain. Not checked: the other two places that compute size on screen (a
reviewer's reading); that Nanite selects a cut per instance (the source tests
parent against child); Dreams' remark that thinning per pixel breaks temporal
history, which is why thinning removes whole plants.

| Step | Lands | Check on `worlds/debug` | Success line |
|---|---|---|---|
| G2. A level source past the collapse's floor (parked) | Nothing is scheduled. [D1](#d1-the-unit-of-detail) holds what was measured and what reopens it | `puck mesh audit --cpu --field-levels` on the candidate's levels for the Moth, `hall-stairs` and `shape-trims`, against the collapse | A candidate is adopted only when its kept levels lower a creation's coarsest total by an order of magnitude from the collapse's at no worse a recorded error, with no open edge, flip or missing crossing beyond level zero's and no surface outside a level |
| G3a. Member boxes for rigidly skinned draws | Per-member boxes in the mesh and record; the kernel's union under live slots; the host twin for the rate decision. A draw with a blended vertex stays `unboxed` | Camera facing away from the falls and the garden crowd: rigid membered draws' vertices in `world.counters gpu` fall to zero when off-frustum or occluded and return when the camera turns; `world.view.pick` on a bent limb still names it; a cascade still shadows a hidden body onto a visible receiver | No rigid membered draw drawn in a pass that cannot show it; no missed surface in a presented burst of a walking creation and a portal straddle |
| G3b. Blended draws boxed, and `unboxed` retired | The blend margin from [moving blends](moving-blends.md)' admitted envelope in the mesh and record; the union widened by it; `unboxed` deleted | The posed audit at the envelope's extremes tests every resolved vertex of each blended group against its box and prints the largest excursion beside the margin; then G3a's views on a blended creation | No resolved vertex outside its box at any admitted pose; no blended draw drawn where it cannot show, and none missing where it can |
| G4. Slot-resolved rigid draws (V2b's design) | A rigid dynamic draw reads its slot's live and previous transforms; selection rows derived from the slot's rest box | `dotnet-trace` of the frame thread at flight B: `sdf.instances records.changed` falls from about two thousand to the moved slots' count; `world.debug-view motion` still on a moving body; a straddling body clipped on both sides at `debug.go portals` | Records restaged per moved body equal its slots, not its groups |
| G5. Occlusion-aware rate floor (the frame-bar CPU item) | The sight step's query list grown past `MaxScreenSurfaces` and fed one swept reach box per rated body per view; a receiver-box query in the cascade kernel; `RateOf` rating a body no query saw, no receiver kept and no consumer read at the slowest nonzero tier | Flight B facing the motion gallery through the stairwell wall: `world.rates` lists the exhibits at the floor tier, with the query latency in frames printed beside; stepping into the gallery in a presented burst; a body behind the wall whose shadow falls on the lit stair, and one whose glass or back surface feeds the far cascade's exit or optical layer, still posing at its shadow's rate; a camera cut onto a hidden body | Bodies behind the wall at the floor tier; no exhibit's presented pose older than one floor period plus the counted latency; every body a cascade's receivers keep rated as shown; a cut keeps demand until the first result for the new camera |
| G6. Creation proxy and union parent | The alternatives rule in the kernel (a parent's record names its children's range), per-member pieces as the proxy's middle tier, the one-draw tier only where V2c's bound exists, parents per render class with a per-triangle source table, `SdfDressedPickMap.Resolve` taking the triangle, union parents above chunked unions | The Moth at 50 and 100 m draws its slots' count of records, and one per render class where its motion bound is derived; `world.counters gpu` draws and vertices at the garden crowd from the lawn; the hall's unions from the forest pose at (47.80, 0.02, -116.84); a glass-over-opaque creation (the lift's cab, or an exhibit the lane chooses) captured at parent distance against its children; `world.view.pick` on a parent naming the same child as a pick on the children at the same pixel; a parent and its children never both drawn (a count per pass) | Draws per distant creation fall to its slots, and to one per render class where admitted; the hall's far draws fall to its parents; no surface of one class lost behind a parent of another; picks agree; captures meet the reference |
| G7. Stand parents for the forest (dense landscapes L4's far field; measured exception after the chain) | A bake group over many static copies contoured as one region per render class, with a source table; compared against the chain of rungs above and the shared prototype path under V1's buckets (adopted only as a measured exception for repeated arrangements, never ahead of the chain) | `view.override layout three-quarter` then `view.override camera forest-overview`, and `body.pose spawn:forest 0` looking south: draws, vertices, candidates and cascade vertices against the shared path's; resident bytes of parents and children together (`world.residency`); cold derivation of the belt's parents and the re-derivation one moved tree costs; captures from the overlook and the road against the reference; the nested forest view's own counts; the frame cost on the Deck's leg in the lead's window | The overlook's far belt draws as parents at sub-pixel error and reads as a forest, and the parents beat the shared path on bytes, derivation and frame cost; where they read wrong the hybrid far tier is briefed, where they cost more the far field is the shared path (with parents shared over repeated arrangements), and the pixel rule is never loosened |
| G8. Demand tiers' arrival contract | The tier, its key and the report are built ([D7](#d7-the-detail-budget)). What remains is arrival: a demand read ahead along the camera's bound, cancellation of a demand that leaves a view's reach, and the one in-flight budget. [The streaming plan](streaming-and-memory.md#the-build-order) owns all three with its prediction and reservation decisions; this row is its check on a creation | Spawn in the studio and walk to the Moth's face at boom 1.5 m, retreat, and cut back; open a portal onto a second demanding view: owed error and interval per group over the walk, peak resident and in-flight bytes | Owed error never worse than one tier below demand for longer than the landing budget's frames on any of the four runs; peak bytes under the budget; no view ever refused, only owed |
| G9. Clusters inside a draw (D4's experiment) | Flat clusters of the chosen level on V1's buckets, behind a lever, admitted only on a cascade-pass measurement on a bar device where published cluster gains are largest | The terrain sector, hall shell and cascade passes on a bar device: candidates, survivors, pyramid tests, rejected triangles, reference writes; moving, cut, portal-clipped and bent geometry captured | Kept only if it removes measured geometry-bound work without a missed surface, a swapped pick or a stale bound |

G3a shares no files with V2a and V1a and runs beside them; G3b waits on moving
blends publishing its margin. G4 is V2b and lands where the visibility plan
places it. G5 is built on `lane/g5`, which is unmerged and far behind the
integration branch; it lands on its windowed check after a merge: at flight B
facing the motion gallery through the stairwell wall, `world.rates` reads the
gallery's exhibits at the floor tier with the query latency printed, a
presented burst stepping into the gallery shows no exhibit pose staler than one
floor period plus that latency, a body whose shadow reaches the lit stair keeps
its shown tier, a camera cut keeps demand until the first result for the new
camera, and `world.cull frustum` lapses every occluded reading, on Direct3D 12
and then Vulkan. G6 waits on G3a, and its one-draw tier on V2c; before it is
built its parents are measured against the two clauses the field levels failed
(enclosure, and a witness crossing within twice the budget). G7 waits on G6 and
on V1's buckets for its comparison. G8 follows the streaming plan's order. G9
waits on V1b and a measurement. The visibility plan's table keeps V3a and V3b.
Beside the steps, one measurement with no build: the far cascade's
receiver-mask occupancy under the moving sun and wind (D8), read in the lead's
window.

## The cold-bake bar under demand tiers

Demand tiers change what "the debug world's meshes" can mean, because a tier
exists only when a view demands it and D7 sets no finest cell for a
mirror-close view. The bar has one definition, in
[how the bars are read](streaming-and-memory.md#how-the-bars-are-read): the
minute is absolute and times the boot's demanded set, the scope the owner
confirmed
([the ruling](../decisions/residency.md#the-owners-rulings-on-look-and-policy)).
A finer tier a later view asks for is a cache miss, read as streaming cost;
nothing on this page defines the bar a second time.

## Found while reading

Defects the round surfaced that stand on their own, each a small fix for
whichever lane next touches its file:

- A chunk of a displaced draw carries the whole height field's band.
  `SdfMeshDresser` sets each draw's `Displacement = group.Displacement` whether
  or not the group is chunked, and `SdfMeshBox.TryOf` widens the box by
  `displacement.Low` to `displacement.High`, so a valley-floor
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

- A level source that encloses carved and thin parts at coarse cells, and
  brackets every witness there, reopens levels past the collapse's floor (D1).
  D3's parents, contoured from the field, face the same two clauses and are
  measured against them before G6.
- A bar device measured geometry-bound on a large partly visible draw after
  G2 to G7 admits G9; one bound on triangle setup after G9 and V4 admits the
  compute rasterizer as an enhancement.
- The overlook's stand parents failing the reference at sub-pixel error admits
  the hybrid far tier, briefed with filtered occupancy measured from the field;
  their losing to the shared prototype path on bytes, derivation or Deck frame
  cost makes the shared path the far field, with parents kept only over
  repeated arrangements.
- The normal component binds the error contract on nearly every level
  measured, so appearance filtering at coarse levels is briefed before any
  deeper chain is tried.
- A cascade dominated by static redraw with unchanged projections reopens
  cascade caching (V5); the receiver mask's occupied share measured small
  under the moving sun briefs sparse shadow pages without caching.
- G5's counted query latency exceeding a floor tier's period on the bar
  devices reopens the floor tier's choice (a higher floor, or a host-side
  test of the reach box against a reduced pyramid).

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
