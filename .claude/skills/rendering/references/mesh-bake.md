# The mesh bake: contouring, groups, store and audit

Read this before changing the bake (`SdfMeshPathBake`, `SdfSampledLattice`,
`SdfDualContouring`, `SdfMeshBakeGroup`, `SdfMeshGeometryBuilder`, the mesh
region or the mesh store), or when a baked mesh looks wrong. Back to
[../SKILL.md](../SKILL.md). [Frame rendering](../../../../docs/rendering/sdf/handbook/frame-rendering.md#the-bake)
explains the technique.

Every view renders a residency's program from meshes the program is baked into,
with no field walk in the frame. The bake samples the field on the GPU through
the interpreter, contours the samples on the CPU, and stores the meshes.

## Contouring

- **One contouring.** `SdfSampledLattice` contours a GPU-sampled lattice through
  `SdfDualContouring` over `ISdfContourLattice`; never write a second mesher.
  A final lattice refines the cells its samples cannot resolve
  (`SdfSampledLattice.Refine`: sheets or gaps thinner than a cell, not a solid's
  edge or crease a line crosses near its tip, outside corners of different
  materials, two patches in one cell) and contours them at the fine spacing
  through `SdfDualContouring.ExtractRefined`; the cells touching them contour
  fine from interpolated coarse values and merge each piece into one vertex,
  which keeps the mesh crack-free. Never refine a cell without that ring: a
  whole cell next to measured fine samples cracks (`SdfSampledLatticeLawTests`).
  Refining every cell instead (5 mm across the Moth) bakes about 8 M triangles
  in 20 s. A refinement past its fine-sample budget (`MaxFineSamples`) splits
  each cell into fewer fine cells, down to two, and contours whole only past
  that: a sheet thinner than the coarse cell survives any refinement and none
  of the coarse contour, so an all-or-nothing budget drops a long light strip
  or keeps it depending on where the coarse samples fall.
- **Crossings stay on their lattice edges.** Before measuring crossing normals,
  `SdfSampledLattice.CrossingPoints` and `AnswerCrossings` bracket each zero along
  its sign-changing edge through the bake's existing point batches. Both the
  contour and its gradient requests use that root. Projecting a linear estimate
  along its nearest normal can land on another face of a corner, supplying the
  wrong tangent plane and serrating the rim. Synthetic transition-ring edges
  and samples displaced by `CloseSheets` keep their lattice interpolation:
  their intentional surface differs from the original field.
- **Features are placed and shaded as features.** A vertex whose crossings'
  planes all meet and turn by over 30 degrees is a feature: it lies on the
  crease, inside its cell (a point past the cell folds the quads around it into
  teeth along the edge). A convex crease up to half a cell past its cell is
  handed to the neighbour that holds it (`SdfDualContouring.HandOff`), whose
  vertex moves onto it; clamped instead, a diagonal or curved crease loses its
  vertices in a row of notches. The cell that hands it over keeps its vertex on
  its crossings' side of the crease (`BesideCrease`), or the quads it shares
  with the crease vertex fold. A feature takes its crossings' normals, each quad
  splits along the diagonal that turns least from its non-crease corners'
  normals, chosen again after the final field correction moves vertices, and
  `SdfContour.SplitAtCreases` splits it per side at 25 degrees. Incident faces
  identify shading creases independently of the placement's feature flag:
  correcting a vertex must not erase its sharp edge. A lifted chamfer's corner
  meets its neighboring bevel at only 31.4 degrees, so a 32-degree threshold
  blends that corner into a jagged band. Every vertex no crossing pins
  to the surface (`SdfContour.Solved`) is checked against the field before the
  split and moved toward the surface where a rounding near the cell's size left
  it proud. `SdfMeshPathBake.ContourProjection` bounds that movement before any
  incident triangle reverses its original facing; an incomplete move keeps its
  original normal and feature flag. One the contour kept beside a sharp convex crease (`SdfContour.Beside`)
  that lies inside the solid stays a crease vertex when moved, or its face's
  normal shades the other face's quads beside the edge. A gradient shorter than
  a tenth of the field's typical slope straddles a crease and moves nothing
  (`SdfMeshPathBake.KinkGradient`): value over length is no distance there.
- **The bake's field is no true distance.** Scopes, flares and non-uniform
  scales read well under the distance (a fifth of it on the Moth's shins). A
  value becomes a distance only over the gradient's magnitude (the bake kernel
  writes the gradient in field units per world unit), and a test of how a value
  changes over a step is measured against the field's own slope there, never
  against the step's length (`SdfSampledLattice.PassesBetween`).
- **Sheets thinner than the fine cell are closed before they contour**
  (`SdfSampledLattice.CloseSheets`): left to their samples they hole, fray into
  slivers where they thin, and their two faces cross. An acute wedge reads as a
  sheet along a line near its tip; a line a few fine steps beside it inside for
  two steps tells it apart (`Thickens`), or its rim closes into lumps. Only
  features and single-face cells read crossing normals: elsewhere (a fillet
  under the cell) they smudge flat faces, so other vertices keep the
  interpolated gradients. Such a vertex on a fillet under the cell carries the
  fillet's normal, so `SdfContour.SplitAtCreases` gives the corners of its quads
  that face more than 25 degrees from that normal a copy with their face's
  normal (a plinth's 6 mm BoxRound fillet at 21 mm cells otherwise shades a
  band a cell wide along every edge). The material cut samples and searches by
  place, not by copy (`SdfMeshMaterialCut`), so the pieces either side of a
  split edge meet at one boundary point.

## Groups, poses and materials

- **Edge weathering reads the baked normal field.** `sdfMeshSurfaceNormal`
  supplies the visibility record's curvature through `sdfMeshMeanCurvature`:
  half the surface divergence of interpolated unit normals in world units.
  Reuse the triangle's normal reads, keep its sign independent of the camera,
  and return zero for degenerate triangles or meshes without normals. A broad
  plate and a narrow bevel must read different curvature; a constant zero
  silently disables every material's `weathering.edge`.
- **Groups are planned in one place.** `SdfMeshBakeGroup.Plan` forms a
  program's groups, geometry and pose rows; the bake and the mesh audit both
  call it, so a change to grouping changes both.
- **A static copy is one group.** Its shapes bake as their union
  (`SdfInstanceRange.BakesWithPrevious`) on a cubic lattice past the per-axis
  cap with per-shape refinement regions (`SdfMeshPathBake.UnionFinal`); its
  serial contour of about a million samples is the bake's long pole on the
  debug world, so unions take each frame's first lattices.
- **A glass instance is a group of its own** (`SdfMeshBakeGroup.Glass`, from
  `SdfProgram.IsGlassRange`): it never joins a static run and no instance joins
  it, whatever either declares, and a dynamic group is glass only while every
  instance on its slot is. The stampers emit a creation's glass shapes as one
  static instance after its opaque ones (`WorldPlacementStamper`, with a field
  scope of their own when one blends other than by union) and a pooled
  creation's each on its own slot, before any text-coupled instance opens
  (`WorldStampPool.Emit`). A glass draw (`SdfMeshDraw.Glass`) is drawn by the
  glass pass alone and stays out of the mesh pass, the cascades and the
  irradiance scene (`SdfMeshIrradianceScene.IsStatic`).
- **Static features declare their spacing.** `WorldPlacementStamper.ShapeDetail`
  feeds the existing `SdfInstanceRange.Detail` refinement regions from authored
  geometry: an eighth of a sweep's smaller radius, a quarter of a shell's
  thickness or a rounded profile's radius, a thirty-second of a warped shape's
  smallest half-extent, or a thirty-second of a cellular relief's wavelength. The tightest applies, at
  placement scale; a scoped creation keeps its tightest shape's spacing.
  A bound alone misses thin strands and leaves warped corners under-resolved.
  Convex rounding reads the scaled polygon's inradius and extrusion clamp;
  warp extents use `SdfSolidGeometry.HalfExtent`, since a revolve's scale Z is
  an offset and may be zero while its radial extent remains positive. A plate
  longer than eight of its half-thicknesses (a slab, a wall, a light strip)
  declares its half thickness: a lattice sized by its length steps clean over
  a sheet thinner than its cell, and the sheet bakes to nothing.
- **A tall union misses thin layers.** One static creation holding stacked
  floors bakes as one union whose lattice is sized by its height, so a 0.3 m
  slab between two samples never crosses and is dropped while it still
  collides. Give each storey's floor its own prototype and placement
  (`worlds/debug/hall.puck`), so each is a flat group.
- **A group of thin strips alone loses strips.** The coarse narrowing
  (`SdfMeshPathBake.Refine`) keeps a sample within half the lattice's finest
  spacing of the surface, so a 6 cm strip lying between sample rows of a 1.8 m
  coarse cube is cut out of the box and never bakes, while its neighbour a row
  lands near survives (`hall.puck`'s light strips, alone in a creation, baked one
  of four). Keeping samples within half a cell's diagonal keeps every strip but
  fattens the converged box until the final lattice's samples step over the
  plates (every hall light group then baked nothing), so the narrowing stands
  and thin trim joins the large solid it hangs from (`hallStrips` in each slab).
  The bake caps declared-detail refinement at eight cells per coarse axis.
  A dynamic instance declares detail too: a pooled creation's embossed text is
  its own instance on a slot after its shapes' that holds the root's pose
  (`WorldStampPool.TextSlot`), bounded by its glyphs' reach and declaring their
  detail. Sharing the root slot with a blend group put the glyphs in that
  group's coarser lattice, which dropped an "I". A slot's group refines toward
  the finest detail its
  instances declare over their bounds (`SdfMeshBakeGroup.Detail`). Engraved
  text stays in one scoped instance with the shapes it carves.
- **A screen slab is a static instance of its own** (`WorldScreenStamper`),
  bounded by the slab and declaring its thickness as detail. Segments no
  instance owns bake in the world group, one lattice the far distance wide,
  too coarse to hold a centimetre-thick slab or keep the body behind it off its
  face.
- **The bake samples through the interpreter**, in the fragment's `mask` and
  `bake` parts, only while `SdfMeshPathBake.Pending`.
  `sdfMapOmitWorldSegments` (bake only) drops unowned segments, which bake as
  their own group. The pass block's lattice rows are a sync pair with
  `SdfFrameBlock.WriteMeshBake`.
- **A bake never reads the frame's transforms.** A dynamic group's box,
  lattices and points are slot-local, and `sdf-mesh-bake` reads every transform
  through the poses the group's geometry hashed (`SdfMeshGeometryBuilder.Poses`,
  staged after the batch's points, named by each lattice's fourth row,
  `SDF_MESH_BAKE_POSES`), so a mesh is a function of its geometry: a body moving
  while it bakes bakes whole, and two boots of one device bake alike. The
  contour is the mesh, already in that frame.
- **Materials are point-sampled, not taken from the lattice.** Each final
  quad's quarters and vertices are sampled through `sdfMeshBakePoints` (a staged
  World table the residency writes before the upload); a quad whose quarters
  disagree splits into them, and one whose vertices disagree is cut along the
  boundary its edges' bisection finds (`SdfMeshMaterialCut`). A material
  boundary only lies on the surface within one group: two groups' shapes meet
  as crossing meshes, whatever their materials.
- **A group's vertices follow their own shape's slot.** A dynamic group whose
  shapes ride slots besides its own gets one more points round over its
  finished mesh: the bake kernel answers each vertex's winning slot in the first
  gradient word of a points lattice without gradients, and the mesh keeps each
  other slot as a member (`SdfMesh.Members`: its offset from the group's slot
  and the pose it was baked at) and each vertex's member
  (`SdfMesh.VertexMembers`). The draw record's skin word names the members when
  its flags carry `SdfMeshRecord.SkinFlag`, and every mesh read
  (`sdfMeshWorldPosition`, `sdfMeshWorldNormal`, `sdfMeshPreviousPosition`)
  carries a member's vertex out through that slot's live (or previous)
  transform, so the mesh interface binds the dynamic transforms. The surface
  pass records the member slot of the hit triangle's nearest vertex as the
  pixel's frame slot (`sdfMeshSurfaceNormal`), so an inset, weathering, an
  unparented paint and the lanes read the shape's own slot rather than the
  group's. A cut or
  intersection face keeps the slot of the shape it shapes
  (`sdfComposeCandidate`); only a union-family winner hands its slot on. Rigid
  per-vertex skinning stretches a blend seam between members that move far
  apart, until they settle.
- **A skinned group bakes again once its members settle (owner's ruling).** A
  smooth or non-union blend across shapes that move independently is warned at
  authoring time (PUCK122, `CreationMotion.MovingBlends`), and its mesh is
  rebaked once the shapes' relative pose settles. `SdfMeshPathBake.Settle`
  watches every dynamic group whose geometry reads slots besides its own: once
  their poses in the group's frame drift past `SdfMeshSettle.DriftPosition` or
  `DriftAngle` from the baked pose and then hold within `StillPosition` and
  `StillAngle` for `SettleSeconds` of frame time, `SdfMeshBakeGroup.PlanSlot`
  plans that slot's group again with its other slots' poses snapped to
  `SdfMeshSettle.PositionSteps` and `OrientationSteps`
  (`SdfMeshGeometryBuilder.Snap`, whose steps join the hash), so poses that
  settle near one another key one geometry and the library or its store answers
  the second. The snap's worst rounding sits under the drift, so a group baked at
  its snapped pose is no candidate where it stands. The replacement takes the
  group's place drawing the old skinned mesh until its own lands; one that meshes
  no surface hands the group back, blocked until its pose moves. A pose still
  changing never rebakes, and each bake starts at most `MaxInFlight` at once and
  `MaxPerSecond` a second. The thresholds are bake constants beside its cell
  sizes, not document fields: they measure a mesh against its own field, not
  anything a world means. `world.counters sdf.mesh-settle` reads queued, done,
  reused, moving (frames a drifted group waited while it moved), capped and
  refused, summed over the residencies sharing the presentation's library. A
  program that arrives later plans every group at the pose of its frame,
  unsnapped, but keeps a dynamic group whose form held (`SdfMeshBakeGroup.Form`,
  `SdfMeshGeometryBuilder.Form`: the geometry without the other slots' poses):
  a body breathing while a placement elsewhere changes keeps its skinned mesh,
  and only its settle rebakes it (`SdfMeshPathBakeLawTests`, the rebuild law).
- **A landing mesh rebuilds the draws.** A group that bakes again draws the mesh
  it replaces until its own lands, so it counts as drawn already; `Land` and
  `Load` hand the new mesh over through `SdfMeshPathBake.Draw`, which marks the
  draws stale, or a still group keeps drawing the old mesh.

## The mesh region

- **Meshes keep their words; draws repack only their records.** The tables
  place each mesh whole in an arena past a reserve of draw records
  (`SdfMeshRegionArena`), where it stays while a draw names it, so
  `StageMeshRegion` (`SdfWorldTables.MeshRegion.cs`) writes the draw records
  (`SdfMeshRegion.WriteDraws`) and only the meshes the arena newly placed. A
  layout that moves placed meshes when one lands (sections by attribute) made
  every landing rewrite the whole million-triangle region, 45 to 120 ms frames
  on the debug world. Newly placed meshes land within
  `SdfWorldTables.MeshLandingWords` a frame, their draws waiting out of the
  staged records until they do, and a grown region takes the words it held. A
  placement names absolute words (`SdfMeshRegionMesh`); a new per-draw field
  belongs in the record, never in a mesh's words. The record's layout is
  `SdfMeshRegion`'s, a sync pair with `frame/sdf-mesh.hlsli`
  ([sync pairs](sync-pairs.md#engine-buffers-groups-and-bindings)).
- **The bake keeps the frame's budget.** `SdfMeshPathBake.FrameTaps` bounds the
  field evaluations a 60 Hz frame's bake dispatch makes (a gradient sample
  counts five), and a longer frame takes a share for each whole 60 Hz frame it
  spans, up to eight (`FrameBudget`); every lattice is chunked to fit one share
  (final lattices in slabs, material and gradient, fine and material points in
  chunks). Gradients are measured only at the samples `SdfDualContouring` reads
  a normal at (`SdfSampledLattice.GradientPoints`): a cell's normal reads its
  own eight corners, so no other sample needs one.

## The library and the store

- **A batch answers the bake that took it.** `SdfMeshBakeBatch.Bake` names it,
  and `SdfWorldPasses.BeginFrame` polls every bake pass's readbacks each frame,
  so a view that leaves the frame or follows another residency never strands a
  bake.
- **Groups are keyed by geometry** (`SdfMeshGeometryBuilder`: instructions with
  side-table values for offsets, materials by place, a dynamic bound's radius
  alone, other slots by their rounded pose in the group's frame, lane-erosion
  lanes, the reach of a box it clamps), and every meshed group is stored in the
  `SdfMeshLibrary` all of a presentation's residencies share, so a rebuild, a
  returning body or a body crossing from another world takes its mesh; only new
  geometry bakes. The key has two failure modes. A field the hash reads that a
  program assigns by position (a slot, a table offset, a palette index) breaks
  sharing silently: canonicalize it in the builder. Anything the bake samples
  that the instructions do not hold (another slot's transform, a lane, a table)
  leaves a stale mesh drawing after an edit: hash it in the builder. A
  stamp-pool blend group is one instance on its first shape's slot whose later
  shapes ride their own slots, so their places and turns are frame transforms;
  the symptom of a miss is an edit that shows only once the group's first shape
  changes. The key is taken from the frame a program arrives in, so shapes that
  move against one another later keep the mesh of that pose until they settle
  and the group bakes again (the settle rebake above). A geometry whose bake
  found no surface is held as empty (`SdfMeshLibrary.StoreEmpty`), so a group
  of it at another selector finishes at once instead of baking again.
- **A material id shift renumbers every later mesh.** A group whose material
  ids moved takes its mesh renumbered (`SdfMeshLibrary.TryTake`), a new
  `SdfMesh` the mesh region lands again word for word, 2 MB a frame, with its
  draw held out of the frame until it lands. Program material ids are dense in
  registration order, so a placement that stops or starts showing a palette
  shifts every group after it: a responding static row registers the palette
  of every creation it can show (`WorldPlacementStamper.EmitStatic`,
  `ResponsePaletteLawTests`), and a creation with nothing to draw emits no
  instance, so a response swaps one row's materials alone.
- **The World's library persists across runs** (`SdfMeshStore`, the `meshes`
  device cache), keyed by geometry and the bake's identity: the bake kernels'
  bytecode, `Puck.SignedDistance`'s build, and the hash of the
  `PuckMeshBakeSource` files listed in `Puck.SdfVm.csproj`
  (`SdfMeshStore.CodeIdentity`). A new `Puck.SdfVm` file the bake's CPU code
  reads joins that list, or an edit to it reads stale meshes. A boot that bakes
  nothing shows `done … loaded=N` with `dispatches=0`.

## Shadows and indirect light

- **Cascades are depth only**: the shadow pipeline
  (`SdfMeshRasterPass.ShadowKey`, `sdf-mesh-shadow.vert`/`.frag`) draws the
  mesh draws into each cascade's sampled `D32Float` depth from the light camera
  `SdfFrameBlock.WriteShadowCamera` writes, with the view cameras' handedness;
  `sdfMeshShadowDepth` in `passes/sdf-surface-stage.hlsli` decodes that
  reversed, linear depth, so the two projections are a sync pair. The shadow
  tier sizes the cascades (`SdfShadowResolution`, `meshShadowTexels`) and a view
  holds the second slot's cascade only while its world holds a second stable
  slot (`SdfWorldPackage.WithShadows`).
- **A view without shadows holds no cascade.** A view whose quality skips
  shadows (windows, camera views) runs `SdfWorldPackage.WithoutShadows` of its
  fragment: the cascades' passes and their 252 MB of targets go, and the
  surface pass binds the filler.
- **Shadow handoffs are pass values.** The outgoing light fades from its slot's
  cascade by the handoff's weight; the incoming light lights unshadowed until
  it holds the slot. No handoff plans or allocates anything.
- **Indirect light is the irradiance volume.** `SdfWorldResidency.IndirectTier`
  (the host lever, else the frame's tier) drives `SdfMeshIrradiance`, staged
  before the upload into the staged World table `sdfMeshIrradiance` and
  described by the pass-block values `meshIrradianceVolume` and
  `meshIrradianceLow`. The probe layout, key, toroidal slot, normal bias and
  probe weights are a sync pair between `SdfMeshIrradiance` (its CPU feedback
  read included) and `shade/sdf-mesh-irradiance.hlsli`, which only the views
  kernel compiles. The trace reads static mesh draws through
  `SdfMeshIrradianceScene`; never route it through the field interpreter. The
  [handbook](../../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
  owns the technique and its limits.

## Auditing a bake

`puck mesh audit --world <file>` finds bake defects without a GPU or a person
looking: it reads the meshes a World boot stored (`SdfMeshStore`, never writing
it), measures each against its group's own field on the CPU (`SdfMeshAudit`,
through `SdfFieldEvaluator`'s render reading), and prints per group, worst
first, the off-surface error at vertices, centroids and edge midpoints, face and
shading normal error against the field gradient, folds, flips, open edges,
uncovered surface and how loose the field is, then the worst clusters with
rest-pose and author-frame positions, the worst sample's position with the
mesh's facing against the field's gradient there, and the shapes nearest them. The
[CLI reference](../../../../docs/reference/cli.md#puck-mesh-auditbaked-mesh-audit)
owns its flags.

- **Run it before calling a bake or mesh-path change done**, and on a changed
  creation's groups (`--prototype`, `--group <shape>`); a later run over the
  same store proves a fix. `--images <dir>` draws each worst cluster on the
  CPU, shading beside face normals: the offline twin of
  `world.debug-view normals`.
- **`--cpu` measures a bake change with no GPU.** It drives `SdfMeshPathBake`
  with each lattice answered through the group's CPU field, so a contour,
  refinement or field change shows in the audit without a boot. Where the
  fixed-point field and the kernel's agree, its mesh matches a stored Direct3D
  12 bake of the group to the triangle; a field the evaluator refuses (a regular
  polygon, a glyph, a sampled region) needs a World boot.
- **The store is keyed by the bake identity of the build that booted.** From
  your own worktree's CLI, audit a World you ran from the same checkout, or name
  another build with `--build <its Puck.World bin dir>`; a rebuilt build's
  meshes stay readable under the `--identity` an earlier report printed.
  `--history --group <shape>` compares every build's stored bake of one
  geometry. `--backend` names the backend that baked (Direct3D 12 by default).
- **The audit reads stored meshes against the CPU field; `world.slice` reads
  the live field.** Use the slice to see what the GPU walk answers on a plane,
  the audit to see what the bake made of it.
- **Read the metrics together.** Sawtooth edges show as face normals off the
  gradient and folds strung along an edge; torn or dropped walls as uncovered
  crossings and open edges; a cutter that is only a lower bound (a stretched
  ellipsoid, a non-uniform `Scale`, a flare) as a slope deficit, attributed to
  the shape whose own field is loosest there.
- **What it cannot read.** The render reading interprets every warp, fold and
  relief the renderer draws, a non-uniform scale, a sweep of any strand count
  and a path's edge table, each a sync pair with its `mapCore` case
  (`SdfFieldEvaluator.RenderOps.cs`, `SdfFieldEvaluator.Path.cs`); a group
  holding a regular polygon or star (runtime trigonometry), a glyph or a
  sampled region reports `field refused`, and one whose evaluation faults
  `field failed`. Mirrored groups (left and right) share one geometry and one
  stored mesh.
