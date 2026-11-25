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

Two things a fresh `cacheDir` does not reset, and one lockstep hides
([the wall-time measurement](../../../../docs/plans/field-programs/research-bake-time.md)).
The driver's own shader cache decides the opening wait: the bake's GPU client is
created with the residency's tables (`SdfWorldResidency.EnsureTables`), after
every one of its pipelines compiles, so a build whose shaders the driver has
not seen waits 15 to 35 seconds before its first lattice and a repeat waits
under one. Lockstep and held frames drive background work without the GPU
budget (`SdfGpuWorkBudget.Observe`), so a lockstep bake measures the worker
pool's pace and a free-running one the budget's, which was three times slower
when the frame's GPU time passed its period. To trace a bake from process
start, set `DOTNET_EnableEventPipe` with an output path and provider config in
the World's environment rather than attaching after the start; the runtime's
allocation events (verbose level) slow allocation-heavy stages, so time a bake
without them.

For timing, `bench startup --bake` pins those clocks with `--paused --lockstep`.
Lighting convergence can delay the readiness barrier after the bakes finish;
allow for it in `--timeout-seconds`. Its per-sample `timeline.log` keeps
the observed timestamp and stream of every stage/job line. Read those spans beside
a sampled thread-time trace; the `cpu` stage totals are summed worker elapsed time,
including waits and suspension, not processor time. With a worker per logical
processor sharing cores they overstate what a stage costs alone, and they swing with
whatever else runs. Attach `dotnet-trace collect -p <World pid> --profile
dotnet-sampled-thread-time` during the bake and count only `CPU_TIME` leaves: it
splits the World's processor time across stages and the work beside the bake (the
desktop capture's readback scaling, the frame). A CPU change to `Land` is measured
load-independently by replaying the group meshes `Land` receives through
`SdfMeshChunks` on one thread and counting that thread's cycles
(`QueryThreadCycleTime`); hash every level's indices and errors to show an
equal-output change changed nothing.

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

- **A grid lattice skips the blocks its bounds clear.** A coarse search's
  lattice and a final lattice are bounded once on the pool before dispatch
  (`SdfMeshPathBake.QueueBounds`, `SdfMeshPathBake.Bounds.cs`): blocks of four
  samples whose CPU-field enclosure, one cell past their samples, clears three
  cell diagonals are filled on the host with the enclosure's nearer end, and the
  rest dispatch as a block lattice (`SdfMeshBakeRequest.Blocks`, mode 1). Every
  lattice reader judges nearness by small values or sign changes across a cell,
  so a skipped sample never changes what it reads; a new reader that reads a far
  sample's magnitude (a distance-weighted average, an SDF-valued blur) breaks
  that and must sample the lattice whole. The done line's `bound=[…]` counts the
  lattices bounded, refused (a field the CPU cannot read), walks and skipped
  samples.
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
  Adjacent fine cells in an X row reuse their common face's four sign bits. The
  row reads its first face, then one new face per cell, after `CloseSheets` has
  fixed every sign; it retains the original cell and edge enumeration order.
- **Index a collapse fan once.** `Collapser.Kind` indexes outgoing edges and
  pairs each with exactly one incoming edge before walking their permutation.
  `OneRing` must visit every triangle and return to its start: several disjoint
  cycles are not a manifold fan. The index is scratch for that invocation, with
  every endpoint it can read cleared before filling, never a cache across collapses.
  Keep feature penalties in edge order, including the prefix before a malformed
  edge locks the fan: a locked place can still be another collapse's target,
  whose cost reads those quadrics.
- **Reuse a collapse's source fan until commit.** `SdfMeshCollapse.Collapser.PrepareFrom`
  lays out the unchanged source triangles once for all attempted neighbours.
  A refused neighbour mutates no source geometry; the first accepted neighbour
  ends that fan's attempts. Its reverse edge witnesses share that lifetime:
  a midpoint's nearest old surface and inherited error depend only on its two
  places, material and the prepared fan. `PrepareFrom` clears the witness map,
  and `Pass` stops at its first commit; never retain those answers across it.
  `MeasureReverse` computes a maximum of point errors: once a point has a
  witness no farther than the maximum established by preceding points, a nearer
  witness cannot change the result. The cached distance is an upper bound plus
  the next old triangle to visit, not necessarily the exact nearest distance.
  A tighter later target resumes that search; only a completed search may raise
  the maximum. Shared edge midpoints precede the unshared centroid.
  Forward checks measure the removed vertex's samples first, then every other
  tracked sample. An original vertex retained by its tracked triangle witnesses
  its own position, normal and material exactly; the other samples choose their
  nearest admissible witness. Ordering avoids later work when an early sample
  refuses the collapse. A nearest point already beyond the current distance
  limit needs no interpolated-normal calculation.
- **Retry refused edges only after their inputs change.** `Collapser.Pass` keys
  each rejection by its directed edge and the revisions of what the refusal read
  (`Collapser.Outcome`): the collapsing place's fan always, the neighbour's fan
  only for a link refusal, and the level's budget only for a measured one, so a
  shape or link refusal stands into the next level. Every other check reads the
  place's own fan, its tracked and line samples and the neighbour's fixed
  position; a new check that reads the neighbour's fan returns `RefusedLinks`.
  `Commit` is the one mutation door for topology, surface and line samples, and
  inherited bounds; it stamps every endpoint of the old fan, including deleted
  triangles. The cache only skips failed attempts; every accepted collapse still
  passes all checks. `PrepareFrom` lays out each source fan's corners, link
  stamps and geometry once for every neighbour tried; its tracked samples and
  lines are listed by the first neighbour that needs them. A `Dictionary<long, …>`
  keyed by two packed places takes `PackedIntPairComparer.Instance`: long's own hash folds the halves,
  and neighbouring places collided until the refusal lookups dominated a mesh.
  A faster query or a different witness assignment still needs the field audit:
  tiny arithmetic and ownership changes can alter later collapse choices and
  uncover surface that the sample-to-mesh bounds alone do not detect.
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
  A thin sheet's cells take a finer split where the ordinary one leaves room
  (`SdfMeshPathBake.ThinSplit`), and so does every measured cell sharing a face
  or an edge with one: a sheet runs past the last coarse line that caught it,
  and a coarser stride beside it steps over the sheet between the fine samples
  the two cells share, ending it in a cap inside the solid whose merged vertex
  sits between the sheet's faces (the range windows' rims).
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
  slab. The field along such an edge need not change sign where the
  interpolated end says it does (a coarse cell's corners interpolate the two
  wedges of an XOR's pinch as one solid), and the search then ends against
  that end, off the surface: a crossing whose bracket still ends at an
  interpolated value (`CrossingRoot.Bracketed`) and whose field there does not
  read zero gives no plane (`Lattice.TryCrossingNormal`). Its measured
  gradient is the nearer face's, and a plane through the lattice point along it
  stands up to a fine cell off that face and keeps a merged piece's planes from
  meeting at its crease. Only samples displaced by `CloseSheets` keep their lattice
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
  splits along the diagonal that faces out as its lattice edge says (next
  bullet) and, where both do, the one that turns least from its non-crease
  corners' normals, chosen again after the final field correction moves
  vertices, and
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
- **A contour holds the facing its lattice gave it.** A dual contour's
  connectivity is the lattice's signs alone: one quad per sign-changing lattice
  edge, joining the four cells around it, and that edge runs from inside the
  solid to outside. Its direction is each quad's facing with no estimate in it
  (`SdfContour.QuadAxes`), and `SdfContourFacing` is the one place a triangle is
  judged against it: a triangle faces out when its area normal has a positive
  component along its quad's edge, and one lying within fifteen degrees of
  edge-on to that edge (a quad squashed into a face that passes through the
  edge) is judged by its corners' patches, the sum of each cell patch's own
  sign-changing edge directions (`SdfContour.Patches`,
  `SdfContourFacing.Patch`). Vertex normals and a triangle's own earlier facing
  are estimates a misplaced vertex has already turned, so no stage orients by
  them first. Four things hold a contour to it:
  - *Placement keeps a vertex in the region its quads allow*
    (`SdfDualContouring.Within`). The four cells around an edge lie in the four
    quadrants about its line, and a quad faces out while each vertex keeps to
    its own quadrant, so an edge bounds its cells' vertices only along the two
    axes it does not run along. A vertex may therefore reach a feature past its
    cell (`FeatureReach`) only through a face its patch does not cross; past a
    crossed face it lies among the cells its own quads join it to and turns
    them over, so it clamps instead. A vertex no feature pins also keeps
    `CrossedClear` of a cell clear of the faces its patch crosses (`Clear`): on
    such a face the quad it shares with the cell across it has no width, and
    which way its triangles turn is rounding.
  - *The settle* (`SdfDualContouring.Settle`, after the hand-off and the merge)
    finds every quad neither of whose splits faces out and returns the vertex
    whose return fixes it, or all of them, to its crossings' mean, which lies in
    its own cell. A merged piece's vertex stands for several cells and has no
    region of its own, so the settle is all that holds a transition fan. A
    piece whose vertex the settle returns and whose planes meet along a crease
    past its block (the faces of a wedge whose edge the neighbouring measured
    cells hold) has no point on both faces, and its returned vertex lies inside
    the solid, where the field check moves it onto one face and turns the other
    face's triangles against the field; the merge runs again with each such
    piece split into one piece per face (`SdfDualContouring.SplitAtCreases`).
    A piece the settle leaves alone keeps its one vertex: splitting every
    creased piece, or every piece holding two opposite sheets, turns more
    triangles on the XOR and the range windows than it fixes.
  - *The split* (`SdfSampledLattice.SelectDiagonals`) takes the diagonal whose
    triangles face out where only one's do, before any normal is read. Two
    quads that hold one pair of vertices as opposite corners (a merged vertex
    stands for several cells) would both split along that pair, an edge four
    triangles hold; the later quad takes its other diagonal.
  - *The field check's moves* (`ContourProjection.Move`) are clipped before a
    triangle that faces out stops doing so, beside the clip against its
    original facing.
  To measure a change here, count a contour's quads with no split that faces
  out (`SdfContourFacing.Splits`) after the settle, where a contour without
  merged pieces has none, and again after the field check. What this does not
  hold: a triangle can face out by every lattice fact and still be turned past
  a right angle from the field's gradient. The audit's remaining flips are of
  that kind: a sliver between two rows of vertices a few hundredths of a cell
  apart along a crease, a web across a junction of two creases, a vertex its
  planes put off the surface where the field check refuses to move it, and a
  face narrower than a cell between two creases (the Moth's shin blades). They
  are [open](../../../../docs/plans/open-items.md#bake-detail).
- **Vertices a cell cannot tell apart are one vertex.** Once the field check has
  moved its vertices, a contour is finished in one place, `SdfContour.Finished`,
  which both the bake's split and `ContourLevel` call: it welds every quad edge
  shorter than `WeldCells` of the finest cell (`SdfContour.Weld`), shortest first,
  then selects diagonals and splits creases. A row of cells beside a crease puts
  its vertices a few hundredths of a cell off the crease's own row, and several
  cells crowd the point where two creases meet; the strip or web between them is
  slivers whose facing is rounding. The crease's vertex (`Features` and not
  `Beside`) keeps its place and takes the other. A weld is refused where it would
  join a quad's opposite corners, break the link condition (a neighbour the two
  share that no triangle between them holds), leave a quad that faced out along
  its lattice edge with no split that does, or turn a surviving quad by more than
  `WeldTurnCosine` (a small quad beside the move folds against its neighbours,
  as on a closed sheet's rim). A quad the weld closes becomes a triangle or goes.
- **A crossing's gradient within a tap of a crease is a blend of the faces
  meeting there**, and its tangent plane is neither face's. A lattice plane that
  falls within a few tenths of a millimetre of a flat face (the narrowed box
  puts one there) reads such planes at every crossing along the rims on that
  face, and the cell's least-squares point is drawn to the face it shares with
  its neighbour. Read `puck mesh audit --cpu`'s clusters by where the lattice
  planes fall before blaming the shape.
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
kernel reads in place (`SdfMeshBakeGpuWork`), which the service owns and
reserves (`SdfGpuWorkRecording.HostBuffer`), grown in 4 MiB granules once the
slot's last job is released (`GpuBufferSizing.Granule`), and bound through the slot's
own World set at the bake's point member, where the tables' World sets bind the
unused-member buffer (`SdfWorldTables.BackgroundWorldSet`).
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
- **A group's bound stops at the frame's far distance.** The plan clamps each
  instance's bound to a cube of the far distance about the instance's centre
  (`SdfMeshBakeGroup.Clamped`), and a world that authors no `render.farDistance`
  takes `SdfFrame.DefaultFarDistance`, 40 m. A creation placed at the origin
  whose shapes stand hundreds of metres out (a valley wall sector) then bakes
  empty on every backend and in `puck mesh audit --cpu` alike, and its entry in
  the done line's `empty=[...]` says `bound clamped to the far distance`. A
  scratch world cut from the debug world keeps the debug world's
  `render.farDistance`, or its far terrain is no evidence about the bake.
- **A static copy is one group.** Its shapes bake as their union
  (`SdfInstanceRange.BakesWithPrevious`) on a cubic lattice past the per-axis
  cap with per-shape refinement regions (`SdfMeshPathBake.UnionFinal`); its
  serial contour of about a million samples is the bake's long pole on the
  debug world, so the plan lists unions first, which decides only among groups
  the demand points rank alike.
- **Work goes near-first** ([residency](../../../../docs/decisions/residency.md)).
  `SdfMeshDemand` holds the eye of every view of the residency's latest frame
  (an `Elsewhere` or hidden view demands nothing; a filmed camera measures in
  its own image's pixels, `SdfViewSnapshot.DemandHeight`; a window fitted to
  its glass measures in the display pixels the glass spans from the viewer's
  eye, `WorldWindowFrustumFit.ShownHeight`, and demands nothing of a box
  outside its frustum, `SdfViewSnapshot.DemandWithinView`). A group whose
  planned box (`Group.TakeDemandBox`, kept before `Begin` cubes it) projects
  past a pixel ranks by the pixels a metre covers at the box, nearest first
  whatever its size; one under a pixel ranks after. Ranking by projected size
  instead let far terrain and shells outrank the lobby's small parts, and the
  occupied area landed no sooner than in plan order. `TryTake` takes
  lattices in `ByRank` order of the groups the tally holds as offering a lattice
  (`GroupTally.Offered`, into two heaps), at the ranks `Prioritize` last
  measured, the work queue runs store loads and
  lattice answers ahead of its stages (`Enqueue`, urgent) and every CPU stage of
  the bake (`Schedule`), and `SdfMeshRegionStore.RankLanding`
  orders owed meshes for landing in that order, the plan's or the list's order among
  equals and a draw no host box bounds first. The landing order is kept, never
  rebuilt a staging: a staged owed mesh is ranked when it joins or one of its rows
  changes, the head of the order every staging, and the rest in turn under a row
  budget, so its cost follows what changed, not what is owed. With no demand point (the CPU
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
  cold bake. The workers are threads of the pool's own at normal priority, below
  the frame's and the ticks' fork workers (`JobPool.WorkerPriority`)
  (`SdfMeshPathBake.StartWorkers`), blocking on an operating-system semaphore
  while idle: held as `Task.Run` items for a whole bake, they kept the process's
  other thread-pool work (timers, `Parallel.For`, the store's account) waiting
  5 to 17 seconds while the runtime injected threads. The done line's
  `tail=` is the seconds from the last moment work was queued behind every
  worker to the bake's end: the serial tail in which the largest groups finish
  alone. A completed batch's readback is copied out by a reader of its own,
  never behind the pool (`SdfMeshBakeGpuWork.Complete`'s `Readers`, a
  `BoundedWorkQueue` of a reader a slot on the host's thread pool), and its slot
  is released as soon as the copy is taken, so neither the frame thread nor a
  worker busy contouring holds the GPU's next batch back; the reader copies a
  final lattice's slab into the lattice and queues each answer on the pool ahead
  of every queued stage, each answer reading its slice of the copy in place
  (`SdfMeshPathBake.AnsweredSamples`, shared until its last answer ends), then
  advances `Progress` (`SdfMeshPathBake.Progressed`), since a field slice's
  answer queues no work item.
- **A bake draws on the machine's headroom, not the play bar.** Its group
  workspaces, readback, store writes and sample reads are authoring owners
  (`MemoryAllowance.Authoring`), held to the machine's room alone; the meshes it
  publishes into the library are play. A player's build holds authoring to the
  play bar (`MemoryAccount.SetAuthoringOnPlayBar`), so a bake there degrades
  and never fails ([the owners and their allowances](../../../../docs/plans/streaming-and-memory.md#the-large-host-owners)).
- **A dispatch's answer never waits for host memory.** The copy, and a sample
  set read in place of a dispatch, are charged to the bake's readback owner
  inside a standing reservation of `SdfMeshPathBake.ReadbackBytes`: the take
  grants a lattice its output bytes only while they fit what is left of it, and
  takes nothing, admitting no group, until the reservation is held, so the
  groups the account admitted can always be answered (the done line's
  `stop=[readback:…]` and `host-refusals=[readback-waits:…]`). The reservation
  goes once a take finds nothing to take, or the residency is released, and no
  answer holds any of it. Elsewhere a host-memory refusal defers rather than
  fails (`SdfMeshPathBake.Refusals`): a sample set the store keeps or reads past
  the account's room is dropped or missed (`store-writes-dropped`), a copy into
  the library the account refuses (a landing's published mesh, a follower's
  renumbered take of its leader's) waits parked until its bytes can be reserved
  as play's, since the library's copy is play storage (`Park`, `copy-waits`),
  and any other refused stage starts its group's
  final lattice again under an admission of at least the most the group's
  storage held and the bytes refused (`Group.PeakFloor`, `readmitted`). A request's points are its group's
  workspace (a final lattice's one point buffer, reused round to round; the
  group's crease and material points, cut to nothing by the answer that
  finishes them), so the reader reads a lattice's request and group only while
  its ticket is `Decoding` (`SdfMeshPathBake.Receive`: the decode and the
  sample key), before the queued answer can move the group on; whatever else
  it needs of the group the take fixed in the batch (`SdfMeshBakeBatch.Poses`,
  `Keeps`). Hashing the points after the answer is queued reads a later
  round's points, or throws `ArgumentOutOfRangeException` on a buffer already
  cut. A group's lead, which paces
  admission (`SdfMeshBakeBacklog.ObserveLead`), runs from its admission to its
  first `Schedule`d stage: a sample probe and an answer are queued urgent
  (`Enqueue`), never through `Schedule`: a probe ended in the admitting take
  would measure a lead of microseconds and clamp admission to one round of the
  workers. Only the groups the process memory account admits hold a final lattice
  (admitted at its first slab, whose answer allocates the lattice's values), by
  each group's own peak (`PeakOf`); the admission rules are in
  [data and memory](../../dotnet-performance/references/data-and-memory.md#owned-storage-and-byte-admission).
  Unadmitted groups keep refining their small coarse lattices. A first slab
  handed back unanswered (`Abandon`: a job whose recording failed or whose
  service was disposed) keeps its admission;
  counting its retry as a new one let returned first slabs fill every admission
  and stall the bake. A stage the host memory account refused starts its
  group's final lattice again (`Readmit`); any other stage that throws finishes its group with no new mesh
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
  frame thread's waits on the gate, and `gate=[site:entries/wait/hold/max]`
  every entry's wait and hold by site (`SdfMeshBakeReport.Gate`, taken through
  `EnterGate`, the one door to `m_gate`): on a cold debug-world bake the
  frame thread's entries (`frame`, `prioritize`, `retier`, `judge`, `dress`,
  `take`, `observe`, `evict`, `begin`) hold the
  gate far longer than the workers' own sections do, so the
  workers' waits are the frame thread's holds; `begin` (`Start`'s call of
  `Begin`) and `evict` (the eviction sweep, and the evictions a frame makes) run
  inside another entry's hold and are counted out of it. Whether a group still stands in
  the bake is its `Listed` flag, which only `GroupList.Set` and `Clear` move,
  never a search of the list. No entry walks the list for counts either:
  the take reads how many groups fly, run a stage, hold an admission, wait for
  their first stage or hold a lattice from `GroupTally`, which every setter of
  those states settles (`Group.Settle`, the one place a group's contribution
  moves a count or a set), and the take walks only the groups the tally holds as
  offering a lattice, the ready and the wanting an admission apart, which it
  does not read at all while the pool backlog closes admissions and does not
  start when none holds one. `ObserveResidency` walks only the groups that landed or
  were drawn again and have not stood in the mesh region
  (`Group.AwaitResidency`, the one door a group joins that set by, queued from
  any thread and taken in under the gate). A start decides whether the store
  may hold a mesh or checkpoint from its known entries
  (`SdfMeshStore.MayHold`, `MayHoldCheckpoint`, the same per-identity sets as the
  sample probe below), never a file read. A report line written while the
  thread holds a gate waits for the gate's release
  (`SdfMeshBakeReport.GateLeft`): a write to a piped standard error can block
  on its reader, and a frame thread blocked there holds every worker.
  `Prioritize` measures a share of the groups' priority and view each prepared
  frame, outside the gate on the demand measure's lane (below), from a cursor
  (`GroupSweep`, the cursor the demand watch's reset sweeps use too): a rank is up to a
  measure old, a restart ranks every group it plans, and a group planned in a
  listed group's place takes that group's rank (`Replace`). The counts of the
  groups that owe the frame and of those with nothing to draw
  (`CountHoles`) move with each visit and each `Replace`. The dresser reads the
  groups the bake names changed since its last read (`Touch`, taken by
  `FrameAccess`) and every group only when the list was replaced, the program or
  the copy layout moved, or a verdict declined (`TouchAll`). The groups that keep
  a floor (`m_keepers`) are the only ones the eviction sweep, the mesh region's
  naming of kept floors and the trace judge read, the judge through the groups
  whose floor, done state or product changed (`Judgeable`). The retier decision
  still reads every group once a measure.
- **A stage's scratch is owned, its result exact.** A CPU stage grows its
  per-vertex and per-quad workspace in `OwnedBuffer<T>` lists and tables, taken
  through a `RentedScope` or the contour lattice's `Allocator`, which is the
  group's native allocator (`SdfMeshLibrary.Allocator`) in a World: that scratch
  never reaches the managed heap, so a bake cannot set off the collections that
  pause the frame thread. Offline (no allocator) the same buffers rent from
  `RentedArrays`, whose arrays up to 1 MiB reuse the shared pool's buckets and
  whose larger arrays of reference-free elements reuse a bounded large pool
  (trimmed when a bake's last group finishes and on `MemoryAccount.TrimCaches`).
  Tables a stage still leases as arrays (`RentedArrays.Rent(minimumLength, out
  array)`, `RentedScope.Rent`) take that managed path in a World too. Storage
  types are stop types of the bake's code identity (`SdfMeshBakeClosure`), so
  changing them bakes nothing again. The stage copies out only the arrays its
  result keeps. A group's lattices and point tables
  stay in owned native storage. A rental can be longer than requested and its
  contents are unspecified. Fill the range before reading it and track the
  logical count separately. The
  done line's `managed=[allocated,gc-pause,collections]` reads the process's
  allocation and collector pauses over the bake's window. The contour, crossing
  search, weld and crease split rent theirs, and so do the collapse
  (`SdfMeshCollapse.Collapser`), the planar merge, the chunk binning and the
  material cut's working lists, through a `RentedScope` the stage disposes
  (every table of one build, charged to the group's `mesh-bake-group` owner); the
  store's entry encoder collects its payload in a `RentedMemoryStream` and writes
  each byte plane in chunks. A mesh the bake passes on keeps its welded sites
  (`SdfMesh.BakedSites`) through a record copy, so a later stage never hashes the
  positions again. The stages' outputs are still allocated exactly, and a large
  rental the pool cannot answer is a fresh array: read `rented-large-fresh` on the
  done line beside `allocated`, which counts it.
- **A dispatch fills its samples.** A take takes lattices by priority until
  `MeshBakeMaxSamples`, passing over one too large for what is left and taking
  smaller ones behind it; stopping at the first that did not fit left the debug
  world's dispatches under half full.
- **One geometry bakes once a bake.** A group whose sealed geometry another
  group of the same restart already bakes or loads waits on it
  (`Group.Leader`, `Follow`) and takes the library's mesh, renumbered, when the
  leader lands; the start line's `shared=` counts the groups a restart plans that way. The
  renumbering shares immutable geometry and allocates remapped triangle materials, so it runs on the pool
  (`TakeLanded`), outside the gate the frame takes; a leader that found no
  surface lands the follower empty too (`Draw(group, null)`), or it would keep
  drawing the mesh it had before the rebake. An offline bake of chosen groups
  (`puck mesh audit --cpu --group`) names them in `SdfMeshPathBake.Selection`:
  an unselected group finishes unbaked and leads none, where answering it as
  empty space handed its empty result to a selected follower.
- **One geometry bakes once across residencies.** A group the restart's index
  does not cover starts through `SdfMeshPathBake.Start`, the one caller of
  `Begin`, which asks the library (`SdfMeshLibrary.Share`) whether a bake of its
  geometry and identity already runs in any residency holding the library. If
  one does, the group waits on that `SdfMeshBakeShare` (holding no lattice) and
  takes the leader's mesh from the library when it lands (`TakeLanded`, the
  same take a follower in one restart makes, on the pool); otherwise it leads a share of its own. The leader's `Done` and
  `Retired` setters are the one door a share resolves by, under the library's
  gate, so no group joins a resolved share. A waiter whose leader left nothing
  in the library (retired, failed, mesh trimmed) starts again, and one whose
  leader's bake was asked no take while its own was (`DesertStalled`: a
  residency no frame renders) deserts and leads. A waiter's priority raises its
  leader's. The start line counts `joined=`, the done line
  `joined=[groups,landed,abandoned,stalled,samples-saved]`, and
  `world.counters sdf.mesh-shares` reads the library's totals. The share is a
  stop type of the bake's code identity: who bakes a geometry never changes what
  it meshes.
- **Large static groups draw in chunks.** A union, and one shape whose contour reaches
  `SdfMeshChunks.WholeTriangles` (a 500,000-triangle terrain held one worker for its whole collapse), is chunked;
  a smaller single shape stays whole, since locked seams tripled a terrain sector's first level. A mesh that would
  fill more than `SdfMeshChunks.MostChunks` 8 m bins takes bins of twice the side until it fills no more: the world
  group's far ground cut at 8 m makes 160,000 draws, too many to cull and
  level on the CPU for every pass, so `frame/sdf-mesh-selection.hlsli` selects them through the existing counted indirect
  path; chunk sizing still controls retained geometry and candidate work.
  `SdfMeshChunks` merges the contour's planar regions first, then partitions the
  merged triangles by centroid into 8 m bins before collapse. Splitting first
  locks artificial rows of contour vertices along otherwise flat faces and
  makes every later level keep them. It never clips a triangle: each chunk's box includes its whole
  triangles. The merge keeps every open-edge vertex and the collapse locks them, so every level shares the exact seam
  edges with its neighbours. `SdfMesh.Chunks` holds the separately levelled draws; the parent holds the joined vertex
  and triangle attributes the chunks view, and never enters the GPU region. The store writes only the chunks; an offline
  decode joins their indices for whole-group queries (the audit), and a published (native) parent keeps none, since the
  region, the irradiance scene and the store all read the chunks. Dynamic groups and glass stay whole; glass skips collapse because its pass always draws level
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
  irradiance scene (`SdfMeshDraw.IsStatic`).
- **Every shape declares its spacing by one rule.** `WorldShapeDetail.Of`
  serves a static stamp's shape, a pooled blend group's part and an ungrouped
  pooled shape alike, feeding `SdfInstanceRange.Detail` and
  `SdfInstancePart.Detail`. A bound measures a shape's reach, not its smallest
  feature, so each feature declares a spacing: a round section (a sphere's, an
  ellipsoid's middle radius, a superellipsoid's tightened by its exponent, a
  cylinder's or cone's, a capsule's, a torus's tube, a strand's thicker radius,
  an elliptic profile's tightest bend, a rounded or convex profile's corner)
  bakes at `SdfSampledLattice.RoundCell`, the finer of a third of its radius
  (each cell turns under the contour's 25-degree crease split, so it shades
  round) and the cell whose chord strays at most `SilhouetteError` (0.2 mm,
  half a pixel at the closest camera) inside it, so a drum's or a lens's
  outline is round, not a 30-gon. A rounding of 5 mm or more declares its
  radius: a fillet about a cell wide otherwise contours as a staircase,
  crease or face cell by cell (the Moth's seat plate), while a narrower one is
  a crease the contour keeps sharp. A plate longer than eight of its
  half-thicknesses declares its half thickness, and a folded shape its copies'
  smallest half extent, since a lattice sized by its length or its copies'
  span steps clean over the sheet. A shell declares a quarter of its
  thickness, a warped shape a thirty-second of its smallest half extent and
  cellular relief a thirty-second of its wavelength. A sine relief or a warp
  also declares its crest's bend (`ShapeWarpDocument.ReliefRadius`, one over
  amplitude times the largest frequency squared) at `RoundCell`, since a
  lattice sized by a large shape's extent facets a relief only a few of its
  cells long. A blend's seam is a
  feature neither shape's bound measures, so a shape with a `smooth` blend
  declares it: a smooth, pipe or groove seam turns at `smooth` (`RoundCell`),
  a chamfer's bevel is a face `smooth` wide (half of it), stairs are `steps`
  turns each; the pipe and groove exhibits refined nothing before this term
  and contoured their 4 cm bead on 27 mm cells. The finest applies, at
  placement scale, no finer than 2 mm (`WorldShapeDetail.FinestDetail`); a
  part thinner than the floor declares the floor (a 1.2 mm eyelid tube that
  declared nothing contoured as shards on a 2.4 mm lattice). A creation emitted
  as one scoped instance (static, or a moving root's surface) declares a part
  a shape and one for its noise relief (`WorldShapeDetail.PartsOf`,
  `SdfInstanceRange.Parts`): each shape's box at its own detail, and the
  relief's spacing (`CreationNoiseDocument.DetailSpacing`) over the box its
  composed surface lies in (`SdfBoundAlgebra.ComposeBox`), so the group bakes on
  a union's final lattice and a moraine's 9 cm round section refines its own
  box, not a whole terrain sector. Text declares a third of its thinnest stroke
  (`SdfProgramBuilder.GlyphDetail`). Refinement stops at
  eight cells a coarse axis (a fitted text group's lattice is spaced to reach
  its detail instead, below), and the fine-sample budget
  (`SdfSampledLattice.MaxFineSamples`) takes a coarser split where the cells a
  region covers would pass it: the debug world's north-west wall, whose relief
  region covers about 128 thousand 7.5 m cells, splits each in three and bakes
  at 2.5 m against the 1.56 m its relief declares
  ([the terrain pass](../../../../docs/plans/terrain-pass.md#what-set-a-sectors-cell)). A surface that large reaches
  its declared spacing only split across lattices, the streaming plan's source
  tiles. A declaration bounds refinement; the bake
  decides its cost (next bullets): a detail at or past the lattice's own
  spacing refines nothing, alone (`SdfMeshPathBake.OwnFinal`) or in a union
  (`UnionFinal`). In a static union a member's region is its own world-axis box
  where the stamper knows one (`SdfInstanceRange.Box`,
  `CreationStampEmitter.ShapeStampBox`: an unwarped, unfolded primitive), not
  its bound sphere's cube, which for a 48 m strip spans the whole slab it hangs
  from. Convex rounding reads the scaled polygon's inradius and extrusion
  clamp; extents use `SdfSolidGeometry.HalfExtent`, since a revolve's scale Z is
  an offset and may be zero while its radial extent remains positive.
- **A declaration is bounded by what a view resolves: the group's tier.**
  `SdfMeshTier` is one ladder of cells (1 mm times two to the tier). A group's
  tier floors every spacing its refinement aims for (`OwnFinal`, `UnionFinal`
  and `FactorOf` take `SdfMeshTier.Cell(group.Tier)` as the floor of a member's
  detail, `FineCell` and `MemberCell`; every factor is still at least two, so a
  lattice at its floor refines the cells `Refine` finds it cannot resolve at all,
  a sheet, gap or tip thinner than a cell, which would otherwise lose the feature's
  whole extent), and is a fact of the sealed geometry
  (`SdfMeshBakeGroup.Seal` appends it, `Fact.Tier`), so each tier of a
  geometry is its own library and store entry. A residency's bake plans each
  group at the coarsest tier whose cell stays under half a display pixel at
  the group's box from the nearest demand point (`SdfMeshDemand.PixelAt`,
  `SdfMeshTier.Of`; at 1080 lines and a 60-degree field, 2 mm within 3.7 m,
  4 mm to 7.5 m, 8 mm to 15 m, 16 mm to 30 m), held between the rungs that
  change its mesh (`FinestTier`, the finest spacing any member asks;
  `FloorTier`, the planned final cell, past which nothing refines;
  `CoarsestTier`, the last rung whose cell fits `SdfMeshTier.CoarsestCells`
  times along the planned box). Past its floor a tier coarsens the lattice
  itself (`FinalLongest`, `SdfMeshTier.LatticeSamples`): the sheet refinement
  still runs but splits no finer than `PastFloorThinShare` of the tier's cell
  (`ThinSplit`), a rung that meshes nothing owes the rung below it
  (`MeshedNothingPastFloor`), and a part thinner than a cell inside a group
  that still meshes is lost at that rung. Every floor role (arrival, the kept
  floor, eviction, the trace mesh, the band a small view draws) reads
  `AtFloor` and `FloorTier`, never `CoarsestTier`. The budget's bias coarsens
  a demand as far as the floor and no further (`SdfMeshTier.Demanded`, the one
  rule `DemandedTierOf` asks, with `DemandedSlack` and `DemandedCoarsestEdge`
  as its watch's bounds); a tier past the floor comes only from the unbiased
  pixel. With no demand point a
  group plans at its coarsest, and a plan made before any view was observed
  asks the floor (`Tiers`); an offline bake
  (`MeshCpuBake`, `puck mesh audit --cpu`) sets `SdfMeshPathBake.PlannedTier`
  and takes each group's finest unless `--tier` or `--eye` says otherwise. A
  landed group whose demanded tier has been finer than its own for
  `SdfMeshTier.HoldFrames` frames is planned again at the finer tier and placed
  as a replacement (`SdfMeshPathBake.Retier`, its `Replaces` path, at most
  `MaxInFlight` at once, the finest demand first; an arrival's landed floors
  start their first details `SdfMeshTier.FirstsPerFrame` a frame, `StartFirsts`,
  and a replan plans only the group's own instances, `SdfMeshBakeGroup.Refresh`,
  never its slot's other groups or the program's other instances): the coarser mesh draws
  until the finer is baked and its words stand in the mesh region
  (`FinishReplacements`; the swap bullet below). A frame measures again only
  the groups whose demand may have moved (`SdfMeshPathBake.MeasureDemands`,
  the demand watch in `SdfMeshPathBake.Demand.cs`), on a lane of the bake's own
  beside the rest of the frame, never under the gate: `PrepareFrame` decides the
  tiers from the measure the frame before posted, copies what the measure reads
  of the frame into the bake's own storage in proportion to what moved
  (`CopyFrameInputs`: the moved slots' transforms, the copies' root matrices),
  and posts the frame's measure and priority share (`PostMeasure`, a `JobLane`
  at the frame's fork priority); the next `PrepareFrame`, a change of the demand
  points (`SdfMeshDemand.BeforeChange`), the reports that read the watch and
  `Retire` join it first (`JoinMeasure`), the group list throws on a change
  while it is frozen (`GroupList.Frozen`), a group's `DemandPixel` throws when
  touched off the measure meanwhile, and the demand watch's group marks live in
  a byte of their own (`Group.DemandMarks`) so workers' marks never race them.
  `sdf.residency.demand.join-waits` counts the joins that found the measure still
  running. `Pending` and `DrawsPending` take no gate: every release of it
  publishes what the bake owes (`PublishOwing`). Groups newly set into a
  place, groups whose slot or copy moved (the frame's transform and copy
  journals, through the list's owner index), groups a clock made due, and
  groups a change of the points reaches. `SdfMeshDemand` matches each view to
  the point it held by its source and cut and keeps two clocks, travel and
  scale, which bound how far any pixel moves; each measure leaves its group
  due on each at half its band (`SdfMeshTier.Slack`). Frustums are watched
  per view, never by one clock: each view bounded to its frustum has a lane
  (`SdfMeshDemand.LaneOf`) with its own shift and turn odometers
  (`CameraSnapshot.FrustumTurn`), and a measure keeps, for each lane whose
  answer could move the group's tier, the box's slack toward that frustum
  and reach from its eye (`SdfMeshDemand.BoundsOf`, `Watch`) in one arena of
  records (`FrustumWatches`). Every frame streams the records against each
  lane's moves since (`SdfMeshDemand.Moves`), so one fast window's sweep
  makes due only the boxes near its own planes; a change of lanes carries
  the records over (`SdfMeshDemand.Carry`). A box is read against every
  point at once, eyes and frustum planes in vector lanes
  (`CameraSnapshot.BoundingPlanes`), and the read is kept per thread for the
  next question about the same box. A moved slot's group is measured only
  once its world box has moved, with the clocks and its lanes since, by that
  much. A view that comes, goes, jumps past `SdfMeshDemand.JumpMetres` or
  changes kind is a change: only groups whose distance from either side puts
  their pixel under their band's top are measured, at once where the new
  side reaches and a share a frame where only the old side does (their pixel
  can only rise); a new view bounded to its frustum that refuses a group's
  boxes only adds its lane to the group's watch. A bias change recomputes the bands from the
  pixels held, and only an overflowing change list
  (`SdfMeshDemand.MostChanges`) measures every group over
  `SdfMeshTier.MeasureFrames` frames.
  `Retier` decides the tiers once every `MeasureFrames` frames, reading only
  the groups the watch attends: those measured or set since they last stood
  at the tier they draw. A still view measures nothing and decides over
  nothing; `world.counters sdf.residency` counts the measures, resets and
  groups decided. Any new input to `MeasurePixel` needs its own route into
  the watch, or a group keeps a stale tier. A bake whose groups have all landed can still owe
  the tiers its demand moved to, which `SdfMeshPathBake.Pending` does not
  say: `SdfMeshPathBake.DetailSettled` does, once `Retier` has replaced
  nothing, had nothing landing or waiting its hold, and pressed on nothing
  it can still coarsen for `SdfMeshTier.SettleFrames` frames, and
  `world.wait bakes` waits on both for the world's own residency, that
  many frames after it armed. With room in the mesh region a coarser tier demanded leaves the
  finer mesh where it is. The region's budget
  (`SdfMeshTier.BudgetEighths` of `SdfWorldTables.MeshRegionLimitBytes`, read
  each frame by `SdfMeshPathBake.ObserveResidency`) decides the rest in
  `Retier`: a finer tier starts only while the bytes the draws name leave it
  room; while the budget presses, the groups drawing a finer tier than their
  demand asks are planned again at the demanded one, the largest saving
  first; and when it presses with nothing spare every demand is coarsened
  half a rung (`SdfMeshTier.Biased`), taken back once the finer demand would
  fit again. The first plan is budgeted the same way from the plan's own
  measure. `world.residency` reports `tiers-owed`, `owed-error` (the worst
  owing group's resident cell in display pixels), `owed-frames`, the worst of
  each since the bake started, `retiers`, the resident triangles by tier, the
  budget's account (`region-budget`, `region-named`, `tiers-spare`,
  `tiers-held-back`, `coarsened`, `detail-bias`, `bias-raised`,
  `bias-lowered`) and, per demand point, the groups and triangles that point
  sets the tier of ([the memo](../../../../docs/plans/bake-demand-tiers.md)).
  A stop that settles with `detail-bias` above zero is drawing coarser than
  half a pixel there: the demand is past the budget, which is the reading to
  act on, never a raised limit.
- **A group arrives at its floor and keeps it.** A residency's group that
  arrives in view with nothing to draw (no group at its selector, or one still
  bringing its floor, `SdfMeshPathBake.Arrives`; in view is
  `SdfMeshDemand.Sees`, every box with no demand point) plans its coarsest
  tier first (`SdfMeshPathBake.Tiers`), so a place lands whole and coarse
  before it is sharp. One arriving out of view plans its demanded tier at
  once and keeps no floor: floors are about a quarter of a cold bake's pool
  work, and no view waits on that place. Every take and every worker stage
  orders the groups in view ahead of those out of it, each side by the
  demand's priority (`SdfMeshPathBake.RankOf`). Once that floor has landed,
  the next tier decision starts the demanded tier as the arrival's own bake
  (`Group.FirstDetail`): no hold, outside `MaxInFlight` and the budget, as a
  first plan at that tier would have baked. Until then the bake owes the frame
  (`Owes`, `IrradiancePending`), so a lockstep still or `world.wait bakes`
  never settles on floors. The detail lands ahead of the draws beside the
  floor and keeps the floor with a claim of its own (`Group.KeptFloor`, taken
  in `ShowReplaced`, the one place a replacement inherits from the group it
  replaces); the kept floors are named ahead with the waiting products so
  their words stay in the region. A finer tier the region refuses over a
  standing kept floor is evicted to it at the next frame
  (`SdfMeshPathBake.Evict`): the floor takes the draws at once where it was
  baked from the geometry the plan reads, and is baked as a coarser
  replacement otherwise; an evicted floor takes detail back only through the
  budget. A group taken whole from the library, or replanned while it drew,
  keeps no floor until it arrives again. `world.residency`'s floor line
  (`SdfMeshPathBake.DescribeFloors`) counts floors shown, pending, empty,
  failed and kept, groups with none, first details owed and landed,
  evictions and their bytes, `holes` (groups in reach, demanded past a pixel,
  drawing no standing mesh) and `hole-frames`, and the floors' bytes by
  class. The floor is the coarsest rung only: a floor that meshes nothing is
  counted `floor-empty` and its group goes straight to its detail, with no
  finer rung tried and no coverage check yet
  ([step 5](../../../../docs/plans/streaming-and-memory.md#step-5-the-floor-and-eviction)).
- **A region refines only where its surface turns, and each region at its own
  factor.** `SdfSampledLattice.Refine` takes a factor per region
  (`SdfLatticeRegion.Factor`): `UnionFinal` asks each member's own
  (`SdfMeshPathBake.RegionFactor`), so one fine member refines no other
  member's box, and the lattice's split is the finest region's. Inside a region
  a cell refines only where the coarse samples' second differences show the
  surface turning (`CurvedSplit`: a plane's field is affine and contours
  exactly), each to the factor its curvature needs under `RoundCell`, never
  past its region's. A cell's split is the refinement's own divided by a power
  of two, so every split divides the finest; a measured cell of a coarser split
  measures only its stride's grid of fine samples and interpolates the rest
  inside each stride cell (`StrideHolder`, the finest holder of a shared face
  answering for both sides), and the contour merges each stride cell's fine
  vertices per connected piece, as it merges an unmeasured ring cell's
  (`SdfDualContouring.Merge`), so its triangles follow its own split. The
  value-only test cannot tell a sharp crease or a sub-cell fillet from a tight
  turn, so a declared region still refines its creases.
- **A tall union misses thin layers.** One static creation holding stacked
  floors bakes as one union whose lattice is sized by its height, so a 0.3 m
  slab between two samples never crosses and is dropped while it still
  collides. Give each storey's floor its own prototype and placement
  (`worlds/debug/hall.puck`), so each is a flat group. Trim set into a large
  union is the same case: the hall's walls bake at 15 cm, so the cassettes at
  their window heads, set 2 cm and 5 cm back from the wall's faces, are a row
  per level and face of their own (`hall-cassettes-*`), each baking at 5 cm.
- **Text bakes over its glyphs' box, without the coarse search.** An instance
  holding text alone is fitted (`SdfInstanceRange.Fitted`): the builder
  unions each glyph cell's box as `SdfProgramBuilder.Text` emits it and
  `EndInstance` declares the union as the instance's box (a static instance)
  or its one part (a dynamic one on the glyphs' slot). A group whose every
  instance is fitted takes the union of those boxes
  (`SdfMeshBakeGroup.Fit`) and goes straight to its final lattice
  (`SdfMeshPathBake.StartFinal`). The search samples a bound at a spacing the
  bound sets, and a line of strokes thinner than that spacing reads far from
  the surface wherever a column falls between two strokes, so the narrowed
  box cut the first words off the gallery plaques' notes. An emitter that
  knows its surface's box exactly declares it the same way; a box with a
  margin about the surface is not fitted. A stroke about one fine cell wide
  tears or bridges to its neighbour, and one two cells wide is the margin
  itself: a sans face's arms are thinner than its stems (Inter's are 0.082 em),
  and the gallery's label-only plaques, at half a 0.1 em stroke, baked no finer
  than 3.7 mm cells, under two cells across those arms. A run asks a third of
  its thinnest stroke (`SdfProgramBuilder.GlyphDetail`, from
  `GlyphStrokeEms`; `WorldPlacementStamper.TextDetail` declares it), and a
  fitted group whose length-spaced lattice cannot reach that detail within
  `MaxDetailRefinement` cells takes a lattice that many details apart
  (`SdfMeshPathBake.OwnFinal`, budgeted as a union's by `BudgetedLattice`). A
  fitted text group's lattice keeps only three samples across its slab
  (`TextMinimum`, not `FinalMinimum`): every cell its glyphs can enter refines,
  and nine samples across a slab a centimetre deep would have the refinement
  split sub-millimetre cells as many times as the strokes' cells, spending the
  fine-sample budget (`SdfSampledLattice.MaxFineSamples`) across the slab's
  depth and leaving a long note a coarser split. The tier floors the detail, so a view that
  resolves no stroke pays for none, and a group baked at a tier coarser than its glyphs
  declare thickens them instead of losing strokes: the lettering stroke rule in `sdfGlyph`
  widens the thinnest stroke toward `GlyphThickenCells` cells within the atlas's band and the
  relief to one cell, or bakes the glyph as its ink box where the stroke stays under
  `GlyphHoldCells` cells
  ([text and glyphs](../../../../docs/rendering/sdf/reference/text-and-glyphs.md#the-lettering-stroke-rule)).
  Every lattice of a group takes the group's cell and stroke through one door
  (`SdfMeshPathBake.RequestOf`, `SdfMeshBakeRequest.Glyphs`), so coarse and fine samples
  read one field; a lattice built around that door bakes the glyph unthickened beside
  thickened neighbours. A
  glyph's field is its atlas distance only within half the atlas's distance
  range of an edge and a bound past it (a constant band, then its cell's box),
  so a static instance's glyphs (`SdfInstanceRange.Glyphs`, joined into
  `SdfMeshBakeGroup.Glyphs`) are a region of their own, at their own detail, in
  which every cell the surface can enter refines (`SdfLatticeRegion.Bound`): a
  cell whose samples sat on that band read flat and skipped the refinement,
  losing an i's dot or a full stop between them. Lettering engraved into a
  scoped creation refines only that box, never the creation's whole surface.
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
- **A pooled blend group bakes as a union of its parts.** Its one instance
  declares a part a shape (`SdfInstanceRange.Parts`, `SdfInstancePart`): the
  shape's box in its own slot's frame and its detail. The plan carries each box
  into the group's frame through the pose the geometry hashed for that slot
  (`SdfMeshBakeGroup.AddDynamicMembers`), and each becomes a member
  (`SdfMeshBakeGroup.Members`), so a group of several members bakes on
  `UnionFinal` as a static copy's run does: a cubic lattice at the union's own
  spacing, not `OwnFinal`'s `MinimumCell` floor, refined per member. A dynamic
  slot holding several instances without parts unions their spheres the same
  way. Parts are hashed into the geometry by slot offset. Under `OwnFinal` an
  18 cm swallow's blend group bakes on a 15 mm lattice refined over its whole
  bound; under `UnionFinal` it starts at the 2.8 mm its static stamp's union
  takes, each member refined over its own box at its own factor. A folded
  member's part is the cube about its slot's origin its copies reach.
  A dynamic instance's text declares detail too: a pooled creation's embossed text is
  its own instance on a slot after its shapes' that holds the root's pose,
  bounded by its glyphs' reach and declaring their
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
  `UnitsOf`), as an operand's snapped pose is, so the key, the detail and the lattice
  are the full-size body's; the group's draw matrix takes the scale
  (`SdfMeshDresser.Dress`, kept in the record of a draw riding its slot), and a member's vertex takes the draw's scale
  (`sdfMeshPoseScale`, the current and previous positions). A body crossing a
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
  disagree splits into them, taking one vertex at the middle of each of its
  edges that the triangle across the edge splits at too (found by position, so a
  cut's boundary point that landed on an end counts), leaving no T-junction for
  the audit to count as an open edge, and one whose vertices disagree is cut along the
  boundary its edges' bisection finds (`SdfMeshMaterialCut`). Two shapes that meet at a crease change
  material at the crease vertex itself, while a point lifted off the chord across the crease reads either
  shape a round or two out from it, so a boundary within the quarter of an edge next to a crease vertex
  (`SdfContour.Features`) lies at that vertex (`SdfMeshMaterialCut.Boundary`), or the cut lays a sliver
  along the rim that turns out of both faces. `SdfMeshMaterialCut.Build` holds its lists to
  `MaxTrianglesPerQuad` and `MaxVerticesPerQuad` for each contour quad, checked in one place (`Within`, after every
  growth); a cut past them throws `InvalidOperationException`, which `SdfMeshPathBake.Build` reports as
  `group <n> refused: the material cut of …` and lands the group as a refused mesh. A material
  boundary only lies on the surface within one group: two groups' shapes meet
  as crossing meshes, whatever their materials.
- **Moving blends bake at authored rest.** The pooled emitter marks its delta-frame instances
  `RestPoseSkin`; `SdfMeshGeometryBuilder` freezes those deltas at identity. `CreationMotion.Envelope`
  admits bounded smooth seams, and `CreationMotionPlan.RenderDocument` partitions rejected groups by
  motion owner before emission. The CPU `SdfMeshSkin.Build` round propagates owner shares through the
  evaluator, once per exact position; every crease/material copy takes that tuple. Twelve packed bytes
  in `SdfMesh.Influences` carry a second owner, share and gradient. A third share above 1/64 falls back
  to rigid owner contours through the existing contour pipeline. Current, previous and shadow positions
  share group-relative dual quaternions and corrected normals (`frame/sdf-mesh-skin.hlsli`). See
  [moving blends](../SKILL.md#moving-blends) for the complete contract and checks.
- **A skin bakes once.** A skinned group bakes at the pose its program arrived in (a rest skin at rest) and
  is never baked again for its members' motion. A moving cutter is an operand instead, and its group is
  planned again through `SdfMeshPathBake.Revisit` as the operand moves a step
  ([moving blends](../SKILL.md#moving-blends)). `sdfLayerFrame` interpolates rest coordinates and lanes
  through the skin. The lead's presented striped-neck burst checks the material result in motion.
- **A landing mesh rebuilds the draws.** A group that bakes again draws the mesh
  it replaces until its own lands, so it counts as drawn already; `Land` and
  `Load` hand the new mesh over through `SdfMeshPathBake.Draw`, which marks the
  draws stale, or a still group keeps drawing the old mesh.
- **Every landed mesh is merged into its planar polygons** (`SdfMeshPlanarMerge`,
  called in `SdfMeshPathBake.Land` before the library stores it), so the store
  keeps and every pass draws the merged mesh, and the irradiance trace reads its
  coarsest collapse level within `SdfMeshIrradiance.TraceError`. A
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
  skip exact measurements that cannot improve the result; they never certify
  acceptance. Reverse edge measurements are shared across targets of an
  unchanged source fan, and unchanged failed edges are not measured again.
  Judge a changed collapse by
  the field audit at each rendered level, not by equality with earlier meshes.
  Glass skips collapse because its pass uses only
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
  directly from the decompressed payload into the storage the mesh keeps (`SdfMeshStorage`), a host's native
  blocks or an offline read's arrays, applying the taking group's material ids as the planes are read; a
  restore takes only the normals and the coarser levels' indices.
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
  jobs of at most `SdfGpuWork.MostUploadJobBytes` under its byte, host-time and
  GPU-share budgets, each a rate of wall time and never a count a pump: a
  per-pump budget lands only as fast as frames come, and a window the
  compositor throttles to a few frames a second would take many times
  longer to land the debug world's mesh region. Jobs take the meshes nearest the views first. A worker packs a job's
  words from the meshes' own arrays into a host-visible buffer of the landing's own before the job is taken
  (`SdfMeshRegionStore.TryTakePackedLanding`, one job packing while the one before it records), so the frame thread writes
  no mesh word; the job's recording gathers them into the region (`GpuRegion.TryOweGather`) in the job's slot
  (`MeshLandingSlot`, past the upload ring's), after any move the region owes. A packed buffer is free again only once
  the job that read it completed. A mesh leaves `m_meshesOwed` only when every job carrying
  its current placement has completed, and the landing filter in
  `SdfWorldTables.StageMeshDraw` writes its rows absent until then; a
  relocation, a moved mesh or a trim drops that mesh's progress, so a job still in
  flight for its old words lands nothing. A frame that stands is staged again
  when a mesh lands (`StageLandedMeshes`), and a lockstep frame waits for the
  landing (`SdfWorldPasses.WaitingOf`). `world.residency`'s
  landing line reads the frame uploads' staged mesh bytes (`frame-staged-most`)
  and the landing jobs. A grown region takes the words it held. A
  list past the draw reserve doubles it and moves only the meshes the grown
  reserve takes (`SdfMeshRegionArena.Moved`): relocating
  every mesh for it rewrote and compared the whole region in one frame. A moved
  mesh that stood is copied from its old words on the GPU (`GpuRegion.TryOweMove`,
  `SdfMeshRegionStore.MoveMesh`) in the next recorded copy, before any staged or
  gathered word, so it never leaves the frame; one the region cannot move is
  written that frame from its arrays. A refused mesh that would fit packed
  compacts the arena instead of relocating it (`SdfMeshRegionArena.Compact`): the
  highest meshes take lower free runs, at most `MostCompactionWords` a plan, and
  their old words stay counted as held until the copies are submitted
  (`ReleaseMoveSources`), so no later move takes them and a mapped region keeps
  them backed. The arena never relocates whole: a compaction that can move nothing leaves
  the refused meshes refused until names change. The presentation's region
  (`SdfMeshRegionStore`) keeps no host copy: a placed mesh's words are owed and
  land through a gather from the mesh's own arrays, and a residency's draw
  records lie in a region of its own tables.
  A placement names absolute words (`SdfMeshRegionMesh`); a new per-draw field
  belongs in the record, never in a mesh's words. The record's layout is
  `SdfMeshRegion`'s, a sync pair with `frame/sdf-mesh.hlsli`
  ([sync pairs](sync-pairs.md#engine-buffers-groups-and-bindings)).
- **A mesh that replaces a drawn one lands before a draw names it.** A
  group's product and the mesh the frame draws for it are two things
  (`SdfMeshPathBake`'s group: its `Mesh` and its `Shown`). Every product
  arrives through the one setter, whatever made it: a finer or coarser
  tier, a resample, an operand's step, a new program's plan, a renumbered
  mesh. Where a residency's tables land the bake's meshes, a product that
  replaces a drawn mesh waits ahead of the draws: the bake names its pieces
  to the tables (`SdfMeshPathBake.ObserveResidency` into
  `SdfWorldTables.NameMeshesAhead`), the arena places them beside the
  meshes the draws read (`SdfMeshRegionArena.NameAhead`) and keeps them like
  a named mesh, they land after the meshes a draw waits on, and the group
  takes the draws only once the tables say every piece stands
  (`SdfWorldTables.MeshStands`). So a swap holds no draw out of a frame, and
  a mesh landing ahead stages no draw again: only the hand-over does. The
  standing products take the draws together, once none is still landing or
  one has waited `SdfMeshTier.HandFrames`, so a wave of replacements moves
  the draws every view renders from once. The hand-over stages its own
  rows: the dresser replaces a group's bindings in their run when its
  piece and copy counts stand, so its journal names those rows alone;
  `SdfWorldTables.StageMeshRegion` moves each changed name through the arena's
  row transaction and stages only those rows. A piece/copy-count change
  reallocates that group's run, preserving every unrelated row. Only a start
  over (failure retry, the free share, a forced pack or locked canonical order)
  rebuilds all owners. Claims and glass counts move inside the row writer.
  `SdfMeshPathBake.Pending` stays true while a product waits, so
  `world.wait bakes` and a lockstep frame wait for it;
  `SdfMeshPathBake.DrawsPending` leaves the waiting products out, and a
  view's standing render is judged by it
  (`SdfWorldResidency.IsUnchanged`), since nothing a view draws moves until
  the hand-over. The owed meshes named ahead land from a list of their own,
  listed again whenever the set changes (`SdfWorldTables.MeshLanding.cs`):
  the roster's landing order is rebuilt only when a draw list is planned,
  which a hand-over no longer does. The guarantee stops
  at a group's first mesh, which has nothing to keep drawing, at a bake no
  tables observe (an offline bake draws each product at once), and at a
  piece the region has no room for beside the mesh it replaces
  (`SdfMeshRegionArena.AheadRefused`): that product takes the draws anyway,
  and the piece lands in the words the old mesh gives up, out of the frame
  until it does. `world.residency` counts the products that took their
  draws (`handed`, with `handed-refused` for the last case) and those
  waiting (`ahead`).
- **One region holds every resident mesh, up to what its grant reserves.**
  Before a plan the tables reserve the standing buffer and a replacement
  together in the process account (`SdfResidencyGrant.ReserveGrowth`,
  `SdfWorldTables.MeshRegionLimitBytes`), never past
  `SdfMeshRegionStore.MostBytes` (`GpuRegion.MaxByteCount`, just under
  4 GiB), and the arena places nothing past it (`SdfMeshRegionArena.SetLimit`),
  so the region, the copy a growth holds until its upload completes and
  everything else the device holds fit together. A growth the account or the
  device refuses leaves the region as it is (`SdfMeshRegionArena.Clip`). It grows to its need plus an eighth, rounded up to a
  16 MiB granule and capped at the limit
  (`SdfMeshRegionStore.GrownBytes`, a pure function), so it reads
  fuller than a doubling would; `world.budget` prints the bytes the staged
  draws use (`MeshRegionUsedBytes`) beside the bytes held and the limit.
  The arena places each mesh in the lowest free run under the limit
  (`SdfMeshRegionArena`, over `GpuRangeAllocator`) and gives a forgotten
  mesh's words to the next mesh they hold, so a group changing tier moves no
  other mesh. A mesh that finds no run is refused
  (`SdfMeshRegionArena.Refused`, `refused` and `refusals` on
  `world.residency`'s landing line): its draws wait out of the staged
  records, which shows as a hole, and nothing throws. The bake keeps the
  named bytes under its budget so that a refusal is the exception (the tier
  bullet above); a run with `refusals` above zero has a demand the budget's
  law did not hold. A growth the device itself refuses under the limit
  (`GpuRegion.Grow`) still stops the host. The ceiling is the whole uints
  below 4 GiB less the staging
  buffer's header and run table, because a staged region's staging buffer binds
  as one storage view and Vulkan states a view's range in a uint32. `GpuRegion`
  counts bytes in a `long` and words in an `int`: a mesh's byte offset is
  `((long)VertexWord) * sizeof(uint)`, `Contents` refuses past `int.MaxValue`
  bytes, and a piece of the host copy is read through `ContentsAt(offset,
  length)` (`SdfWorldTables.StageChangedDraws` reads only the draw records that way). Host writes
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
  by binary search over their first samples. Each lattice names its output word offset
  and fields in the last word of its fifth row. Box refinement and root searches read
  distance, material cuts read material, fine samples and slabs read both, and measured
  normals and slices read distance followed by gradient triples. Slabs fill the final
  lattice after their ticket is claimed. The CPU sampler writes the same planes through
  `SdfMeshBakeRequest.WriteSample`. `Pump` runs on the frame thread
  (the GPU layer records and submits on that thread alone) at the top of each
  frame, before any residency prepares, and after each upload: it hands every
  job whose fence signaled to its client, which copies and decodes the samples
  on the bake's pool and releases the slot (`SdfGpuWorkReadback.Release`), and
  refills every released slot while the budget holds. Between two frames the
  host's wait takes the same step (`HostWaits.Pace`, the service's
  `WaitService`): the frame thread sleeps on its deadline and one wake
  together, and a job's fence (`IGpuSubmissionFence.SignalOnCompletion`), a
  lattice a worker readied (`SdfMeshPathBake.Offered`), a released readback
  and the budget's next credit each end the sleep early, so a batch is
  answered when it completes and the next taken when it is ready, whatever
  the frame rate. Such a step takes no job within `SdfGpuWork.HostBudget` of
  the wait's deadline. The budget is
  the share of the GPU's time the frame leaves free (`SdfGpuWorkBudget.Share`, at most `SdfGpuWork.GpuShare`), charged by each job's own
  timestamps (the bake's mean-following rate, from a prior of `NanosecondsPerTap`, until they land or on a
  queue without them), banking at most a short spell while idle. A job binds the
  latest upload's World set only while nothing it binds has moved since that
  upload and the tables' uploaded program is the bake's
  (`SdfWorldTables.BindsBackgroundWork`, `BackgroundProgram`): between a pack
  and its upload the slot's buffers are owed contents not yet sent. Every
  lattice is chunked to fit `SdfMeshPathBake.LatticeTaps` (final lattices in
  slabs, material and gradient, fine and material points in chunks). The done
  line's `gpu-work=[…]` reads jobs, their measured GPU time, the wall time some
  job was in flight and the GPU's share of it, the most in flight, the steps
  (`pumps`), those taken inside the host's wait and the jobs they submitted
  (`services`, `service-jobs`), the drives of held frames and their waits, the
  steps the World set could not be bound in, those the budget held, the jobs
  handed back for want of device room (`deferred`) and the waits a lease made
  the tables take (`lease-waits`, zero on one queue);
  `world.gpu-timing` does not see the work. A slot's buffers are the
  service's, created through one door that reserves them before the device is
  asked (`SdfGpuWorkRecording.HostBuffer`, `DeviceBuffer`, `CopyToReadback`,
  `ReserveReadback`; workspace under `SdfResidencySelector.WorkOwner`) and that
  creates nothing once the job has consumed state (`CopyRegion` takes a
  region's owed words): a job asks for every buffer, its readback reserved,
  before it copies a region, so a refusal hands it back whole. A client's own
  cache initialization (the probe hit cache's clear, the meter cache's fill)
  stays owed until a job that recorded it completes. Its readback and the
  bake's point table, sampled table, masks and samples grow to the slot's
  largest batch and go once no job has needed them at that size for
  `SdfGpuWork.IdleSeconds`, so a settled bake holds none;
  `world.residency`'s `gpu-work` line reads what remains and what is
  reserved. A client keeps what it wrote into a buffer, or the sets that name
  it, by comparing the buffer's object: a replaced one is another object. A
  batch the pool has no room for goes back to the bake (`Abandon`), which is
  offered a slot again once one comes back; a client with no slot in use
  reserves its batch as the floor. Admission of a group's final lattice waits
  on the pool's backlog measured as wall time (`SdfMeshBakeBacklog`): the
  stages queued and on their way against what the workers drain in an
  admission's lead. Gradients are measured only at the
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
  table, instance masks and samples) are written only while it is free. What a
  batch reads of the tables is retained through the device timeline. Background
  admission requires the tables' frame queue, so the same checkpoint covers
  frame and batch readers; an upload waits out a lease on the ring slot it
  rewrites (`SdfGpuWork.EndLeases`). A batch
  abandoned (a recording that failed, a disposed service) returns its lattices
  to their groups, which dispatch them again, and counts its samples as
  `returned`, or `discarded` once the GPU sampled them (a failed readback, a
  group retired while its lattice flew: GPU work spent for nothing);
  `Answered` settles a batch once.
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
  side-table values for offsets, materials by place, instance bounds in their
  declared frame and the bake turn for nonzero world-axis offsets, other slots
  by their rounded pose in the group's frame, lane-erosion
  lanes, the reach of a box it clamps, glass, and for a group holding a glyph the
  content identity of the atlas pixels its glyphs sample, `SdfProgram.GlyphAtlas`,
  which `SdfProgramBuilder.Glyph` takes from the atlas image's
  `FontAtlasImageData.ContentHash` and a retained emission carries, since a glyph's
  instruction holds only its cell's UVs; one texture binds for a program, so the
  builder refuses glyphs of two atlas images; each appended as a kind tag, its
  length and its payload through `AppendFact`, so no two fact sequences hash
  alike), and every meshed group is stored in the
  `SdfMeshLibrary` all of a presentation's residencies share, so a rebuild, a
  returning body or a body crossing from another world takes its mesh; only new
  geometry bakes. The key has two failure modes. A field the hash reads that a
  program assigns by position (a slot, a table offset, a palette index) breaks
  sharing silently: canonicalize it in the builder. Anything the bake samples
  that the instructions do not hold (another slot's transform, a lane, a table)
  leaves a stale mesh drawing after an edit: hash it in the builder. A
  stamp-pool blend group uses rigid delta slots and opts into the authored-rest geometry, so arrival
  poses do not change its key or its form's reference pose. Other dynamic instances retain their
  declared pose semantics. A geometry whose bake found no surface is held as empty
  (`SdfMeshLibrary.StoreEmpty`), so a group of it at another selector finishes at once. The
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
- **A published mesh is what the store answers.** `SdfMeshLibrary.Store` writes the entry and publishes the baked
  mesh in the form a decode of that entry gives (`SdfMeshStoreCodec.Conform`: positions on the entry's grid, unit
  normals through their octahedral words, every other plane exact), without reading the entry back, so a cold bake
  draws the mesh a warm boot loads (`AStoredMeshIsWhatItsEntryAnswersWithoutTheEntryBeingRead`), and every store-published mesh's planes
  (positions, indices, normals, triangle materials and every level's) can be let go once nothing has held them for
  `SdfMesh.IdleSeconds` and restored from the entry when a reader holds them again (`SdfMesh.Restore.cs`; the residency's store line reads `mesh-releases`, `mesh-restores`,
  `mesh-released-now` and `mesh-restore-misses`). Restores run on at most a quarter of the processors' pool
  items and decode into the blocks outside the storage's gate; any failure, an allocation's included, ends the storage lost. A
  library that publishes from a set pins it (`SdfMeshStore.Hold`, `CacheRetention.HoldSet`: a shared lease
  beside the set that no process's eviction passes) for the rest of its process. The held mesh's palette is
  the first taker's only when later takers can renumber from it (`SdfMeshMaterials.Remap`: the decode reads the entry in the first taker's ids, or in the stored ids when one collides); otherwise
  the store's canonical ids are held. A restore the store cannot answer loses the geometry
  (`SdfMeshLibrary.Lose`): the library forgets it, `LostRevision` moves, and each bake holding the library restarts on
  its program, baking again the groups whose meshes were lost (`Keeps`). A write the store refuses publishes the
  baked mesh, which keeps its parts.
- **The World's library persists across runs** (`SdfMeshStore`, the `meshes`
  device cache). `SdfMeshBakeIdentity` derives keys for sampling, contouring,
  simplification and landing. The final mesh is tried first. On a miss,
  `SdfMeshPathBake.ResumeCheckpoint` looks for the simplified mesh or the
  finished contour, including material cuts and skinning. Both use the
  lossless path in `SdfMeshStoreCodec.EncodeExact`. An earlier miss drives
  contouring normally, and it may reuse individual samples for any unchanged
  lattice or point request. The sample key (`SdfMeshPathBake.SampleInput`, the
  one derivation a probe and a write share) is the dispatch's packed lattice row
  and points over the group's `SampleKeys`, part of its `CheckpointKeys`
  (`KeyCheckpoints`: one immutable set taken from one program, replaced whole),
  which the take records beside each lattice it dispatches, so the reader that
  keeps the samples never reads the live group. A group a new program keeps
  mid-bake (`Place`, `Keeps`) carries on and is keyed again under that program,
  since its sealed geometry, and so its mesh, is the same under either. The keys hold the group's
  sample input (`SampleFieldInput`): its `Shape`,
  which the geometry hashes before the tier and the operands' poses, the
  program's packed field and step scale, its palette and its pose rows. A tier
  changes what a lattice samples only through those rows or the lattice's own
  row, so a detail tier takes its floor's coarse search and final slabs from the
  store when they sample alike. The take decides a probe under the gate from
  identity-sized facts alone: what the store knows of the sampling identity's
  sets (`SdfMeshStore.MayHoldSamples(identity)`: the directory listed once on
  the pool, or found missing, then kept by this process's writes and deletes),
  so a cold store dispatches every lattice in its first take with no file read
  and no hash. A lattice whose identity the store may hold goes to the pool's
  urgent queue (`LoadSamples`), which hashes its points (a request holds up to
  `LatticeTaps`, never hashed under the gate), checks and reads the set, while
  the group stays held (`Contouring`) so no stage moves the points; one that
  misses there dispatches in the next take unprobed. A dispatched
  lattice's samples, its contour and its simplified mesh go to the store's
  writer (`SdfMeshStore.KeepSamples`, `WriteCheckpoint`: a `BoundedWorkQueue`
  of two pool items, drained for up to ten seconds at process exit), never
  ahead of the answer or the landing; past `MostPendingWriteBytes` held, a
  checkpoint writes on its caller and a sample set is not kept
  (`store-writes-dropped` on the residency's store line). Every entry is encoded
  straight into its temporary file and written atomically without a flush to
  stable storage (`AtomicFile.WriteVerifiedCache`): its checksum refuses what a
  power loss leaves. A native checkpoint alone is encoded on its caller into a
  buffer the writer copies out, since its parts may go before the writer runs.
  The finished mesh's write stays on the landing, since a published mesh
  restores its parts from that entry, and returns the position planes it stored
  (`SdfMeshStoreCodec.Encode`), which the published native copy conforms to;
  its bound's enforcement runs on the writer. All products share the store's byte
  budget. The bake's done line prints hit/miss counts for the three intermediate
  stages beside its actual GPU dispatch count.

  The final codec's quantization (the position grid and octahedral normals)
  decides what every later run draws. The
  encoder's fit check, the decode and `Conform` expand quantized positions through `SdfMeshStoreCodec.Expand`,
  so the encoder's budget holds for what the decode writes and what a cold bake publishes. An edit to what a decode
  answers for the two lossy planes changes `Conform` in the same change as the decode. The kernels' source
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
  The closure starts from production entry methods. The contour walk stops at
  `SdfMeshPathBake.SimplificationStage.Run` and the simplification walk starts
  there. The sampling walk starts from the batch methods that pack the dispatch inputs. The final closure reaches group/geometry planning, lattice refinement,
  contours, material cuts, skin and levels. It follows every call, delegate and override of a type the bake constructs
  across the `Puck` assemblies `Puck.SdfVm` references; a call through an abstract or interface member reaches only the
  implementations the walk constructs, since the call runs on an instance some code made (a lane the bake posts its
  demand measure to does not bring in the residency's pack unit posted to the same kind of lane). It hashes each
  method's IL with its tokens written as the members they name, its locals, exception regions and loaded field data,
  and each named type's layout. A compiler-generated name (a lambda, a local function, a closure class, a cached
  delegate, a state machine) is written with the name of the method that declares it in place of that method's
  ordinal in its type (`CodeClosure`'s `Module.Canonical`), so adding or removing a member elsewhere in a type renames
  nothing the bake runs. A value type's layout counts whole (the bytes its
  code moves); any other type's counts only the fields the closure's code loads,
  stores or takes the address of, so a field the bake never reads (a
  presentation-only flag on `SdfFrame`) leaves the identity as it was, and a
  record the code merely copies with `with` has its synthesized clone and copy
  constructor hashed by signature (they read no field). A record the bake
  constructs counts its whole layout, since its synthesized equality and
  printing read every field. `SdfMeshBakeClosureLawTests` pins both ways. A
  field and a method added to `SdfMeshPathBake` that nothing in the bake reaches
  leave the identity as it was (`puck mesh identity --against`); check that way
  before relying on any edit being outside it. It
  stops at `SdfMeshLibrary` and `SdfMeshStore`,
  which hold meshes, at a mesh's claims and restorable parts (the stop types `SdfMesh/Payload` and
  `SdfMesh/Restorable`: `CodeClosure` stops a nested type named by its full name), and at `GpuRegion` and
  `GpuUploadRuns`, at memory accounting and storage (`MemoryAccount`, `HostMemory`, the allocators and
  `OwnedBuffer<T>`: admission paces when a group holds its final lattice, never what it meshes, and a buffer holds
  the words the bake's own code writes), at bake admission pacing (`SdfMeshBakeBacklog`), and at the runtime. It also
  stops at `SdfMeshBakeReport`, which words, counts and writes every `[sdf.mesh-bake]` line and owns the counters
  they report (`Accounts`): the bake hands it values (a group's selector, counts, outcomes) and never builds or
  writes text itself, so an edit to a line's words, or a new counter, leaves the identity as it was. A new line or
  counter goes in that type. What the bake stores for a line's later use (a group's `Outcome` text) is still
  its own code. The checkpoint keys also cover their storage format. The final mesh key reads the encoder and its
  quantization. The store decoder and background GPU scheduler can change independently. An edit to `Puck.Maths` code the contour calls moves the contour
  key and its downstream stages. Comments and checkout paths do not
  change the key, and worktrees of one bake share one set. Appearance is outside the key by construction: the
  closure's program root is `SdfProgram`'s only constructor, the field derivation, which takes no material or screen
  value, only an `SdfAppearanceOutline` (material count, glass materials, declared screens, the appearance section's
  size). `SdfProgram.Of` runs it and then attaches the `SdfAppearance` and packs its materials and paints into the
  stream's tail; the program's appearance is unattached while the field constructor runs, so a read of it there throws
  rather than joining the key. The CPU field evaluators the bake runs (the skin's influence round, the audit) read
  `SdfProgram.Excerpt`, which the field constructor derives from the outline alone with no appearance attached, so
  they reach no appearance code either. The kernels follow the same split: the stream is field first (`SdfProgram.FieldWords`),
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
  index shared by demand and dressing, copy the frame's demand inputs, revisit moved frame inputs, finish replacements,
  decide tiers from the last measure and post the next to the measure's lane, then return a stack-bound `FrameAccess`. Its consumer
  claims the landed meshes and publishes draws/picking before acknowledging that access's captured product revision;
  it reads the groups only while the access holds the gate, and releases it (`FrameAccess.Release`) once its rows claim
  every mesh it draws, so posing, clipping and publishing run with the gate open; a group read after the release throws.
  Disposal releases the gate without acknowledgment. Every mesh replacement, including a same-count or empty result,
  advances the revision. `Stage` chooses and slices the exact point/pose rows; its caller uploads the returned span
  through the generic region transport. No bake entry calls the dresser. This is a type boundary as well as a call
  boundary: applied code updates report types, including declaring types of nested helpers, so a partial/nested dresser
  would still invalidate the bake. `SdfMeshBakeClosure.Reads` includes producing types and excludes the standalone dresser,
  draw history, world tables, pass recorder and the view frame block; unknown applied-code updates remain conservative.
- **A library has a cache policy** (`SdfMeshCachePolicy`, `SdfMeshLibrary.Policy`,
  the World's `--mesh-cache`), which every bake holding it follows about a rung
  of a group's ladder the store lacks. `Demand` bakes the rung asked, an
  author's boot. `Ladder` also bakes every other rung between each drawn
  geometry's `FinestTier` and `CoarsestTier`, coarsest first, into the store
  alone (`SdfMeshPathBake.FillLadders`): a rung bakes in its group's place as a
  tier's replacement does, its product never takes the draws (`Group.Fill`,
  `Draw`), and the group it stood in for takes the place back
  (`FinishFill`). The detail is not settled while a ladder is unfinished, so
  `world.wait bakes` waits for all of them, and the hold names the rungs in
  flight, the ladders owed and the first group holding (`LadderHold`). Every
  replacement, a rung included, is finished at the place the group list records
  for it (`Group.Index`): a restart that keeps a replacement mid-bake sets it at
  the new program's index, and one left standing off `m_replacing` would hold a
  ladder forever. A residency besides the world's own
  holds the wait while it owes a group or is between rungs (`Filling`) and a
  view draws it (`SdfMeshPathBake.Driven`: its GPU work asked it for a lattice
  within a second); one no view draws lands nothing, so it holds no wait.
  `Stored` draws stored rungs alone, the policy of a build that ships its cache
  (a complete player build's default): a group arriving takes the nearest
  stored rung of its ladder, coarser first (`StoredRung`, in `Place`), a group
  asked another tier keeps what it draws until its demand names another
  (`StartReplacement`, `Group.RetierRefused`), and a stored entry the account
  had no room to read is never baked instead: a tier's replacement gives its
  place back (`Decline`), and a group with nothing to draw reads again after a
  doubling wait (`Reload`). Only a shape the store
  holds no rung of bakes, and live geometry (`Group.Live`): a group that reads
  an operand's pose, or one planned again because a sampled field it reads
  published another snapshot. The world makes that geometry as it runs, so no
  cache holds it; it bakes under every policy, has no ladder filled and is
  counted apart. Liveness and a reader's waiting successor belong to its place
  in the list, not to the group holding it: every group that takes a place
  (`SdfMeshPathBake.Replace`, and a later program's plan in `Place`) takes both
  from the group it displaces (`Group.InheritLive`), so a tier of a live group is
  live and a successor survives a replacement giving its place back. A tier
  start that plans nothing (a dynamic slot no active instance rides) marks the
  group refused like a missing rung, so no decision asks it again until its
  demand moves; only a replacement that started keeps the detail unsettled
  (`TierStart`). A `world.wait bakes` that releases unsettled names what holds
  each bake (`SdfMeshPathBake.DescribeUnsettled`: products ahead, successors,
  groups baking and how many are live, and the latest tier decision's hold),
  and `puck world player` records that reason per place. `world.counters sdf.mesh-cache` (`SdfMeshCache`) is the
  reading: `dispatches` and the `live-dispatches` among them, `groups-baked`
  and `live-baked`, `rungs-missed` and the `rungs-missed-live` among them,
  `loads-declined` and `rungs-filled`. A rung is missed at one door
  (`CountRungMissed`), which names it (`[sdf.mesh-bake] rung-missed`); a sampled
  field's reader asked at a snapshot the world then moves past
  (`ReclassifyReaderMisses`, on `Revisit`'s resample) is live geometry, and its
  misses move to `rungs-missed-live` with a `rung-missed-live` line. A boot
  of a whole cache under `Stored` dispatches only live lattices, bakes no other
  group and misses no rung but live ones, which is what `puck world player`'s
  check boots require.
- **A dynamic group's mesh is stored under its form too.** Its geometry holds
  its other slots' poses, which no later run stands in again, so
  `SdfMeshLibrary.Store` writes the mesh a second time under
  `SdfMeshLibrary.FormGeometry` (the latest replacing the one before; a group
  whose form is its geometry writes once), and a group whose own entry is
  missing loads that one (`SdfMeshPathBake.HoldsForm`, `Group.LoadsForm`), as
  `TryTakeForm` takes a form this run holds. A tier's replacement takes its
  form from the library the same way.
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
  time (`CacheRetention.EnforceSetsOnWrite`, which the store's writer runs after
  each entry stands): a set's last use is its most
  recently read or written entry's, and a write removes whole sets, least
  recently used first, keeping its own. A peer's write may evict a set being
  baked or loaded unless it is pinned (`SdfMeshStore.Hold`). An unpinned writer's
  set larger than the cap alone keeps its most recently used entries, including
  the entry being written. An entry left directly in the directory is a set of its own and ages
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
  tier of an anchor's view sizes its entries (`SdfShadowResolution`, `meshShadowTexels`), and an anchor holds the
  second slot's entry only while its world holds a second stable slot (`SdfWorldShadows`).
- **A view without shadows reads none.** A view whose quality skips shadows (windows, camera views) takes no anchor
  and its surface pass binds the filler; no view fragment holds a cascade.
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
- **A defect's class and its refinement flag are in the report.** Each flipped
  triangle, fold, off-surface vertex, open or non-manifold edge and uncovered
  crossing is sorted by geometry into `SdfMeshDefectClass` (`crease-sliver`,
  `narrow-face`, `off-surface-vertex`, `short-tip`, `other`), and with `--cpu`
  each is read through the group's `SdfRefinementMap` (what `SdfSampledLattice.Refine`
  flagged, `SdfMeshPathBake.RefinementObserver`) into `measured`, `ring`,
  `unflagged` or `lattice-unrefined`. Run it on a defect before deciding whether
  the fault is the trigger (items `unflagged`) or the contour (items `measured`
  or `ring`). The class is the geometry's reading, not the bake's cause.
- **`--cpu` measures a bake change with no GPU.** It drives `SdfMeshPathBake`
  with each lattice answered through the group's CPU field, so a contour,
  refinement or field change shows in the audit without a boot. Every lattice
  names where its gradient taps lie (`SdfMeshBakeRequest.TapSpacing`, a
  sixty-fourth of its finest cell); the kernel reads that spacing from the
  lattice's record and the CPU answers it too, so neither side decides it
  alone. The fixed-point evaluator answers in 2⁻¹⁶ m steps and cannot place
  taps closer than `SdfMeshCpuField.ResolutionSpacing`
  (`SdfMeshCpuField.TapSpacing`): there its taps straddle creases the
  kernel's do not, and the group's block prints a `cpu taps:` line counting
  those samples. Only a group without that line, and whose field the two
  interpreters compute alike, has a CPU mesh that should match a stored
  bake triangle for triangle. Read the others' folds and flips beside a store
  audit: blended crease normals can hide folds the device's one-sided normals
  produce, and can add flips the device does not have.
  A field the evaluator refuses (a regular polygon, a glyph, a sampled
  region) needs a World boot.
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
- **`rough` sees mottling the centroid checks cannot.** The shading check
  compares each corner against the gradient at its triangle's centroid, which
  the surface's own curvature over half a cell turns by up to the 30-degree
  threshold, so sub-cell relief of 5 to 15 degrees scored clean. `rough`
  measures each smooth vertex (copies with one normal; crease copies stay with
  the shading check) against the gradient at the vertex across a quarter cell,
  where a correct bake's normal is the gradient, and flags past 10 degrees. A
  CPU field that rounds a product of two lengths before dividing (a superellipsoid
  ratio `(|p| · min(r)) / r` rounded at the product craters every ellipsoid and
  sphere at centimetre sizes in `MeshCpuBake` and `mesh preview`) reads p50 near
  10 degrees; a sound one reads under 1. A loose bound multiplies the CPU twin's
  rounding by its slope deficit: a flare raises `rough` p99 to about 6 degrees
  on every primitive in a CPU bake, which the kernel's float field does not.
- **Audit changed collapse levels explicitly.** `--level 1` and `--level 2`
  select the triangles those forced render levels draw, clamped per chunk to
  its last level. Only referenced vertices count. The field coverage lattice
  and error thresholds retain level zero's bounds and cell; a coarser mesh
  must not buy a cleaner report by enlarging its error budget. `--images`
  draws the selected level too. The default still audits level zero.
- **`--cpu --field-levels` measures field levels that follow the collapse.**
  `SdfMeshFieldLevels.Contour` drives `SdfMeshPathBake.ContourLevel` (the
  bake's own lattice, refinement, crossings, contour, field check, crease
  split, material cut and planar merge on a coarser lattice at an outward
  iso-value), coarsest first and down to the first level past the kept share
  of the collapse's coarsest, flattening near-coplanar faces first
  (`SdfMeshPlanarMerge.Flatten`: the field level's gradient normals differ in
  their last bits, which the exact merge reads as separate faces) and
  vanishing components that would flip or open an edge past level zero's
  count. `SdfMeshFieldLevels.Measure` is the one place the plan's error
  contract is computed, for field and collapse levels alike; a vanished
  part's level-zero surface counts by its run's extent and is excused from
  enclosure and from the audit's missing crossings (`SdfMeshAudit.Audit`'s
  accounted surface). Flattening recovers little on an offset level: an
  outward offset rounds every edge into a strip no planar merge takes. The answers go through `ISdfMeshLevelField`, which the CLI
  implements on `MeshCpuBake`'s sampler and the shared tap stencil
  (`SdfMeshBakeTaps`), counting evaluations by contour and oracle and the
  thread work inside them (`ThreadCycles`). A field whose level-zero brackets
  break its step scale (`SdfMeshFieldLevels.Certified`) is answered at every
  sample; a member's part takes its own box, and a slot whose shapes only carve
  is no part (`SdfMeshAudit.MemberFields`). The offline bake answers the skin
  round with each lattice's own slot, so no CPU-baked group is skinned and every
  multi-slot group carries collapse levels a World's bake would not give it.
- **What it cannot read.** The render reading interprets every warp, fold and
  relief the renderer draws, a non-uniform scale, a sweep of any strand count
  and a path's edge table, each a sync pair with its `mapCore` case
  (`SdfFieldEvaluator.RenderOps.cs`, `SdfFieldEvaluator.Path.cs`); a group
  holding a regular polygon or star (runtime trigonometry), a glyph or a
  sampled region reports `field refused`, and one whose evaluation faults
  `field failed`. Mirrored groups (left and right) share one geometry and one
  stored mesh.

