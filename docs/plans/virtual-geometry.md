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
is this memo's reasoning; **[hypothesis]** is a prediction nothing has measured;
**[review]** marks a decision the adversarial review of the first draft (Codex
Astra, sixteen findings F01 to F16) changed, each answered one by one in
[the answers to the review](#answers-to-the-review). A number carries the
command that produced it. Durable citations are by symbol; a `file:line` is a
lead at this revision.

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
makes at coarser cells, each level offset outward by its budget so a joint
between independently levelled groups never opens and a thin part survives,
its error measured in world units both ways against the field's surface by
bracketed witnesses and raised for every material or normal region it loses,
and ending in an empty level whose error is the group's own extent; **a skinned
group's levels are contoured per member**, each piece riding one slot, so the
error measured at rest holds in every pose; **a skinned draw is bounded exactly
by its members' rest boxes under their live transforms**, read from the slot
table the kernel already has; **a hierarchy above the draw is contoured from
the field too** (per-member pieces and, where the motion program bounds the
pose, one proxy per distant creation; one parent per chunked union; one parent
per stand of static copies), one parent per render class, each a closed
replacement the kernel chooses by the same pixel rule and each triangle naming
the child it came from, so the forest's far field is a region of the field and
not an impostor atlas or an authored HLOD, kept where it beats the shared
prototype path on bytes, bake work and frame cost; **the finest tier becomes a
cache baked on demand** at what the views resolve, with the detail it owes
bounded and reported, which is virtual geometry from the field; and **clusters
inside a draw, compute micropolygon raster, mesh shaders and virtual shadow
maps are watched**, each with the measurement that admits it, because the
floor, the skinned gap and the far field dominate at MMO scale and none of
those four touches them. Borrowed techniques win where they are better (the two-phase
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
  cascade. Moving-blends steps 1–3 supersede the original one-member premise:
  `SdfMeshSkin.Build` supplies two-owner influences, and `sdfMeshWorldVertex`
  resolves their group-relative dual quaternion and corrected normal. Rigid
  one-member vertices retain the fast path. A future bound must cover the
  admitted blend's envelope, not just the union of member rest boxes.

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
  per draw (`sdfMeshOcclusionVisibleRW[draw]`, `state.x`) is read back by
  nothing. What is read back is `SdfPortalSight`'s own queries **[opened]**:
  the kernel's sight step tests "Four rows per screen" against the same
  pyramid through `sdfOcclusionMaySee` and writes one word per query at
  `SDF_SIGHT_COUNT_BASE + id.x`; `SdfMeshOcclusion.ReadSight` hands that range
  back once the slot completes, and `SdfPortalSight` applies a result only to
  the query and camera it was captured for ("Unknown, changed and retired
  observations always keep demand"). It is a reusable per-query box test with
  a completion seam, not per-draw evidence (F15 **[review]**); D6 builds on it
  as such. The frame-bar packet records the consequence of posing without
  occlusion ([open items](open-items.md#engine-job-system), the CPU item): at
  flight B the motion gallery's exhibits pose at 60 Hz behind the stairwell
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
   mesh's defects; a field level is contoured from the field per member, each
   piece riding one slot, so its rest error holds posed
   ([D1](#d1-the-unit-of-detail), the error contract). Argon's variant stays
   as the fallback if G1 rejects field levels.
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
   a per-body sight query on the swept reach box, whose evidence lowers a body
   to the slowest nonzero tier and never holds it, with every shadow layer's
   demand read from the receiver test ([D6](#d6-the-hosts-work-per-moved-body)).
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
| 1a. Field-derived levels: level k is the group's field contoured again by the one contouring on a lattice of cell `c0 * 2^k`, at the iso-value that offsets the surface outward by the level's budget (so a level grows and never retreats), continued until a level is empty; its error measured in world units against the field's surface both ways by bracketed witnesses and raised for every material or normal region the level loses and every part that vanishes ([the error contract](#the-error-contract)), then raised to the finer level's so the kernel's walk stays monotonic; a skinned group's levels contoured per member, each piece riding one slot | A coarse lattice drops sub-cell parts, closes sub-cell holes and merges near parts by itself, which a collapse refuses at locked places; a level is not inherited from level 0, so the nose's defects do not block it; the field answers distance where a simplifier tracks displacement (Bevy's author calls perceptual simplification "very much an unsolved problem", Claude **[record]**, opened); an offset is one iso-value for a field and a hard problem for a mesh | Crossings, contour and answer per surface cell of each coarse lattice (about a third of one level's surface work for the chain's contours, a geometric series; Claude **[record]**, a lead), plus the oracle's bracket walks (several field evaluations per witness) and the reverse hierarchy per level, which the first draft left out of the estimate (**[review]**); a re-contoured level carries its own vertices, a geometric tail on the coarse side; per-member pieces draw interior surfaces where members overlap | The offset closes a deliberate sub-pixel gap at the distance where it is sub-pixel; a level that loses a material stripe or a corrugation's normals is held back until that region is sub-pixel, which is pessimistic for shading detail no geometric level can carry ([watched](#rejected-and-watched), appearance filtering) |
| 1b. A deeper collapse chain (Argon's `CellShares = 8f, NormalDegrees = 45f`, unlocking corners) | Reuses the landed collapse | Cheap to try | The far cascade item tried four levels to 128 cells and the card back made no level past the second; the oak reached 1,440 **[opened]** (open items); topology never changes, so a tree's branches never merge into a crown |
| 1c. A cluster LOD DAG over the merged mesh (Nanite's group-lock-simplify-recluster, meshoptimizer's `clodBuild`) | Proven at scale for dense static meshes; a complete replacement cut with group-coherent hysteresis (Codex **[record]**, opened) | A builder, a BVH, a cut selector and history keyed by group; a deep hierarchy accumulates boundary density under chunk seam locks; QEM is a priority, not an error (Codex **[record]**) | Aggregates ("riddled with holes ... leaves and grass", Nanite 2021, cited by [visibility](visibility.md#rejected-and-watched)); its error is the simplifier's displacement, not the field's distance; it inherits level 0's defects as 1b does |
| 1d. 1a without the offset, dilating only where a level would lose a part, and recording the dilation `d` as that level's error (the first draft's dilated variant) | The field-native analogue of Nanite's aggregate voxels, which switch representation "if doing so would result in lower error than using triangles" (Epic, experimental; Claude **[record]**, opened) | One more contour per level where it applies | Two touching closed groups levelled independently can each retreat under a pixel and open a slit wider than one between them (F05 **[review]**); `d` is an offset in field units, not world units, so for a field that is no true distance the recorded error is wrong by the field's slope (F03 **[review]**: `SdfSampledLattice.PassesBetween`'s own comment names "a scope, a flare, a non-uniform scale" as fields that change "by less, or more, than a step's length over a step" **[opened]**); a dilated opaque crown overstates a sparse canopy's coverage **[hypothesis]** |

#### The error contract

The first draft's oracle was value over gradient, a first-order root estimate
that bounds nothing for a nonlinear field, and its reverse direction measured
only level-0 vertices (F04 **[review]**); `SdfSampledLattice.PassesBetween`,
which it named as the existing measure, is a boolean sheet-or-gap heuristic
over neighbouring falls **[opened]**, not a distance. The contract below is
what `Land` measures and records for each level, in world units, so that the
field's scale never enters a recorded error (F03 **[review]**):

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
  `SdfMeshIrradianceScene.TryNearestSurface` already walks **[opened]**. A
  witness inside the group's whole field by more than `τ` lies on an interior
  surface (a per-member piece's face inside another member) and is excluded.
- **Enclosure.** A level is contoured at the iso-value that offsets the
  surface outward by its budget, with the iso-value converted from world
  units by the lattice's measured slope (`SdfSampledLattice.SurfaceSlope`
  **[opened]**) only to aim the contour; the check is geometric: a surface
  witness that lies outside the level's mesh (on the nearest triangle's outer
  side) by more than `τ` refuses the level. A level therefore never retreats
  inside the true surface, so two closed groups that touch or overlap at level
  0 still touch or overlap at every level, whichever levels the kernel picks
  for each (F05 **[review]**). The residual at a joint is overlap, hidden
  inside both solids; a deliberate gap narrower than the budget closes, at
  the distance where it is sub-pixel.
- **Attribute error.** Each surface witness carries level 0's material there;
  its distance to the nearest level triangle *of that material* is its
  material error, as the collapse measures ("within the level's error of a
  triangle of its own material", `SdfMeshCollapse` **[opened]**). A witness
  whose normal differs from the level's interpolated normal at the nearest
  point by more than the collapse's widest angle (`SdfMeshCollapse.NormalDegrees`'
  last entry) is normal-mismatched. Witnesses of one material the level does
  not carry within the budget, and normal-mismatched witnesses, are grouped
  into connected runs over level 0's triangle adjacency, and each run's
  half-diagonal is the error it contributes. A flat sign loses its stripe only
  where the stripe is sub-pixel; a corrugation whose ridges are under the cell
  but whose slopes exceed the angle holds its level back until the whole
  corrugated face is sub-pixel (F02 **[review]**). That pessimism is real and
  is the price of shading detail no geometric level carries; the remedy is
  appearance filtering at coarse levels ([watched](#rejected-and-watched)),
  and G1 prints the three components apart so the lead sees which binds.
- **Vanished parts and the terminal level.** A connected component of level 0
  with no level triangle within the budget of any of its witnesses contributes
  its own half-diagonal, not its distance to whatever surface remains (a tree
  beside a wall is as wrong as the tree is tall, not as far as the wall is).
  The chain continues to the first empty lattice, or until the cell exceeds the
  group's extent and the lattice is `FinalMinimum` samples across the box, where
  an empty level is appended; the empty level records the group's half-diagonal
  as its error and zero vertices, so `sdfSelectionLevel` selects it by the same
  walk and `sdfOcclusionEmit` writes its zero-vertex command as it writes any
  rejected candidate's (F06 **[review]**). A group is drawn as nothing exactly
  where its whole extent projects under a pixel; the stand parent (D3) stands
  above a forest's trees long before that.
- **Skinned groups.** A multi-member group's level is contoured per member:
  the member's own shapes, sampled through a lattice whose selector names them
  alone, in the member's slot frame, so every vertex of the piece rides that
  one slot. A rigid transform with the draw's scale preserves distances up to
  `SdfMeshBox.Stretch`, so the error measured at rest between the piece and
  the member's level-0 surface holds in every pose. The first draft's
  recontoured union skinned by the points-round winner has no such property:
  a hand resting on the torso at rest is assigned to the torso and left behind
  when the arm extends, at a recorded error that stays sub-pixel (F01
  **[review]**). A smooth blend's fillet between two members belongs to
  neither piece and is measured as the pieces' error against the union surface;
  the joint's tear under motion is level 0's own ("torn moving seams", the
  rendering skill's visual-bar note) and a level reproduces it to within its
  error, no worse.
- **Recorded error.** The largest of the two geometric directions, the material
  runs, the normal runs and the vanished components, raised to the finer
  level's.
- **Where the guarantee stops.** At the witness spacing (the level's cell), the
  bracket tolerance `τ`, and features thinner than level 0's own sampling,
  which no witness of either direction sees. The audit's missing-crossings
  measure (`SdfMeshAudit`, "where the field's zero set has no triangle near
  it" **[opened]**, its `MissingCrossings`) runs on every level and is part of
  the acceptance, not a separate lattice the level may ignore.

**Favoured.** 1a, for closed groups first (every creation group and every
unchunked static group); chunked unions keep their collapse levels until D3's
union parent stands above them. **Strongest case against:** the chain's bake
cost on the one-minute bar and its storage are estimates, and the oracle's
bracket walks and reverse hierarchy are work the collapse never does. Both are
what G1 measures, under one accounting scope (the whole bake path, final
lattice included, since `SdfMeshCollapse.Levels` samples nothing itself),
before anything lands in a running World.

**Decided.** A level is contoured from the field at an outward offset, and the
chain continues to an empty level. The invariant: *every level's recorded
error bounds, in world units, the distance from each of its witnesses to the
field's surface and from each surface witness to the level, encloses the
surface, covers every material or normal region and every part the level
loses, and is at least the finer level's*, enforced in one place, the bake's
`Land` stage, which measures each level by [the error contract](#the-error-contract)
before it records it and refuses a level that fails; the selection kernel's
monotonic walk consumes that guarantee and nothing else. The guarantee stops
where the contract says. Rests on the owner's one-pixel ruling (a coarser level
is drawn where its recorded error projects to at most one display pixel), the
one-contouring rule (the same mesher on a coarser lattice at another iso-value
is not a second mesher), and the derived-product ruling (a level is part of the
group's cached mesh, keyed by its geometry and the bake identity). The
visibility plan's V3c, "Field-derived levels only as a bake experiment on large
static unions", is superseded by this decision and amended to point here.
Material point-sampling at a coarse cell is admitted only because the
attribute error holds a level back until the region it loses is sub-pixel.

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
| 2a. Member rest boxes under live transforms: the bake records each member's local box over the vertices that ride it and the root's over the rest; the box is the union of each member's box carried through its slot's current position and orientation and the draw's scale, then the carry; computed where the kernel runs, from the slot table it already binds, or on the host for the rate twin | Exact only for rigid per-vertex skinning; `SdfMeshSkin.Build` now also produces blended influences (moving-blends steps 1–3); `O(members)` per draw, at most `SdfMesh.MaxMembers` | Per-member boxes in the mesh and the record's members table; the kernel's selection rows become derived from slots rather than staged | A vertex on a member bent far from rest is still inside its member's box, so no failure for rigid skinning; a future blended skin would need a refit |
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
as a flag, so no caller can draw past the test. The box bounds where a vertex
can be, never how far the level's surface is from the detailed one: that is
D1's per-member contract, and the two are separate guarantees (F01
**[review]**). With D1 the coarse levels of a skinned group are per-member
pieces riding the same slots, so the draw gains levels whose error holds in
every pose.
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
| 3a. Field-contoured parents, chosen by the kernel as alternatives to their children, one parent per render class: the **creation proxy** (the middle tier is D1's per-member pieces at a coarser cell, one draw per slot, so the Moth's 174 draws become its slots' count; the top tier, one rigid draw of the creation's union at rest, is admitted only where the motion program bounds how far any part moves from the root, and records that bound plus its rest error, so a creation with no derived bound stops at its per-member pieces); the **union parent** (a chunked union contoured at a coarse cell as one closed draw replacing every chunk at once, so no parent ever meets a chunk edge); the **stand parent** (a region of the field over many static copies, the far field of a forest). A parent is contoured over children of one render class (opaque with opaque, glass with glass; a displaced group never joins a parent), so a glass dome over an opaque dial yields an opaque parent that keeps the dial and a glass parent that keeps the dome. Every parent triangle names its source child in a per-triangle table beside `TriangleMaterials`, answered by the same points round that answers materials, so a pick on a parent resolves to the child. The kernel draws a parent when its error projects under a pixel, else its children, with the parent's error raised over its children's coarsest so the cut is monotonic; a parent's record names its children's record range so the kernel skips them without a host walk | Every piece exists: unions over instances, the points round, the pixel rule, the render-class partition (`SdfMeshBakeGroup` already refuses to merge a static run with a glass instance or a displacement **[opened]**, and `WorldRigidAssets` emits a mixed creation's glass and opaque shapes as separate surfaces **[opened]**); a parent is a closed replacement and needs no boundary locking, where a cluster DAG locks group borders (Kapoulkine, Claude **[record]**, opened); Codex's own analysis that a per-prototype DAG cannot merge a region **[record]**; the surface pass already names the triangle a pixel shows, which `sdfMeshMaterial(draw, triangleIndex)` reads **[opened]** | A new group kind spanning instances; a kernel rule for alternatives and a record range; a source table per parent and a pick resolver that takes the triangle beside the identity (`SdfDressedPickMap.Resolve` takes the packed identity alone today **[opened]**); a bake cost per parent of one coarse contour per render class; a stand parent is unique geometry derived from its arrangement, so it trades the prototype sharing V1 compacts for fewer draws, and a moved tree re-derives its cell's parents | The one-draw creation proxy needs a derived motion bound (V2c) and is not admitted without one; a stand parent can lose to shared coarse prototypes on bytes, bake work or frame cost at the Deck (F11 **[review]**), which G7 measures and which falls back to the shared path; coverage of a sparse canopy (see 3d) |
| 3b. Authored HLOD (Simplygon, UE World Partition HLOD; Argon **[search summary]**) | Shipped in open worlds | Authoring per cell; a second asset the author owns | Puck authors fields, not proxies; the engine would carry a proxy the field could derive, which "builds it twice" (AGENTS.md) |
| 3c. Octahedral impostors or billboard clouds (Argon **[search summary]**; Epic's Impostor Baker cited by [visibility](visibility.md#rejected-and-watched)) | Cheap per instance | An atlas per species, a capture pass, a parallax shader | A second representation for shading, picking, reprojection and the visibility record; baked colour where Puck shades from a material identity and the bake "point-samples a quad's winning material" **[opened]** (decisions); no occlusion pyramid contribution without depth output |
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
memory, bake and edit bars" **[opened]**, and the far field must pass them.

**Decided.** The hierarchy above a draw is contoured from the field: creation
proxy, union parent, stand parent, each a closed replacement the kernel chooses
by the one-pixel rule with monotonic error. Three invariants, each enforced at
one door: *a parent and its children are never both drawn in one pass*, in the
kernel's one selection step, which decides a parent's record and its children's
range together; *a parent replaces children of one render class and never
hides a surface of another class* (F09 **[review]**), in the bake's grouping,
which forms a parent over one class's children as it forms a static run today;
and *every parent triangle names the child it came from* (F10 **[review]**), in
the parent's own mesh (its source table), so a pick resolves to the child
whether or not the child's product is resident, and a seam triangle names the
child the points round's winner at its centroid belongs to. The far field is
admitted by cost as well as by look (F11 **[review]**): G7 compares the stand
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
proven, and never copied into the shadow exit or optical passes. The first
draft said normal cones were worth measuring first on "the cascades, which
have no occlusion of their own" (Claude **[record]**); that premise is false
at this revision (F14 **[review]**): the far cascade rejects casters whose
receivers are hidden (`sdfReceiverMayShadow`) and runs the two-phase entry
test against its own pyramid (`sdfOcclusionMaySee` in the late step), both
**[opened]** in `sdf-mesh-occlusion.comp.hlsl`, and its exit and optical
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
| 6a. Rigid dynamic draws read their slot: a draw riding a slot resolves its vertices through the slot's live and previous transforms as a member does, and the kernel moves a slot-frame rest box by that transform; a moved body restages its slots' transform rows, not its groups' records (the Moth's 174 records become 42 slots) | The mesh pass already binds the slot table and reads live and previous transforms for members (`sdfMeshWorldPosition` **[opened]**); V2b's "a moved slot updates its instances' transforms in place" with the transform's one home on the GPU (Claude **[record]**) | A shader change in the three vertex resolvers; the selection rows derived from slots (D2's door); picking and reprojection read the slot | Transform rows already upload by change **[opened]**: `SdfWorldTables.FramePreparation` packs "the rows the frame's moved set owes" and `WriteDynamicTransform` owes a row only when the region's `Write` reports it changed, so the first draft's doubt is closed from the source (the review confirmed the same **[review]**). Still unverified: the carry, scale and clip words of a body straddling a portal **[record]** (Claude); the straddle adds instances carrying the other clip plane under V1, which keeps the slot shared |
| 6b. Occlusion as evidence that a body is shown nowhere, read per body, not per draw: each rated body submits one query per view, its reach box (`RateReach`: the rest reach widened by `MotionReach`, the farthest any pack has posed a part **[opened]**) swept by its part speed over the readback latency, through the same sight step `SdfPortalSight` dispatches today, and a cascade's evidence is its receiver test on that box; a body no view's query sees, no cascade's receivers keep and no consumer read (`PoseReadPack`) takes the slowest nonzero body tier (`SdfUpdateRateTiers.BodyTiers` ends at 5 **[opened]**), never `HeldHertz` | The sight step is exactly this test for portal slabs and already has the completion seam and the stale-result policy ("Unknown, changed and retired observations always keep demand" **[opened]**); the reach box is already what the frustum test rates by (`BodyPixelsPerMetre` over "the sphere it reaches" **[opened]**); the tier floor bounds how stale a hidden body's pose can be to the tier's period plus the latency, where a zero-Hz hold is unbounded and wakes with a continuity cut (`WorldCreationInstances.Rates`: a held body's "next full pack is a continuity cut, as a teleport's is" **[opened]**) | A growable query list in the sight step (its capacity is `MaxScreenSurfaces` today), a receiver-box query in the cascade's kernel, and one word per body per view read back on slot completion | Codex's caution (F07 **[review]**): a camera-hidden body may still contribute to a cascade's exit or optical layer, which the kernel emits from `kept` alone ("Entry visibility (was/visible) must never remove a potential second surface" **[opened]**), so the first draft's "receiver and entry visibility" was wrong evidence; receiver-kept is the right evidence for every layer. The hold's staleness (F08 **[review]**): the pyramid is a completed earlier frame's, so a result is applied only to the camera it was captured for, a missing or stale result keeps demand, and the swept reach box, not the held pose's tight member boxes, is what the pyramid tests, so a limb that would swing out of cover makes its box visible before it does |
| 6c. Leave posing to frustum evidence; let the job system hold unseen work | The job system's first value is "holds unseen work" ([plans](README.md)) | None here | "Unseen" needs the evidence 6b supplies; without it the scheduler holds nothing behind a wall |

**Favoured.** 6a and 6b. **Decided.** A rigid dynamic draw reads its slot, and
occlusion joins frustum and cascade evidence in the one rate decision, as a
tier floor rather than a hold. The invariant for 6b: *a body rated below its
frustum tier by occlusion is one whose swept reach box no view's completed
query saw, no cascade's receivers kept and no consumer read, and it is rated
at the slowest nonzero tier, never held*, enforced in `RateOf`, the one door
every rated body passes, which reads sight results only through
`SdfPortalSight`'s camera-and-epoch match. What the two-pixel rule does and
does not cover (F08 **[review]**): the tiers bound an update's step by part
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
publication door (a tier publishes whole or not at all). That invariant keeps
holes and dangling addresses out; it bounds no debt (F12 **[review]**), so the
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
and D4; no shadow cache is built while the sun moves and the meadow blows.
Caching and sparse paging are two benefits with two admissions (F13
**[review]**): a cache pays only where static redraw with unchanged
projections dominates a cascade ([visibility V5](visibility.md#v5-multi-view-and-shadow-work)),
with a page identity that includes light projection, geometry and deformation
incarnation, level and layer (Codex **[record]**), and the day program and
wind refuse it today; a page pool that allocates and rasterizes only the
texels receivers demand pays wherever demand is sparse, whether or not every
page is dirty, and a moving sun does not refuse it. Puck already computes the
demand: `sdf-caster-receivers.hlsli` reprojects every covered camera sample
into light-space occupancy cells for the far cascade. The admission measure
for a paging experiment is the occupied share of each cascade's texels, read
from that mask under the moving sun, the meadow's wind, a narrow portal view
and a sparse-receiver view (the lead's measurement window, no build); a far
cascade whose receivers occupy a small share of its map in representative
views briefs the experiment, which pages all three layers together with the
filter footprint's margin (`SdfMeshCull.ShadowFilterTexels`) and is judged by
texels written and bytes against the full-layer cascade. Until that measure is
read, 8c stays rejected. The transmission plan's identity peel is unaffected.

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
host never waits on it. Arrival quality is D7's contract (owed error and
interval, lookahead, cancellation, one in-flight budget), measured by G8, since
the address-lifetime invariant here says nothing about latency (F12
**[review]**). A tier's identity stays input-only: the camera that demanded it
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
- **Rigid per-vertex skinning makes exact bounds and posed error cheap.** Each
  vertex rides one slot, so a member box under a live transform is exact (D2),
  and a level contoured per member keeps its rest error in every pose, since a
  rigid transform preserves distances (D1, D3).
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
| Normal-cone culling | Watch | Nanite found it not worthwhile after occlusion; meshoptimizer and NanoMesh keep it (Codex **[record]**, opened) | Clusters adopted; the first place to measure is the near and second cascades' entry layers, which have box-only selection (the far cascade already rejects by receivers and entry depth, and the exit and optical layers must keep faces turned from the light; F14 **[review]**) |
| Appearance filtering at coarse levels (normal variance into roughness, material coverage into a blend) | Watch | D1's attribute error holds a level back until a region of lost normals or material is sub-pixel, which is correct and pessimistic for shading detail no geometry carries (F02 **[review]**) | G1 showing the normal or material component binding the chain on real content (a corrugated or striped face held at level 0 past where its geometry allows), which briefs a filtered-attribute level whose shading, not its geometry, carries the detail |
| Mesh and amplification shaders | Enhancement, not baseline (D10) | No enablement on either backend; Deck Proton leg unqualified | A measured win over the compute list on each device, with automatic fallback |
| Compute micropolygon raster, 64-bit atomics | Enhancement, last (D10) | A second rasterizer; unmeasured on any bar device | Residual fine triangles shown to dominate setup after D1 to D4 and V4 |
| Work graphs | Enhancement on the 4070 only | [visibility](visibility.md#rejected-and-watched) | Unchanged |
| Shadow cascade caching | Rejected for now (D8) | The day program and wind erase the cache; three layers to page consistently | Static redraw with unchanged projections measured dominating a cascade |
| Sparse shadow pages without caching | Rejected until measured (D8) | Page tables, pools and indirection for all three layers against an unmeasured saving | The receiver mask's occupied share of a cascade's texels measured small under the moving sun, wind, a portal view and sparse receivers (F13 **[review]**) |
| Octahedral impostors, billboard clouds | Rejected (D3) | A second representation for shading, picking and reprojection; baked colour against material identity shading | The stand parent and the shared prototype path both failing the dense landscape's L5 bars on the Deck, and the hybrid far tier failing them too (F11 **[review]**) |
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
| **G1. Field-levels audit mode** (offline; brief-ready below) | A field-levels mode in the CPU bake that `puck mesh audit --cpu` drives (`MeshCpuBake`, `SdfMeshPathBake.Land`), printing per group and level: triangles, the three error components of [the error contract](#the-error-contract) (geometric both ways, material runs, normal runs, vanished components) and the recorded error, the Lipschitz lower bound beside the bracketed upper bound, the enclosure check, the audit's open edges, flips and missing crossings on each level, field evaluations (contour samples and oracle brackets apart), and the land thread's cycles, over the whole bake path for both the chain and the collapse | `puck mesh audit --world worlds/debug/avatars/moth.puck --prototype moth --cpu --field-levels` and the same on one chunked union of `worlds/debug/hall.puck` and on a flat multi-material face (a signed or striped placement of the debug world, chosen by the lane), against the collapse on the same groups; the Moth's skinned groups through per-member pieces | Adopt field levels for closed groups when (a) at equal or fewer triangles every level's recorded error is no worse than the collapse's measured error and the chain continues past level 2 to an empty level, the Moth's coarsest nonempty total falling by an order of magnitude from 238,235; (b) no level adds open edges, flips or missing crossings beyond level 0's, and every level passes enclosure; (c) the chain's field evaluations and land cycles over the whole bake path do not exceed the collapse path's by more than the chain's extra levels justify in triangles saved (the lane states the ratio it found; the lead rules); (d) on the striped face the material component, not the geometric one, is what holds the level back. Any failed clause names the fallback (Argon's member-aware collapse for skinned groups) or the fix. **Result: not adopted.** The mode stands (`SdfMeshFieldLevels`, `SdfMeshPathBake.ContourLevel`, `puck mesh audit --cpu --field-levels`; readings in its commit). (a) fails: at no more triangles a field level's recorded error exceeds the collapse's measured error on most Moth groups, on the stairs union and on the trims face, with or without the normal component, because each level carries its offset and a dual contour tiles faces the planar merge and the collapse keep coarse; its floor sub-clause holds, the chain reaching an empty level where the collapse stops. (b) fails: coarse levels of carved and thin parts flip, and the coarsest levels leave surface outside them. (c) is stated for the lead: the chain's contours cost about the bake's own lattices, and its oracle several times more. (d) holds against the geometric component on the trims band once the band is under a level's cell, but the normal component binds above both there and on nearly every level measured, the collapse's included. Per [what would change the answer](#what-would-change-the-answer) this sends skinned levels to the member-aware collapse and static levels to V3b's simplifier, keeps D3's parents contoured from the field (the only levels that continue past the collapse's floor), and briefs appearance filtering, since the normal runs bind the error contract on real content. The lead's ruling on (c): the bake pays only the chain's contours, which cost about the bake's own lattices; the oracle is the audit's measurement, not the bake's, so cost does not decide the chain. Because the field chain is the only path past the collapse's floor, the next step (G1b) appends field levels below the collapse's coarsest level rather than replacing the collapse: a component that would flip vanishes instead (recording its error under the contract), field levels' near-coplanar faces merge, and the appended levels meet clauses (a) and (b) on their own against the error and topology of the level they follow |
| **G1b. Field levels that follow the collapse** (offline) | The G1 mode rebuilt as the hybrid chain: `SdfMeshFieldLevels.Contour` contours coarsest first down to the first level past `SdfMeshCollapse.KeptShare` of the collapse's coarsest (level zero where a group has no collapse level), flattens near-coplanar faces before the planar merge (`SdfMeshPlanarMerge.Flatten`) and vanishes the smallest components that flip or open an edge until the level holds no more of either than level zero; `SdfMeshFieldLevels.Measure` counts a vanished part's level-zero surface by its run's extent and excuses it from enclosure and from the audit's missing crossings (`SdfMeshAudit.Audit`'s accounted surface); the CLI judges each level against the level it follows (the kept share, no open edges, flips or missing crossings beyond level zero's, no surface outside, no refused witness, a recorded error below the group's half diagonal), keeping the levels that pass | The same three runs as G1: `puck mesh audit --cpu --field-levels` on the Moth, `hall-stairs` and `shape-trims` | The floor sub-clause of (a): the Moth's coarsest nonempty total falls by an order of magnitude from the collapse's, with (b) holding on every kept level. **Result: fails.** Kept levels bring the Moth's coarsest nonempty total to about two thirds of the collapse's without the normal component and about four fifths with it, nowhere near a tenth; the stairs keep no field level and the trims face one. The eyes take four levels each down to 86 triangles and other smooth closed parts one or more (90 of 174 groups gain a level without the normal component, 48 of them unbroken to the empty level); carved and thin parts (the shins' cavities and arch bands, the pods) are refused by enclosure (surface outside the level) and by the contract's refusal rule (level witnesses with no crossing within twice the budget, where the coarse contour misplaces surface by more than twice its offset), and with the normal component counted most refused levels reach the half diagonal and are dominated by the terminal level. Waiving enclosure and refusal still leaves the floor at about a sixth of the collapse's: some levels vanish whole for their flips (the pods' bands) and others still fail the kept share or missing crossings. Flattening recovers about a tenth of the stairs' faceted levels' triangles; the rest is the offset's rounded edges, a surface no planar merge takes. The bake would pay only the contours, about a quarter of the bake's own lattice evaluations on the Moth (readings in its commit). What it rejects: the outward-offset contour as the route past the collapse's floor on carved and thin parts. What it leaves open for the lead: shipping the levels that pass, a safe and small saving, or another level source for carved parts (the member-aware collapse, V3b's simplifier, or a field level without the offset, D1's option 1d). **Re-run on the corrected evaluator (G1c; the same three runs, unchanged):** still fails. The evaluator fix alone moved the collapse's floor, not the field chain's: the Moth's coarsest collapse total fell from 238,235 to 110,363 (groups with no collapse level 38 to 13) because level zero lost most of its flips. The field levels bring the coarsest nonempty total to 79,921 without the normal component (3.0x below 238,235, 1.4x below the corrected collapse's) and 89,949 with it (2.6x, 1.2x); 82 groups gain a level without the normal component (63 unbroken to the empty level) and 41 with it (24). The stairs and the trims face read exactly as before. Rounding is not a clause: `SdfMeshFieldLevels.Certified` (the 106 of 180 groups still uncertified read at most 4.5 raws over, 0.069 mm) only decides whether `Contour` reuses the coarser level's samples, and `MeshFieldLevels.Judge` reads no certified or lower-over-upper figure, so no rounding floor was added. What still blocks the floor, by the groups that gain no level (98 groups, 73,007 of the 79,921): components that all vanish for flips or open edges (35 groups, 54,706 triangles, the shins, pod bands and heel housings first), witnesses with no crossing within twice the budget (26 groups, 39,570) and surface outside the level (15 groups, 30,754), with the normal component then dominating the rest; waiving enclosure, refusal and dominance together still leaves 41,267, not the 23,823 an order of magnitude needs (readings in its commit) |
| G2. Field levels in the bake and region | Levels contoured from the field for closed groups at the outward offset, their own vertices laid after the merged ones (a representation change to `SdfMeshLevel`, which gains zero-vertex terminal levels), skinned groups' levels as per-member pieces, `SdfMeshCollapse` kept only for chunked unions | `world.counters gpu pass sdf.world$mesh` vertices with the Moth at boom 1.5, 4.2 and 12 m and at 50 and 100 m under `world.detail auto`, against today's build; captures from every side at each distance, and of a walking Moth with an arm extended and a hand on the torso at 12 and 50 m (the F01 witness); captures of two touching closed placements across their level transitions (the F05 witness); `puck mesh audit` clean on the levelled groups; cold-bake stage seconds per backend | Vertices fall at every distance past 4.2 m with captures meeting the reference, no slit at a joint and no limb left behind; the cold bake stays within the bar or the owner's call on tiers is taken first |
| G3. Member boxes and `unboxed` retired | Per-member rest boxes in the mesh and record; the kernel's union under live slots; the host twin for the rate decision; `unboxed` deleted | Camera facing away from the falls and the garden crowd: membered draws' vertices in `world.counters gpu` fall to zero when off-frustum or occluded and return when the camera turns; `world.view.pick` on a bent limb still names it; a cascade still shadows a hidden body onto a visible receiver | Bounds alone (F16 **[review]**): no membered draw drawn in a pass that cannot show it, no missed surface in a presented burst of a walking Moth and a portal straddle. Membered draws selecting levels 1 and 2 at distance is the G2+G3 integration check, run when both have landed |
| G4. Slot-resolved rigid draws (V2b's design) | A rigid dynamic draw reads its slot's live and previous transforms; selection rows derived from the slot's rest box | `dotnet-trace` of the frame thread at flight B: `sdf.instances records.changed` falls from about two thousand to the moved slots' count; `world.debug-view motion` still on a moving body; a straddling body clipped on both sides at `debug.go portals` | Records restaged per moved body equal its slots, not its groups |
| G5. Occlusion-aware rate floor (the frame-bar CPU item) | The sight step's query list grown past `MaxScreenSurfaces` and fed one swept reach box per rated body per view; a receiver-box query in the cascade kernel; `RateOf` rating a body no query saw, no receiver kept and no consumer read at the slowest nonzero tier | Flight B facing the motion gallery through the stairwell wall: `world.rates` lists the exhibits at the floor tier, with the query latency in frames printed beside; stepping into the gallery in a presented burst; a body behind the wall whose shadow falls on the lit stair, and one whose glass or back surface feeds the far cascade's exit or optical layer, still posing at its shadow's rate; a camera cut onto a hidden body | Bodies behind the wall at the floor tier; no exhibit's presented pose older than one floor period plus the counted latency; every body a cascade's receivers keep rated as shown; a cut keeps demand until the first result for the new camera |
| G6. Creation proxy and union parent | The alternatives rule in the kernel (a parent's record names its children's range), per-member pieces as the proxy's middle tier, the one-draw tier only where V2c's bound exists, parents per render class with a per-triangle source table, `SdfDressedPickMap.Resolve` taking the triangle, union parents above chunked unions | The Moth at 50 and 100 m draws its slots' count of records, and one per render class where its motion bound is derived; `world.counters gpu` draws and vertices at the garden crowd from the lawn; the hall's unions from the forest pose at (47.80, 0.02, -116.84); a glass-over-opaque creation (the lift's cab, or an exhibit the lane chooses) captured at parent distance against its children; `world.view.pick` on a parent naming the same child as a pick on the children at the same pixel; a parent and its children never both drawn (a count per pass) | Draws per distant creation fall to its slots, and to one per render class where admitted; the hall's far draws fall to its parents; no surface of one class lost behind a parent of another; picks agree; captures meet the reference |
| G7. Stand parents for the forest (dense landscapes L4's far field) | A bake group over many static copies contoured as one region per render class, with a source table; the shared prototype path at D1's coarsest levels under V1's buckets as the comparison | `view.override layout three-quarter` then `view.override camera forest-overview`, and `body.pose spawn:forest 0` looking south: draws, vertices, candidates and cascade vertices against the shared path's; resident bytes of parents and children together (`world.residency`); cold derivation of the belt's parents and the re-derivation one moved tree costs; captures from the overlook and the road against the reference; the nested forest view's own counts; the frame cost on the Deck's leg in the lead's window | The overlook's far belt draws as parents at sub-pixel error and reads as a forest, and the parents beat the shared path on bytes, derivation and frame cost; where they read wrong the hybrid far tier is briefed, where they cost more the far field is the shared path (with parents shared over repeated arrangements), and the pixel rule is never loosened |
| G8. Demand-bounded tiers | The tier fact in the geometry key, the finest tier baked on demand with lookahead, cancellation and the one in-flight budget, `world.residency` reporting tiers owed with each group's owed error and interval, `world.budget` per creation | Spawn in the studio and walk to the Moth's face at boom 1.5 m, retreat, and cut back; open a portal onto a second demanding view: owed error and interval per group over the walk, peak resident and in-flight bytes, and the lead's produce-work window; the cold bake from an empty store on both backends | Owed error never worse than one tier below demand for longer than the landing budget's frames on any of the four runs; peak bytes under the library's budget; cold-bake seconds against today's with the owner's answer on what counts; no view ever refused, only owed |
| G9. Clusters inside a draw (D4's experiment) | Flat clusters of the chosen level on V1's buckets, behind a lever | The terrain sector and the hall shell from inside, on the 2060 or at the Deck's resolution: candidates, survivors, pyramid tests, rejected triangles, reference writes; moving, cut, portal-clipped and bent geometry captured | Kept only if it removes measured geometry-bound work without a missed surface, a swapped pick or a stale bound |

G1 and G3 share no files and run beside V2a and V1a. G2 waits on the lead's
ruling on G1b: two experiments have not broken the floor by an order of
magnitude, and the parts that block it (carved and thin parts refused by
enclosure and by witnesses with no crossing within twice the budget, and
level-zero bake defects such as the nose's) are the same parts where the Moth's
fields were measured exceeding the evaluator's Lipschitz bound by up to 26
times, which a field-sampled witness and an outward-offset contour both rest
on. So the chain waited for that bound to be fixed at its cause and the G1b audit
was re-run unchanged on the corrected evaluator (the G1b row): the floor still
fails, so the work goes to D1's option 1d or the member-aware collapse, and the
levels that pass ship with G2, not alone. The lead's ruling after that re-run:
the evaluator's fix alone cut the collapse's own floor about twofold, field
levels added little on top, and what still blocks them is thin shells flipping
at coarse levels (the Moth's shins, pod bands and heel housings, which her
rebuild is replacing) and fine cells spent where no view resolves them (D7's
per-creation budget). Three experiments on one approach is the point to change
the work: G2 is parked with its comparison re-baselined to the corrected
collapse, and the build order proceeds with the steps that do not wait on it
(G3, G5) and with D7's budget; option 1d or the member-aware collapse is taken
up again after the Moth's rebuild lands and D7 is measured on her. G4 is
V2b and lands where the visibility plan places it; G5 is built on `lane/g5`,
its adversarial review's findings answered (one rejected: a camera-cut frame is
rated with the previous frame's cameras, a lag of at most one floor period, the
same as the frustum tier's), and lands on its windowed check: boot the debug
world windowed on Direct3D 12 from that branch, `world.wait bakes 240`,
`world.wait ready 60`, `world.cadence off`, `world.rates bodies on`; pose at
flight B (`body.pose 16 4.55 30 0 0 0`,
`view.override camera look 12.6 8.3 39.3 13 9.4 35`, `world.wait 60`); expect
`world.rates` to read the motion gallery `@5hz(occluded,latency=Nf)` with N
about 3 to 5 and `sdf.rates.occluded` to rise; a presented burst stepping into
the gallery shows no exhibit pose staler than 200 ms plus the latency; a body
whose shadow reaches the lit stair keeps its shown-elsewhere tier; a camera cut
shows no occluded reading for about N frames; `world.cull frustum` lapses every
occluded reading; then the flight B reading on Vulkan; G6 waits on G2 and G3, and its one-draw tier on V2c; G7
on G6 and on V1's buckets for its comparison; G8 on G2; G9 on V1b and a
measurement. The visibility plan's table keeps V3a and V3b; V3c is replaced by
G1 and G2. Beside the steps, one measurement with no build: the far cascade's
receiver-mask occupancy under the moving sun and wind (D8), read in the
lead's window.

### G1, the first step, as a brief

Offline, load-independent, no GPU, no lease. Add a field-levels mode to the CPU
bake under `puck mesh audit --cpu` (the audit drives `MeshCpuBake`, which runs
`SdfMeshPathBake` on the CPU evaluator). In `Land`, after the planar merge, for
a closed group: contour the group's field again at `c0 * 2^k` for `k = 1, 2,
...` through `SdfSampledLattice` and `SdfDualContouring` (no second mesher), at
the iso-value that offsets the surface outward by the level's budget
(`SdfSampledLattice.SurfaceSlope` converts the budget to field units to aim
the contour), until the lattice is empty or its cell exceeds the group's
extent, then append the empty level with the group's half-diagonal as its
error. For a multi-member group, contour per member through a lattice whose
selector names that member's shapes alone, in the member's slot frame. Measure
each level by [the error contract](#the-error-contract): bracketed witnesses
from the level to the surface and from level 0's surface to the level (the
nearest triangle through `SdfMeshIrradianceScene.TryNearestSurface`'s
hierarchy), the enclosure check, material and normal runs over level 0's
adjacency with `SdfMeshCollapse.NormalDegrees`' widest angle, vanished
components, the Lipschitz lower bound beside each upper bound; raise each
recorded error to the finer level's; run the audit's open-edge, flip and
missing-crossings measures on each level; count field evaluations (contour
samples and oracle brackets apart) and the land thread's cycles over the whole
bake path, for the chain and for the collapse alike. Print per group and
level, the error's components apart. Run it on the Moth (its skinned groups
per member), on one chunked union of the hall, and on a flat multi-material
placement the lane chooses, beside the collapse's levels on the same groups.
The success line is the table's: (a) to (d) together adopt field levels for
closed groups; a failed clause names what it rejects. Nothing lands in a
running World in G1; its deliverable is the measurement and the mode. Systems
it reuses, by name: `SdfSampledLattice` (its `SurfaceSlope`),
`SdfDualContouring`, `SdfMeshAudit` (its `MissingCrossings`),
`SdfMeshIrradianceScene.TryNearestSurface`'s triangle hierarchy,
`SdfProgram`'s Lipschitz analysis, `SdfMeshCollapse.NormalDegrees`,
`SdfMeshPathBake.Land`, `MeshCpuBake`. What it must not do: write a second
mesher, use value over gradient as a distance, change the selection kernel, or
touch the store's identity (the mode is offline).

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
exactly. Claude's own sums ran 449 higher at every level; this memo attributes
that to a counting difference in its `awk` rather than a bake difference
**[inference]**, since the levels are a pure function of the mesh and the two
runs agree on every group line compared, but the attribution is unverified
(the review noted the same). The run's summary is in the lane's commit; the
raw group output was not kept.

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
- G1b found that field levels appended below the collapse fail enclosure and
  the contract's refusal rule on carved and thin parts, so the Moth's floor
  falls by a third at most, not an order of magnitude (79,921 against the
  collapse's 110,363 on the corrected evaluator, the same clauses binding). A level source that encloses
  those parts at coarse cells, and brackets every witness there, reopens field
  levels past the collapse; D3's parents, contoured from the field the same
  way, face the same two clauses and are measured against them before G6.
- A bar device measured geometry-bound on a large partly visible draw after
  G2 to G7 admits G9; one bound on triangle setup after G9 and V4 admits the
  compute rasterizer as an enhancement.
- The overlook's stand parents failing the reference at sub-pixel error admits
  the hybrid far tier, briefed with filtered occupancy measured from the field;
  their losing to the shared prototype path on bytes, derivation or Deck frame
  cost makes the shared path the far field, with parents kept only over
  repeated arrangements.
- G1 showing the material or normal component binding the chain on real
  content briefs appearance filtering at coarse levels.
- A cascade dominated by static redraw with unchanged projections reopens
  cascade caching (V5); the receiver mask's occupied share measured small
  under the moving sun briefs sparse shadow pages without caching.
- G5's counted query latency exceeding a floor tier's period on the bar
  devices reopens the floor tier's choice (a higher floor, or a host-side
  test of the reach box against a reduced pyramid).
- The owner's reading of the cold-bake bar sets what G8 must fit.

## Answers to the review

The adversarial review of the first draft (Codex Astra; findings-only, argued
from the source at the integration tip, nothing run) raised sixteen findings.
Each is answered here with what it changed; every code premise the review
cited was reopened for this revision and held. None is dismissed outright;
where this memo keeps part of the first draft's position, the row says why.

| Finding | Answer |
|---|---|
| F01 (high). Rest-space error does not survive independent skinning: a coarse vertex assigned to a member other than the surface it approximates, or a triangle spanning members, carries limb-sized posed error at a sub-pixel recorded error | Accepted. D1's skinned levels are contoured per member, each piece riding one slot, so rest error holds posed; D3's one-draw creation proxy is admitted only with V2c's derived motion bound added to its error, and a creation without one stops at per-member pieces. G2 gains the extended-arm and hand-on-torso witness |
| F02 (high). Geometric error cannot authorize deleting material or normal features | Accepted. The error contract adds material runs (distance to a triangle of the witness's own material, as the collapse measures) and normal runs (the collapse's widest angle), each contributing its run's extent; G1 prints the components apart and tests a striped face. The pessimism this brings for sub-pixel shading detail is named and appearance filtering is watched |
| F03 (high). `f - d` is a field-unit offset, not a world-unit distance, for a field that is no true distance | Accepted. The offset's iso-value is aimed through the lattice's measured slope and the recorded error is always measured in world units by bracketed witnesses; `d` is never an error |
| F04 (high). Value over gradient is a first-order estimate, the reverse direction measured only level-0 vertices, and `PassesBetween` is not a distance oracle | Accepted. The contract brackets each witness to the surface along the gradient (an upper bound by construction), adds grid witnesses over large triangles and over level-0 triangles, projects surface witnesses onto the true surface so level 0's own contour error does not enter, and makes the audit's missing-crossings measure part of acceptance. `PassesBetween` is cited only for what it is |
| F05 (high). Closed groups levelled independently can open a slit at a joint | Accepted. Every level is offset outward by its budget and the enclosure check refuses a level that retreats, so touching groups keep touching at every level; the residual is hidden overlap. G2 gains the touching-placements witness |
| F06 (medium). The terminating empty level has no finite error | Accepted. The empty level records the group's half-diagonal and zero vertices, selected by the same walk; a vanished component records its own half-diagonal, not its distance to what remains; the chain's growth and stop are stated |
| F07 (high). Entry invisibility cannot stop posing for exit and optical contributors | Accepted. A cascade's evidence in the rate decision is its receiver test on the body's reach box, the same evidence the kernel emits every layer from; entry visibility is not used |
| F08 (high). A zero-Hz occlusion hold has no bounded wake and no guaranteed next-frame feedback | Accepted in substance. Occlusion lowers a body to the slowest nonzero tier and never holds it, so staleness is bounded by the tier's period plus the counted latency; the query is the swept reach box, not the held pose's boxes, so a limb emerging from cover is seen before it emerges; results apply only to their camera and a missing result keeps demand. The first draft's claim that the tiers bound the wake to two pixels is withdrawn; what the first presented frame shows is stated |
| F09 (high). A creation-wide union destroys mixed opaque and glass surfaces | Accepted. A parent is contoured over children of one render class, one parent per class, and a displaced group joins none; G6 captures a glass-over-opaque creation |
| F10 (medium). A stand parent loses the child identity picking needs | Accepted. Every parent triangle names its source child in a per-triangle table answered by the points round, and the pick resolver takes the triangle beside the identity; G6 checks pick equivalence |
| F11 (medium). The forest decision has no cost-based fallback or sharing contract | Accepted. G7 compares the stand parent against the shared prototype path on bytes, derivation (cold and one moved tree), classification, raster and Deck frame cost; a losing parent yields to the shared path, parents over repeated arrangements stay shared, and impostors' reopening condition names the case where every field-derived far tier fails L5's bars |
| F12 (medium). Demand tiers can pass G8 while visibly coarse or spiking | Accepted. D7 gains owed error and owed interval with an admitted degradation (one tier below demand for at most the landing budget), lookahead demand from the camera program's bound, cancellation, hysteresis and one in-flight budget; G8 measures a cut, an approach, a retreat and a portal's concurrent demand |
| F13 (medium). VSM admission tests only cache reuse | Accepted. D8 separates caching (still refused while the sun and wind move) from sparse paging, whose admission measure is the receiver mask's occupied share of a cascade's texels, read in the lead's window with no build |
| F14 (low). The cone opportunity rested on a false premise about the cascades | Accepted. The far cascade's receiver rejection and entry occlusion are stated; the first population to measure is the near and second cascades' entry layers, never the exit or optical layers |
| F15 (low). Session sight readback is not per-draw visibility readback | Accepted. The pipeline section describes `SdfPortalSight` as the per-query slab test and completion seam it is, and D6 builds on it as such (one query per body) rather than on per-draw bits |
| F16 (low). G3's success line needed G2's levels | Accepted. G3 is bounds-only; level selection on membered draws is the G2+G3 integration check |

The review also recorded that the complete chain's cost includes the oracle's
reverse walks and hierarchy construction, which the first draft's one-third
estimate left out, and that G1's accounting needs one scope since the collapse
samples nothing itself; both are now in D1's cost column and G1's clause (c).
Its confirmations (the kernel can read the slot table; member rest boxes under
live transforms enclose their vertices; transform rows upload by change; D5's
contract is sound; input-derived tier identity is right; the depth-less
impostor prohibition does not exist; no mesh-shader or int64 path exists) are
carried where each bears.

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
