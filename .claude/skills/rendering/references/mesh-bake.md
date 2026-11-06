# The mesh bake: contouring, groups, store and audit

Read this before changing the bake (`SdfMeshPathBake`, `SdfSampledLattice`,
`SdfDualContouring`, `SdfMeshBakeGroup`, `SdfMeshGeometryBuilder`, the mesh
region or the mesh store), or when a baked mesh looks wrong. Back to
[../SKILL.md](../SKILL.md). [Frame rendering](../../../../docs/rendering/sdf/handbook/frame-rendering.md#the-bake)
explains the technique.

Every view renders a residency's program from meshes the program is baked into,
with no field walk in the frame. The bake samples the field on the GPU through
the interpreter, contours the samples on the CPU, and stores the meshes.

## Reproducing a cold bake

Use `puck_world_start` with a fresh `cacheDir`, `paused: true` and
`lockstep: true`, then `world.wait bakes` and `puck_world_stop`, one World at a
time. `paused` stops authority ticks; it does not stop animated placements on
the display clock. `lockstep` pins that clock before the first frame too
(`WorldFramePresenter.CaptureFrame`), so the first bake captures the same poses.
Pausing or enabling lockstep after attachment is too late to control those
initial inputs. Keep the backend and built source state fixed across repeats.

Compare the key sets before comparing each key's decompressed payload. A
different pose can legitimately produce a different key. The store codec maps
material ids to canonical places before writing every chunk and level, so a
program's palette registration order cannot change a keyed payload. The library
maps those places into the loading program's palette. Ids outside the group's
palette retain their values and are reserved before canonical ids are chosen,
so they cannot collide with a place. Trace a varying key back through `SdfMeshBakeGroup` and its saved poses,
and use `puck mesh audit --cpu --group ...` to separate CPU contour work from
device sampling. Do not infer a GPU race from a whole-store hash alone.

## Contouring

- **Count ownership classes, then fill owned indexes.** `SdfSampledLattice.Refine`
  counts distinct samples by the cell faces they touch: low boundary, interior,
  or high boundary on each axis. Every interior coordinate has the same holders,
  so its class contributes `split - 1` samples per interior axis. The selected
  count admits cells. `FineKeys` numbers their slots in the original cell/sample
  order, then sizes the key buffer from its own count and fills it through those
  slots. Every sample buffer takes that key buffer's length, independently of
  admission's estimate, without growing managed lists and copying them.
  Preserve that order and the region admission order: both feed the bake.
- **Collapse line samples have one owner.** `SdfMeshCollapse.Collapser.AppendLine` links
  samples by index and retains each edge's append order when a collapse moves
  them. `MeasureLines` must finish before `CommitLines` relinks any sample.
  This is the line counterpart of the collapse's existing surface-sample links;
  changing traversal order can change equal-distance choices.
- **Reuse crossed topology.** `CrossingEdges` saves each crossed fine cell's
  low corner and mask after `CloseSheets`; gradient requests and
  `SdfDualContouring.ExtractRefined` consume that same coarse-cell/fine-cell order.
  Fine signs cannot change between that preparation and contouring. Coarse
  vertices remain interleaved in their original order; appending all fine
  vertices afterward changes the payload.
- **Reuse a collapse's source fan until commit.** `SdfMeshCollapse.Collapser.PrepareFrom`
  lays out the unchanged source triangles once for all attempted neighbours.
  A refused neighbour mutates no source geometry; the first accepted neighbour
  ends that fan's attempts. Keep that lifetime explicit when changing `Pass`.
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
  The ring's crossings carry interpolated coarse gradients, so a crease inside
  it is cut: a gap between two inside samples refines only where the falls meet
  half a fine cell below the surface. Solids that touch (a stair's treads, a
  board on its table) meet at the surface, and refining that seam chamfered the
  concave crease above it by a cell.
- **Touching solids are one solid to the contour.** A Box is sharp unless its
  document rounds it ([decision](../../../../docs/decisions/engine-design.md#a-box-is-sharp-unless-its-document-rounds-it)),
  so boxes that touch meet with no groove at the seam and no crevice where the
  seam meets a third face. Inside a union the field is the distance to the
  nearest face of the solid a sample lies in, which beside a seam is the shared
  face, so a value and gradient there measure to the seam, not the surface. A
  transition ring's crossing is therefore searched along its edge as a measured
  one is (below), and the field correction moves no vertex along a gradient its
  own triangles face away from (below). The interpolated normal (`TryNormal`)
  still reads such gradients at a seam, which shades a cell-wide band beside a
  concave crease where a layer thinner than the cell meets its neighbour (the
  strips under `hall-floor-3`). Rewriting sub-cell gap samples as inside, or
  leaving seam-pointing corners out of the interpolated normal, opens holes
  (open edges) across the debug world's store-wide audit wherever it helps a
  seam, so the contour does neither: a seam's residue is fixed in the shape
  language (sharp boxes) or the lattice, not patched in the contour.
  Refining every cell instead (5 mm across the Moth) bakes about 8 M triangles
  in 20 s. The fine-sample budget (`MaxFineSamples`) measures the cells the
  samples cannot resolve first, then each detail region's cells, the region
  covering the fewest cells first, leaving a region that would carry the count
  past the budget to the coarse contour, and takes the finest split serving the
  most regions, down to two fine cells a coarse cell: a member whose region
  spans a whole union (a slab's 2 cm ceiling finish) costs as much as every
  other member together, and dropping every region for it left the strips
  under the slab at the coarse cell, drawn or lost by where the coarse samples
  fell. A sheet thinner than the coarse cell survives any refinement and none
  of the coarse contour.
- **Crossings stay on their lattice edges.** Before measuring crossing normals,
  `SdfSampledLattice.CrossingPoints` and `AnswerCrossings` bracket each zero along
  its sign-changing edge through the bake's existing point batches. Both the
  contour and its gradient requests use that root. Projecting a linear estimate
  along its nearest normal can land on another face of a corner, supplying the
  wrong tangent plane and serrating the rim. A transition ring's edge is
  searched too, bracketed by the signs its interpolated ends give the contour's
  topology: inside a union the interpolated value is no distance (a 2 cm layer
  under a 6 cm coarse cell measures to the face it shares with its neighbour),
  and its linear estimate put a slab's underside a centimetre up inside the
  slab. Only samples displaced by `CloseSheets` keep their lattice
  interpolation: their intentional surface differs from the original field.
  The search (`CrossingRoot`) measures two trials a round, a tolerance apart
  about an estimate (the linear one, then the secant through the last round's
  pair), so a root the estimate all but hits closes the bracket that round, and
  cuts the bracket in thirds once the thirdings it still needs fill its
  remaining rounds; it ends once the bracket is an 8,192th of the edge wide (the
  root, its middle, within a 16,384th), in at most eleven rounds. The
  `ACrossingLandsWithinASixteenThousandthOfItsEdgeInAFewRounds` law holds the
  bound and the cost. Each round is a readback round trip of the group, and
  only the groups the memory account admits hold a final lattice, so a group's rounds bound its
  latency: a search must not trade fewer samples for more rounds than its worst
  root needs (a one-trial secant search's worst roots took up to 37).
- **Signs are read once.** `SdfSampledLattice.FineInside` keeps each fine
  sample's sign in a bit once read (`AnswerRefinement` clears them,
  `CloseSample` keeps a closed sample's), and the contour's corner masks,
  `CrossingEdges`, `GradientPoints` and `CloseSheets`' line scans and face tests
  read signs through it; a value is computed only where its magnitude is used.
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
  Nor does one whose gradient turns away from the way its own triangles face
  (`ContourProjection.Outward`): inside a layer thinner than a cell the
  gradient points at the seam the layer shares with its neighbour, and the move
  lifted the vertex onto that seam.
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
  normal (a plinth's authored 6 mm fillet at 21 mm cells otherwise shades a
  band a cell wide along every edge). The material cut samples and searches by
  place, not by copy (`SdfMeshMaterialCut`), so the pieces either side of a
  split edge meet at one boundary point.

## Groups, poses and materials

The dispatch's fixed `MeshBakeMaxSamples` budget counts sampled values, not
the saved pose rows appended to its input point table. Each slot of the
background GPU work holds its batch's point table, a host-visible buffer the
kernel reads in place (`SdfMeshBakeGpuWork`), grown in 4 MiB granules once the
slot's last job is released (`SdfGpuWork.Granule`), and bound through the slot's
own World set at the bake's point member, where the tables' World sets bind the
unused-member buffer (`SdfWorldTables.WriteBackgroundWorldSet`).
`SdfMeshBakeBatch.InputRows` is the highest input prefix actually read: explicit
points retain their sample offsets; saved poses still begin after all samples.
A grid-only batch without poses needs no input rows. Do not allocate or write
its sample-count holes; nothing reserves `MeshBakeMaxSamples` rows merely
because that is the sampled output budget. Interior holes before explicit
points or poses remain part of the current kernel layout. Preserve their
addresses when changing storage. The holes are never read, so a slot writes only
the runs the batch names (`SdfMeshBakeBatch.PointRuns`), comparing nothing.
Charging poses against the sample limit can strand the first pending lattice
forever: a nearly cubic lattice plus a sparse pose span no longer fits, so the
scheduler takes no work and every group behind it waits. Cold bakes expose this;
warm meshes bypass it. Keep the sampled-output bound while allowing the input
table to carry all the batch's poses. The posed-cube and growing-input-table
laws exercise that boundary.

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
  debug world, so the plan lists unions first, which decides only among groups
  the demand points rank alike.
- **Work goes near-first** ([residency](../../../../docs/decisions/residency.md)).
  `SdfMeshDemand` holds the eye of every view of the residency's latest frame
  (an `Elsewhere` or hidden view demands nothing; a filmed camera measures in
  its own image's pixels, `SdfViewSnapshot.DemandHeight`). A group whose
  planned box (`Group.TakeDemandBox`, kept before `Begin` cubes it) projects
  past a pixel ranks by the pixels a metre covers at the box, nearest first
  whatever its size; one under a pixel ranks after. Ranking by projected size
  instead let far terrain and shells outrank the lobby's small parts, and the
  occupied area landed no sooner than in plan order. `TryTake` takes
  lattices, the work queue (`Schedule`) runs store loads, lattice answers and
  every CPU stage of the bake, and `SdfWorldTables.OrderLanding`
  orders owed meshes for landing in that order, the plan's or the list's order among
  equals and a draw no host box bounds first. With no demand point (the CPU
  laws, `puck mesh audit --cpu`) the plan's order stands. Order never changes
  a mesh. `world.residency [radius]` reads when the groups landed and became
  resident from the bake's start, near the primary seat's eye and everywhere,
  with the store's reads, the region's landing, program build times and the
  probe cache's bytes.
- **The bake's CPU work is one process-wide pool.** `SdfMeshPathBake.Schedule`
  queues every residency's work on one pool of a worker a processor: the
  lattice answers (decoding a readback and narrowing a group) first, in arrival
  order, then the stages by group priority. A pool per residency oversubscribes
  the processors three times over on the debug world, and workers below normal
  priority lose them to the irradiance trace and the machines, more than doubling a
  cold bake. A completed batch's readback is copied and decoded into each
  group's answer buffers on the pool, ahead of every queued stage
  (`SdfMeshPathBake.RunFirst`, from `SdfMeshBakeGpuWork.Complete`), and its slot
  is released as soon as the copy is taken, so the frame thread reads no
  samples. Only the groups the process memory account admits hold a final lattice
  (admitted at its first slab, whose answer allocates the lattice's values), by
  each group's own peak (`PeakOf`); the admission rules are in
  [data and memory](../../dotnet-performance/references/data-and-memory.md#owned-storage-and-byte-admission).
  Unadmitted groups keep refining their small coarse lattices. A first slab
  handed back unanswered (`Abandon`: a job whose recording failed or whose
  service was disposed) keeps its admission;
  counting its retry as a new one let returned first slabs fill every admission
  and stall the bake. A stage that throws finishes its group with no new mesh
  (`[sdf.mesh-bake] ... failed`, `Fail`), releasing its reservation and
  workspace, and a later program bakes it again. A group a later program does
  not keep, or whose residency is released, is retired (`Group.Retired`): the
  pool drops its queued work, an answer arriving for it is dropped, and its
  workspace is freed when its running job ends; `Retire` never waits.
- **Nothing array-sized runs under the bake's gate.** The frame thread takes
  `m_gate` in `PrepareFrame` and `TryTake`, so a worker fills a
  group's lattice, points and answers on its own job, which only that group's
  one running job touches, and takes the gate only to publish the group's next
  request or stage. The done line's `frame-gate=[max,total,over-1ms]` reads the
  frame thread's waits on the gate.
- **A dispatch fills its samples.** A take takes lattices by priority until
  `MeshBakeMaxSamples`, passing over one too large for what is left and taking
  smaller ones behind it; stopping at the first that did not fit left the debug
  world's dispatches under half full.
- **One geometry bakes once a bake.** A group whose sealed geometry another
  group of the same restart already bakes or loads waits on it
  (`Group.Leader`, `Follow`) and takes the library's mesh, renumbered, when the
  leader lands; the debug world plans 1,115 of its 2,090 groups that way. The
  renumbering shares immutable geometry and allocates remapped triangle materials, so it runs on the pool
  (`TakeFollowed`), outside the gate the frame takes; a leader that found no
  surface lands the follower empty too (`Draw(group, null)`), or it would keep
  drawing the mesh it had before the rebake. An offline bake of chosen groups
  (`puck mesh audit --cpu --group`) names them in `SdfMeshPathBake.Selection`:
  an unselected group finishes unbaked and leads none, where answering it as
  empty space handed its empty result to a selected follower.
- **Large static groups draw in chunks.** A union, and one shape whose contour reaches
  `SdfMeshChunks.WholeTriangles` (a 500,000-triangle terrain held one worker for its whole collapse), is chunked;
  a smaller single shape stays whole, since locked seams tripled a terrain sector's first level. A mesh that would
  fill more than `SdfMeshChunks.MostChunks` 8 m bins takes bins of twice the side until it fills no more: the world
  group's far ground cut at 8 m makes 160,000 draws, too many to cull and
  level on the CPU for every pass, so `frame/sdf-mesh-selection.hlsli` selects them through the existing counted indirect
  path; chunk sizing still controls retained geometry and candidate work.
  `SdfMeshChunks` partitions the finished contour's triangles by their
  centroid into 8 m bins before merging and collapse. It never clips a triangle: each chunk's box includes its whole
  triangles. The merge keeps every open-edge vertex and the collapse locks them, so every level shares the exact seam
  edges with its neighbours. `SdfMesh.Chunks` holds the separately levelled draws; the parent concatenates their merged
  surfaces for whole-group queries, and never enters the GPU region. The store writes only the chunks and reconstructs
  that query view on load. Dynamic groups and glass stay whole; glass skips collapse because its pass always draws level
  zero. The existing per-draw box cull and detail selector apply to each chunk without shader changes.
- **A glass instance is a group of its own** (`SdfMeshBakeGroup.Glass`, from
  `SdfProgram.IsGlassRange`): it never joins a static run and no instance joins
  it, whatever either declares, and a dynamic group is glass only while every
  instance on its slot is. The stampers emit a creation's glass shapes as one
  static instance after its opaque ones (`WorldPlacementStamper`, with a field
  scope of their own when one blends other than by union) and a pooled
  creation's each on its own slot, before any text-coupled instance opens
  (`WorldCreationInstances.Emit`). A glass draw (`SdfMeshDraw.Glass`) is drawn by the
  glass pass alone and stays out of the mesh pass, the cascades and the
  irradiance scene (`SdfMeshIrradianceScene.IsStatic`).
- **Static features declare their spacing.** `WorldPlacementStamper.StaticInstanceVisitor.ShapeDetail`
  feeds the existing `SdfInstanceRange.Detail` refinement regions from authored
  geometry: an eighth of a sweep's smaller radius, a quarter of a shell's
  thickness or a rounded profile's radius, a thirty-second of a warped shape's
  smallest half-extent, or a thirty-second of a cellular relief's wavelength. The tightest applies, at
  placement scale; a scoped creation keeps its tightest shape's spacing, or its
  noise relief's (`CreationNoiseDocument.DetailSpacing`: a quarter of the
  wavelength of the finest octave carrying a sixteenth of the amplitude) where
  that is finer. Refinement stops at eight cells a coarse axis, and a scoped
  creation's coarse lattice spans its bound sphere about the creation's origin,
  so a terrain sector centred on its ring's centre bakes at 2.8 m, not its
  declared 1.56 m (`docs/plans/valley-and-moons.md`).
  A bound alone misses thin strands and leaves warped corners under-resolved.
  In a static union a member's region is its own world-axis box where the
  stamper knows one (`SdfInstanceRange.Box`, `CreationStampEmitter.ShapeStampBox`:
  an unwarped, unfolded primitive), not its bound sphere's cube, which for a
  48 m strip spans the whole slab it hangs from; and a member whose declared
  detail is at or past the union's coarse spacing is no region at all
  (`SdfMeshPathBake.UnionFinal`), since the lattice already resolves it.
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
  (`worlds/debug/hall.puck`), so each is a flat group. Trim set into a large
  union is the same case: the hall's walls bake at 15 cm, so the cassettes at
  their window heads, set 2 cm and 5 cm back from the wall's faces, are a row
  per level and face of their own (`hall-cassettes-*`), each baking at 5 cm.
- **The coarse narrowing keeps the rows a thin plate lies between.** The
  narrowing (`SdfMeshPathBake.Refine`) keeps a sample within half the lattice's
  finest spacing of the surface, beside a sign change, or in a pair of rows a
  sheet thinner than the spacing passes between
  (`SdfSampledLattice.SheetsBetween`), and a final lattice all of one sign
  refines when that test finds a sheet rather than finishing empty. The pair
  test reads one line: the field falls toward the pair from both sides, the
  falls meet strictly below the surface (a sheet's meet below it by half its
  thickness; the falls either side of a solid's grazed edge meet above it), the
  field changes across the line by under three quarters of a step at the
  line's own slope (a line a step over a plate's top or a step out from a
  board's face falls or rises by the whole step across it), and neither sample
  is within the distance test's reach, so the pair moves the box only where
  the distance test loses the sheet. A pair's samples join the box with one
  cell about them, which keeps the sheet off the next lattice's edge, whose
  outermost pair no line can test. Loosened, each of these moved the box of
  groups that never needed it by a coarse cell and changed their final
  lattice's alignment (the ground slab's lattice went from flat to cubic and
  its clean mesh to 1.3 M rippled triangles; a canvas or sign scored four times
  worse or better by where its faces fell), so a change here is measured by
  `puck mesh audit --cpu` over the whole debug world against the previous
  build's. The pair test is what keeps a 6 cm strip lying between sample rows
  of a 1.8 m coarse cube in the box while its neighbour a row lands near is
  kept by the distance test (`SdfMeshPathBakeLawTests`, the plates law: four
  standalone 48 m strips); keeping samples within half a cell's diagonal
  instead fattens the converged box until the final lattice steps over the
  plates and the group bakes nothing. `hall.puck`'s strips ride each slab (`hallStrips`) as thin
  trim of the solid they hang from, which also keeps a level's lit and dark
  looks one geometry. The bake caps declared-detail refinement at eight cells
  per coarse axis.
- **A body's round sections declare their spacing.** Every pooled instance
  declares detail from its shapes' round sections (`WorldCreationInstances.BodyDetail`):
  a strand's thicker radius, a capsule's, a sphere's, an ellipsoid's middle
  radius (a superellipsoid's tightened by its exponent), or an elliptic prism's
  tightest bend (an eye socket's carved rim follows it), over three cells, so
  each cell turns about 19 degrees, under the contour's 25-degree crease split.
  At `SdfMeshPathBake.MinimumCell` (15 mm) any section under 4.5 cm turns past
  it and shades as flat facets: the Moth's fingers, hair locks and braid did.
  A flattened ellipsoid, a box, another prism or a cylinder declares nothing (an edge
  bends along its length alone, and the contour keeps it as a crease), and
  neither does a detail of 15 mm or more. A blend group takes its finest
  member's detail over its whole surface, so a member finer than 3 mm (a lid, a
  rivet) declares none rather than refining the plate or face it is grouped
  with; a shape on an instance of its own goes to 2 mm. Static placements keep
  `ShapeDetail` alone. `puck mesh audit --prototype <id> --cpu` prints each
  group's triangles and cell before a boot.
- **A pooled instance's detail spreads over its whole bound.** A dynamic group
  refines the box its detailed instances' bounds cover
  (`SdfMeshBakeGroup.DetailLow`, `OwnFinal`), never per member as a static
  union does, so one thin member of a blend group refines every member's
  surface. Pooled creations therefore take `BodyDetail`, not `ShapeDetail`,
  whose plate and rounded-profile terms put the Moth's whole suit at 2 to 3 mm
  and multiply her triangles about eighteenfold.
  A dynamic instance's text declares detail too: a pooled creation's embossed text is
  its own instance on a slot after its shapes' that holds the root's pose
  (`WorldCreationInstances.TextSlot`), bounded by its glyphs' reach and declaring their
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
  their own group. The bake's own pass block (`sdf-mesh-bake.interface.hlsli`)
  and its lattice rows are a sync pair with `SdfMeshBakeBlock.Write`.
- **A bake never reads the frame's transforms.** A dynamic group's box,
  lattices and points are slot-local, and `sdf-mesh-bake` reads every transform
  through the poses the group's geometry hashed (`SdfMeshGeometryBuilder.Poses`,
  staged after the batch's points, named by each lattice's fourth row,
  `SDF_MESH_BAKE_POSES`), so a mesh is a function of its geometry: a body moving
  while it bakes bakes whole, and two boots of one device bake alike. The
  contour is the mesh, already in that frame.
- **A dynamic body bakes at its own size.** The pool (`WorldCreationInstances.EmitOne`)
  and the rig catalog emit a body's geometry in its slots' own units, whatever
  its look's or `bodies.scale` register's scale, and every slot carries that
  scale (`DynamicTransform.Scale`; positions stay world positions, and field
  transforms stay rigid). The geometry builder reads another slot's offset from
  the group's slot in the group's units (`SdfMeshGeometryBuilder.RelativePose`,
  `UnitsOf`), as the settle watch does, so the key, the detail and the lattice
  are the full-size body's; the group's draw matrix takes the scale
  (`SdfMeshDresser.Dress`), and a member's vertex takes the draw's scale
  (`sdfMeshDrawScale`, the current and previous positions). A body crossing a
  portal pair of two sizes (Wonderland) bakes nothing and draws its full-size
  mesh scaled. Its material layers stay in world units at its scale, since the
  shading reads the slots' world poses. Never fold a body's scale back into its
  emitted geometry: a quarter-size body then keys new geometry and bakes four
  times coarser relative to itself. A slice (`world.slice`) walks the field
  through the frame's rigid transforms, so it shows a scaled body at its own
  size about its slots.
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
- **Every landed mesh is merged into its planar polygons** (`SdfMeshPlanarMerge`,
  called in `SdfMeshPathBake.Land` before the library stores it), so the store
  keeps, every pass draws and the irradiance trace reads the merged mesh. A
  region is a run of triangles across shared edges with one material, one member,
  one normal bit for bit at every vertex and every vertex within 2^-21 of its
  plane relative to its coordinates; a vertex only its region names, whose
  triangles close a ring about it, is removed and its hole ear-clipped again under
  exact float orientation tests, then flipped toward Delaunay. Every outline vertex
  stays, a straight run two merged faces share included (dropping it from both
  sides keeps the geometry but has the rasterizer snap the shared edge from other
  vertices, for 1.6% of the triangles left). Keep it that way: a change that moves
  or renormals a vertex, or drops an outline vertex, changes pixels. What a
  retriangulated face does change is depth interpolation's rounding, so where it
  meets a separately baked mesh (a plinth's embossed label, a wall's window frame)
  a pixel at the junction can take the other surface: five pixels in three
  settled normals captures of the debug world. The merge leaves curved surfaces
  alone (their vertex normals differ), so it removes about two fifths of the debug
  world's triangles, and its polygons' triangles are long and thin where a face's
  outline is dense: a consumer that bounds triangles by their boxes sees looser
  boxes than the lattice's quads gave, which the trace hierarchy answers by
  boxing such a triangle in pieces (below). The done line counts `merged=`
  triangles; `SdfMeshPlanarMergeLawTests` holds the outline, the area per
  material and normal, and no opened edge.
- **Every landed mesh then collapses into two coarser levels** (`SdfMeshCollapse`,
  `SdfMesh.Levels`, the decision in
  [rendering](../../../../docs/decisions/rendering.md)). A place is a position;
  its copies are the crease split's vertices there. A collapse moves one place's
  copies onto a neighbour's, each side of the collapsed edge taking that side's
  copy, so a level is indices and triangle materials over the merged mesh's
  vertices. Each (vertex, material) of the merged mesh is a sample tracked to a
  triangle; a collapse re-measures the samples about it against the triangles
  that stay (point to triangle in doubles, plus the interpolated normal's angle)
  and is refused when one has none within the level's bounds
  (`CellShares` of the contour cell, `NormalDegrees`). Every crease and material
  edge vertex and edge midpoint is a line sample tracked to the feature edge it
  lies nearest, re-measured point to segment when a collapse moves that edge: the
  surface samples alone let a slide fill a notch in a face's outline (the notch's
  vertices lie inside the filling triangle, at no distance), which drew surface
  past a raised plate's outline several pixels out. `SdfMeshLevel.Error` is the
  largest distance the surface samples, the line samples and the triangle bound
  below measure (raised to the finer level's where
  re-tracking measures a coarser level nearer). Places sort per pass into free (at most one
  crease or material edge, ending there), sliding (two copies: only along the
  crease between them; one copy on material edges: along them) and locked
  (three or more copies, an open or non-manifold edge, a fan that is not one
  ring); a refusal also guards the link condition, a fold past `FlipCosine`, and a
  face turning to the other side of its vertices' normals. A pass locks only the
  places a collapse moved triangles onto, since those places' lists miss them;
  the passes take about half as many collapses each, about twelve a level. A
  third guard bounds each new triangle: its centroid and edge midpoints are
  measured against the old fan's same-material triangles, and each triangle
  carries the largest such distance it inherited, so a collapse cannot cut a
  triangle through a thin rod the samples straddle. On the debug world's stored
  set it keeps 11% of the triangles at the first level and 6% at the second, for
  9% more stored and region bytes. Conservative triangle-box distance bounds
  skip exact measurements that cannot improve the result, and the old fan's
  coordinates are cached per proposal: the same 1,406 meshes take 206.1 CPU
  seconds instead of 215.7 with identical indices, materials and recorded errors,
  spread across `Land`'s tasks. Glass skips collapse because its pass uses only
  level zero. The done line counts `levelled=` pieces and
  the triangles every mesh draws at `level1=` and `level2=`;
  `SdfMeshCollapseLawTests` measures each level's contract by brute force.

## The mesh region

- **The compact store expands to the fastest GPU layout.** `SdfMeshStoreCodec` chooses bounds-relative
  16/24-bit positions only when every decoded vertex moves by at most `BakeCell.Length()/1024`;
  otherwise positions remain floats. Every chunk shares its group's grid and width, so shared seam
  vertices decode bit-identically. Unit normals use signed oct16 pairs; non-unit authored normals
  retain their magnitudes. Indices use 16 bits through 65,536 vertices and materials use bytes when
  their values fit. Delta-predicted attribute byte planes are zlib-compressed. Decode reads planes
  directly from the decompressed payload into the CPU arrays, avoiding per-plane byte copies.
  Both backends draw six floats per vertex and 32-bit indices/materials through `frame/sdf-mesh.hlsli`.
  `SdfMeshLibrary.TryLoad` coalesces a set entry's decode across groups and keeps its result with the
  library's bounded meshes. A failed read is removed so a later derivation can be loaded.
  Sound entries can also record that a bake found no surface: a successful store read with a null
  mesh is an empty result, not a miss. `StoreEmpty` persists it under the bake identity so fully warm
  boots need no bake dispatch or bake-pipeline creation. Offline consumers skip its absent surface.
  The owner's mesh-compact correction: warm decode must be no slower than the old load, and a
  smaller GPU region is welcome only when mesh and cascade GPU passes are no slower. Measure
  repeated `world.gpu-timing` windows at the same pose; do not infer speed from region bytes.
  Record the presentation target and GPU clocks beside an A/B: the device can downclock at 60 Hz,
  so compare both variants at the same target and frequency range. Keep ordinary-target readings too.

- **Glyph-bearing groups retain their outlines at every distance.**
  `SdfMeshGeometryBuilder.ContainsGlyph` recognizes `ShapeBlend/Glyph` in static, dynamic and unowned
  world groups. `SdfMeshCollapse` caps crease and material-edge error in those groups at the largest
  bake-cell dimension divided by 8 while face interiors use the ordinary level budget; a mesh
  without detectable features retains its base surface. Corners and open edges stay fixed.
  Whole-group collapse exclusion also prevents large scenery sharing a glyph group from coarsening,
  so its cascade cost must be measured before choosing it. A geometric distance budget alone does
  not protect text counters and thin strokes: the lift's lit zero lost its hole near 21 m.

- **Meshes keep their words; draws repack only their records.** The tables
  place each mesh whole in an arena past a reserve of draw records
  (`SdfMeshRegionArena`), where it stays while a draw names it, so
  `StageMeshRegion` (`SdfWorldTables.MeshRegion.cs`) writes the draw records
  (`SdfMeshRegion.WriteDraws`) and only the meshes the arena newly placed. A
  layout that moves placed meshes when one lands (sections by attribute) made
  every landing rewrite the whole region. Placed meshes land through the
  background GPU work, never a frame's upload (`SdfWorldTables.MeshLanding.cs`):
  jobs of at most `SdfGpuWork.UploadChunkBytes` under its byte, host-time and
  GPU-share budget, nearest the views first, each staged into the job's own slot
  of the region (`MeshLandingSlot`, past the upload ring's) and copied in the
  job's command buffer. A mesh leaves `m_meshesOwed` only when every job carrying
  its current placement has completed, and the landing filter in
  `StageMeshRegionCore` is the one place its draws are held out until then; a
  relocation, a moved mesh or a trim drops that mesh's progress, so a job still in
  flight for its old words lands nothing. A frame that stands is staged again
  when a mesh lands (`StageLandedMeshes`), and a lockstep frame waits for the
  landing (`SdfWorldPasses.WaitingOf`). `world.residency`'s
  landing line reads the frame uploads' staged mesh bytes (`frame-staged-most`)
  and the landing jobs. A grown region takes the words it held. A
  list past the draw reserve doubles it and moves only the meshes the grown
  reserve takes (`SdfMeshRegionArena.Moved`), written that frame: relocating
  every mesh for it rewrote and compared the whole region in one frame. The
  region's host copy holds the draw reserve alone: a placed mesh's words are
  owed and the flush writes them from the mesh's own arrays
  (`SdfWorldTables.WriteSourcedMeshWords`), zeros in words no placed mesh holds.
  A placement names absolute words (`SdfMeshRegionMesh`); a new per-draw field
  belongs in the record, never in a mesh's words. The record's layout is
  `SdfMeshRegion`'s, a sync pair with `frame/sdf-mesh.hlsli`
  ([sync pairs](sync-pairs.md#engine-buffers-groups-and-bindings)).
- **One region holds every resident mesh, up to
  `SdfWorldTables.MostMeshRegionBytes` (`GpuRegion.MaxByteCount`, just under
  4 GiB).** It grows to its need plus an eighth, rounded up to a 16 MiB granule
  and capped at that ceiling (`SdfWorldTables.MeshRegionGrownBytes`, a pure
  function), so it reads fuller than a doubling would; `world.budget` prints the
  bytes the staged draws use (`MeshRegionUsedBytes`) beside the bytes held and
  that ceiling. A frame that needs
  more than the ceiling throws `InvalidOperationException` naming the need and
  the ceiling. The ceiling is the whole uints below 4 GiB less the staging
  buffer's header and run table, because a staged region's staging buffer binds
  as one storage view and Vulkan states a view's range in a uint32. `GpuRegion`
  counts bytes in a `long` and words in an `int`: a mesh's byte offset is
  `((long)VertexWord) * sizeof(uint)`, `Contents` refuses past `int.MaxValue`
  bytes, and a piece of the host copy is read through `ContentsAt(offset,
  length)` (`StageMeshMotion` reads only the draw records that way). Host writes
  go to a buffer in pieces of at most 1 GiB. No run of the debug world reaches
  2 GiB, so `GpuResidencyLawTests` holds a 3 GiB region written and flushed
  through buffers that record their writes, and `SdfWorldTablesUploadLawTests`
  holds the growth arithmetic past 2 GiB.
- **A jump in planned groups is usually a population change.** Every pooled
  body adds one dynamic group a slot, so a body that starts wearing a large
  creation multiplies `groups=` and `dynamic=` in the `[sdf.mesh-bake] start`
  line. A `state.body` register selector whose case names the register's
  initial value selects every body: the suit selector once dressed every chess
  piece and the board cursor as the Moth, and the debug world planned 7,322
  groups, 6,880 dynamic, in place of about 1,286 and 844. The validator refuses
  such a case (`WorldDefinitionValidator.ValidateRowSelector`); `body.rig <n>` names the creation a body wears.
- **A group a later program drops before it lands is never stored,** so it bakes
  again on every boot. A boot whose second `[sdf.mesh-bake] start` line plans
  fewer groups than its first, and whose next boot's first start still reads
  `baking=` above zero, presented a document its first tick then changed. A
  placement's response mask is settled into the booting document
  (`WorldStateSettlement.SettleResponses`), so the debug world's raised blinds
  never reach the first plan; look for another tick-one change (a rule, a deal)
  when the line comes back.
- **The bake is background GPU work under a budget.** The residency's tables
  own one `SdfGpuWork`: a ring of `SdfGpuWork.SlotCount` slots, each a command
  buffer, a submission fence, a readback and two timestamps, which its bake
  client (`SdfMeshBakeGpuWork`) fills with one batch a slot (`TryTake`: up to
  `SdfWorldPackage.MeshBakeBatch` lattices, 240, whose rows fill the bake pass
  block to the portable 16 KiB uniform limit; `MeshBakeMaxSamples` samples,
  2^21, 32,768 groups of 64, under the 65,535 a dispatch axis allows; and
  `SdfMeshPathBake.BatchTaps` field evaluations, a gradient sample counting
  five). A batch is the mask dispatch, the sampler dispatch and the samples'
  copy back, submitted on its slot's fence; the kernel finds a sample's lattice
  by binary search over their first samples. `Pump` runs on the frame thread
  (the GPU layer records and submits on that thread alone) at the top of each
  frame, before any residency prepares, and after each upload: it hands every
  job whose fence signaled to its client, which copies and decodes the samples
  on the bake's pool and releases the slot (`SdfGpuWorkReadback.Release`), and
  refills every released slot while the budget holds. The budget is
  `SdfGpuWork.GpuShare` of the GPU's time, charged by each job's own
  timestamps (a client's estimate, `NanosecondsPerTap`, until they land or on a
  queue without them), banking at most a short spell while idle. A job binds the
  latest upload's World set only while nothing it binds has moved since that
  upload and the tables' uploaded program is the bake's
  (`SdfWorldTables.BindsBackgroundWork`, `BackgroundProgram`): between a pack
  and its upload the slot's buffers are owed contents not yet sent. Every
  lattice is chunked to fit `SdfMeshPathBake.LatticeTaps` (final lattices in
  slabs, material and gradient, fine and material points in chunks). The done
  line's `gpu-work=[…]` reads jobs, their measured GPU time, the wall time some
  job was in flight and the GPU's share of it, the most in flight, the pumps,
  the pumps the World set could not be bound in and those the budget held;
  `world.gpu-timing` does not see the work. A slot's readback and the bake's
  per-slot point table, sampled table, masks and samples grow to its largest
  batch and go once a free slot has not needed them for `SdfGpuWork.IdleSeconds`
  (`ISdfGpuWorkClient.ReleaseIdle`, called only where `Refill` would reuse the
  slot), so a settled bake holds none; `world.residency`'s `gpu-work` line
  reads what remains. Gradients are measured only at the
  samples `SdfDualContouring` reads a normal at
  (`SdfSampledLattice.GradientPoints`): a cell's normal reads its own eight
  corners, so no other sample needs one.
- **A mesh never depends on how its lattices are cut.** A slab keeps its final
  lattice's origin and names its first plane in the lattice's mode word, so its
  samples lie at `origin + (x, y, slab + z) * cell` exactly as the whole
  lattice's would; a slab origin offset by `slab * cell` moves samples by an ulp
  and changes meshes whenever the share changes. `UnionFinal`'s plane limit is
  its own constant (`UnionPlaneSamples`), not the share. Check a change to the
  bake's chunking, scheduling or CPU speed with `puck mesh audit --cpu --group
  <shapes>`: its done line's samples, triangles, vertices and level counts must
  not move. Stages that hold a worker past two seconds log
  `[sdf.mesh-bake] slow … stage=…` with the group and its lattice, and the done
  line's `kinds=` and `cpu=` split samples by stage and summed worker-stage
  elapsed seconds. `Account` uses `Stopwatch`: these totals include descheduling,
  GC suspension and waits inside a stage, not just consumed processor time.
  Read them beside a .NET sample profile and allocation events; queue wall-time
  comparisons through `puck measure request` for the quiet window.

## The library and the store

- **A batch answers the bake that took it.** `SdfMeshBakeBatch.Bake` names it,
  and the slot that dispatched it holds it until its readback is released, so a
  view that leaves the frame or follows another residency never strands a bake:
  no view dispatches or reads back a batch. A slot is reused only after its
  fence signaled and its client released the readback (`SdfGpuWork`, the one
  owner of its slots), and a slot's inputs and outputs (its pass block, point
  table, instance masks and samples) are written only while it is free. A batch
  abandoned (a recording that failed, a disposed service) returns its lattices
  to their groups, which dispatch them again, and counts its samples as
  `discarded`; `Answered` settles a batch once.
- **Readback ownership belongs to a job, never a recycled token.** Every
  completion gets a new `SdfGpuWorkReadback`; its gate serializes reading,
  release and revocation. A stale or concurrent second release cannot free
  the next occupant, and disposal waits for the reader before freeing native
  storage. `SdfWorldTablesWorkLawTests` exercises those interleavings and
  recording, submission, completion and device-loss failures. A throwing
  completion never accepts ownership. Keep a submitted job tracked until its
  timestamps have been read; a failed submission may have executed commands
  without a usable fence, so disposal drains the device before abandoning it.
  `DirectXSubmissionFenceLawTests` drives the actual fence through native
  stand-ins at rollover: renew a drained fence before `UINT64_MAX`, which
  `GetCompletedValue` reserves for device loss, never signal that value or
  wrap to zero.
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
  of it at another selector finishes at once instead of baking again. A dynamic
  group whose geometry the library lacks but whose form it holds (a body
  crossing into another world mid-stride, or wearing a creation another body
  wears) takes that form's latest skinned mesh (`SdfMeshLibrary.TryTakeForm`)
  and watches its members against the poses the mesh's skin records
  (`SdfMesh.Members`), so it bakes only once it settles away from them. The
  library holds every mesh, empty result and form under the identity of the
  bake that made it, and a program keeps a group (`SdfMeshPathBake.Keeps`,
  `Renumbers`) only while that identity stands: after an applied update to the
  bake's code or a reloaded bake kernel, every group bakes again, drawing its
  earlier mesh until the new one lands.
- **A material id shift renumbers every later mesh.** A group whose material
  ids moved takes its mesh renumbered (`SdfMeshLibrary.TryTake`), a new
  `SdfMesh` the mesh region lands again word for word through the background
  work, with its draw held out of the frame until it lands. Program material ids are dense in
  registration order, so a placement that stops or starts showing a palette
  shifts every group after it: a responding static row registers the palette
  of every creation it can show (`WorldPlacementStamper.EmitStatic`,
  `ResponsePaletteLawTests`), and a creation with nothing to draw emits no
  instance, so a response swaps one row's materials alone. A body leaving or
  entering a world adds or drops its palette ahead of every later body's: a
  skinned group of the same form and material count at its slot keeps its
  mesh renumbered (`SdfMeshPathBake.Renumbers`), so the bodies after it bake
  nothing, though they stand off their baked pose (the renumber law in
  `SdfMeshPathBakeLawTests.Rebuild`).
- **The World's library persists across runs** (`SdfMeshStore`, the `meshes`
  device cache), keyed by geometry and the bake's identity
  (`SdfMeshBakeIdentity`): the hashes of the mask and bake kernels' bytecode
  and the code the bake runs (`SdfMeshBakeClosure`). The kernels' source
  closure is `passes/sdf-bake-world.hlsli`: their own interface
  (`isa/sdf-mesh-bake.interface.hlsli`, `SdfWorldInterfaces.MeshBakeParameters`:
  a pass block of `SdfWorldPackage.BakeValues` alone, the masks, samples and
  counters, and the World group with the field's tables first), the field
  interpreter, the field's generated vocabulary (`isa/sdf-isa.hlsli`) and the
  work counters, stamped with the field's fingerprint
  (`SdfIsaFingerprint.Field`). A lighting, screen or view module, a renderer
  constant (`isa/sdf-render.hlsli`), a world pass-block value or resource, or a
  World table after `FieldTables` is outside it and leaves their bytecode byte
  for byte as it was on DXIL and SPIR-V; an edit to the field, its generated
  vocabulary, the encoding or the bake kernels moves it. The code part is read from
  the build's assemblies through the shared `Puck.Assets.CodeClosure` walker;
  `SdfMeshBakeClosure` supplies the bake's roots and transport boundaries, while
  compiled-world chunks and generated machine images use the same walker with their own roots.
  Positive production entry methods in `SdfMeshBakeClosure.BakeRoots` include
  the bake's constructor/defaults, `PrepareFrame`, planning, batch taking (`TryTake`) and the point table's
  layout (`SdfMeshBakeBatch.PointRuns`), the sample answer (`Complete`), `SdfMeshBakeBlock.Write`, persisted-product normalization
  (`SdfMesh.NativeCopy`) and the store codec. Their graph reaches group/geometry planning, lattice refinement,
  contours, material cuts, skin and levels. It follows every call, delegate, override of a type the bake constructs
  and implementation of an abstract member it calls across the `Puck` assemblies `Puck.SdfVm`
  references, and hashes each method's IL with its tokens written as the
  members they name, its locals, exception regions and loaded field data, and
  each named type's layout. A value type's layout counts whole (the bytes its
  code moves); any other type's counts only the fields the closure's code loads,
  stores or takes the address of, so a field the bake never reads (a
  presentation-only flag on `SdfFrame`) leaves the identity as it was, and a
  record the code merely copies with `with` has its synthesized clone and copy
  constructor hashed by signature (they read no field). A record the bake
  constructs counts its whole layout, since its synthesized equality and
  printing read every field. `SdfMeshBakeClosureLawTests` pins both ways. It
  stops at `SdfMeshLibrary` and `SdfMeshStore`,
  which hold meshes, and at `GpuRegion` and `GpuUploadRuns`, and at the runtime. The
  background GPU work (`SdfGpuWork`, `SdfMeshBakeGpuWork`), which carries a batch's rows to the device and its samples
  back rather than choosing them, is outside it: no root reaches it. An edit to `Puck.Maths` code the
  contour calls re-bakes. The standalone `SdfMeshDresser`, `SdfMeshHistory`, `SdfWorldPassRecorder`, `SdfWorldTables`,
  `SdfFrameBlock` and `SdfMeshRegion` are outside the CPU closure, including type-granular applied updates;
  `SdfMeshBakeBlock` and the shared `ShaderParameterWriter` remain inside. Comments and checkout paths do not
  change the key, and worktrees of one bake share one set. Appearance is outside the key by construction: the
  closure's program root is `SdfProgram`'s only constructor, the field derivation, which takes no material or screen
  value, only an `SdfAppearanceOutline` (material count, glass materials, declared screens, the appearance section's
  size). `SdfProgram.Of` runs it and then attaches the `SdfAppearance` and packs its materials and paints into the
  stream's tail; the program's appearance is unattached while the field constructor runs, so a read of it there throws
  rather than joining the key. The kernels follow the same split: the stream is field first (`SdfProgram.FieldWords`),
  the field interpreter finds the bound table at `dataOffset + 2 * instructions`, the material and paint strides and
  lanes live in `sdf-render.hlsli`, and the field fingerprint describes the encoding over `FieldWords` alone (the
  description leaves out a probe input that moves no described word, so a new material input on the probe's material
  call moves only the whole fingerprint). An edit to
  a material's values, a paint, a screen surface or `SdfAppearance`'s code leaves the identity standing (`puck mesh
  identity --against` reports the same identity and kernels); the one material fact beside places that shapes a mesh,
  glass, enters each glass group's geometry (`SdfMeshGeometryBuilder.AppendGlass`). The diagnostic slice, frame/input
  types, admission and settle accounting remain conservatively reached.
  A new member matters if a positive entry or its callgraph reaches it. Code the bake
  reaches only through reflection or a runtime callback on a type it does not
  construct is outside the walk: add a root to `SdfMeshBakeClosure` for it.
  Finish formatting before measuring a new cold set: argument ordering can change IL and therefore
  the computed bake identity even when the source rewrite preserves semantics.
- **Dressing is outside derivation.** Top-level `SdfMeshDresser` owns retained topology, posed draws, clipping,
  pick maps and draw claims. Its constructor binds one bake; dispose it before retiring that bake or its library.
  `SdfMeshPathBake.PrepareFrame` is the one locked door: restart/reconcile the frame, update the single copy-membership
  index shared by demand and dressing, prioritize and settle, then return a stack-bound `FrameAccess`. Its consumer
  claims the landed meshes and publishes draws/picking before acknowledging that access's captured product revision;
  disposal releases the gate without acknowledgment. Every mesh replacement, including a same-count or empty result,
  advances the revision. `Stage` chooses and slices the exact point/pose rows; its caller uploads the returned span
  through the generic region transport. No bake entry calls the dresser. This is a type boundary as well as a call
  boundary: applied code updates report types, including declaring types of nested helpers, so a partial/nested dresser
  would still invalidate the bake. `SdfMeshBakeClosure.Reads` includes producing types and excludes the standalone dresser,
  draw history, world tables, pass recorder and the view frame block; unknown applied-code updates remain conservative.
- **A miss names its cause.** Each identity's parts and code members are
  recorded beside the entries (`<identity>.identity`, the 64 most recent kept),
  and a start that bakes groups logs one line: `[sdf.mesh-bake] miss … identity
  … unchanged: geometry new to its set of S entries (N entries in K sets, B of C
  bytes)` or `… unchanged: its set was evicted, or it never stored one (…)`, or
  `identity … new since <earlier>: the code, K members (Puck.Maths
  1: Puck.Maths.VectorFunctions.IsFinite) changed`. `puck mesh identity --build <dir>
  --against <dir>` prints the same comparison for two builds with no boot, and
  its value is the key a cache of the store is saved under.
- **The store keeps whole sets.** Each identity's entries are one set, in a
  directory named for the identity (`SdfMeshStore.SetDirectoryOf`). The debug
  world's set is compressed with its mesh codec; the store keeps `SdfMeshStore.DefaultCapacityBytes`
  (8 GiB), shared by every World and worktree on the machine, a set at a
  time (`CacheRetention.EnforceSetsOnWrite`): a set's last use is its most
  recently read or written entry's, and a write removes whole sets, least
  recently used first, never its own. A set a World is baking or loading is
  never cut short by another lane's writes, so concurrent lanes on different
  identities keep each other's complete sets until newer sets fill the capacity,
  and a set larger than the cap alone keeps its most recently used
  entries. An entry left directly in the directory is a set of its own and ages
  out first. A boot that bakes nothing shows `done … loaded=N` with
  `dispatches=0`. A program that arrives while groups are pending and takes
  every group from the library reports the started bake `done` at once
  (`SdfMeshPathBake.Restart`), since no landing follows to report it.
  Any replaced group invalidates the dressed draw list, including a cache hit
  with the same group count: no later bake callback will repair a stale draw.

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
  `meshIrradianceLevels`. The probe layout, key, toroidal slot, normal bias and
  probe weights are a sync pair between `SdfMeshIrradiance` (its CPU feedback
  read included) and `shade/sdf-mesh-irradiance.hlsli`, which only the views
  kernel compiles. The trace reads static mesh draws through
  `SdfMeshIrradianceScene`; never route it through the field interpreter. The
  scene is two-level and keyed by each mesh's position and index buffers, which
  its triangle tests read in place (a geometry holds only its hierarchy and the
  leaf order of its triangles), so a static-draw change builds only geometry new
  to the previous scene: a slow `[sdf.mesh-gi] scene` line with a large
  `built-triangles=` means new geometry, not the trace. `puck mesh scene --world
  <file>` times a build and a one-draw change over the stored meshes with no
  device, and `world.counters sdf.mesh-gi` counts builds. A triangle whose box's
  surface passes 64 times its area, a long thin one a merged polygon leaves,
  enters the hierarchy as up to 64 pieces each boxed tight, every piece naming
  the one triangle: unsplit, the merged debug world's trace took 10.6 CPU seconds
  a sweep where the unmerged took 6.9, and split it takes 7.3, with the same
  hits. The
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

`RestScene.Compose` reconciles its `WorldTextCatalog` before emitting the frame. A catalog object
without that call silently omits text and makes the audit miss every glyph group, even when the
definition carries prepared font assets. Keep this offline scene path aligned with the live emitter.

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
