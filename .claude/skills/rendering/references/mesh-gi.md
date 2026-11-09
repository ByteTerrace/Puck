# Mesh irradiance

The GPU relight, CPU reference and views shader share one probe lookup. Read this before
changing indirect light, probe visibility, the volume boundary or exposure.

The software trace device law compares scene instance and original
mesh triangle ordinals through a diagnostic output separate from the hit words.
Hit/miss, material, normal bytes and back-face flags are exact; distance and
reconstructed position use a scene-scale FP32 bound with a grazing-incidence
factor (`SdfMeshIrradianceTraceDeviceLawTests`). A different triangle must itself
intersect within that bound; the law reports near ties and rejects more than
0.1% of rays. It also skips a top-level subtree and requires rejection on each
backend. Runtime trace, relight, selection and exposure queries use
`SdfMeshProbeGpuWork` on the residency's shared `SdfGpuWork` service on both
backends. CPU `TraceHits` and `Relight` remain reference/diagnostic paths;
placement still uses CPU nearest-surface and short clearance queries.

The trace device law also dispatches the production relight kernel with
miss-only cached hits and 32 shadowing directionals, isolating its any-hit
light bits. Every bit must equal CPU `Unblocked`, including the empty scene;
both that output and nearest-hit tracing assert zero overflow. Writable
fixture buffers are device-local, with explicit upload/copy/readback ordering.

Boolean visibility uses `sdfProbeOccluded` through the same `sdfProbeWalk` node
and triangle tests as `sdfProbeIntersect`, returning at the first accepted
triangle. `probeClear` uses that entry for shadows, light bits and the meter.
The CPU twin is `Unblocked` through `Walk` and `ILeaves` any-hit leaves.
`Hierarchy.BuildNode` bounds both hierarchies to `StackDepth - 2` edges from
the root, switching to median splits when only a balanced subtree fits and
refusing a named hierarchy that cannot fit. Walks check before pushing and
count `sdf.mesh-gi.traversal-faults`; CPU overflow also throws. GPU walks also
stop at `SdfMeshProbeTrace.MaxWalkSteps` node visits across both levels and
count the same assertion, so a single-child cycle cannot hang a dispatch. GPU jobs reset
and read the assertion word after the job's logical output under its fence.
The entry point supplies its generated result stride; retained slot capacity
can grow without moving that word or causing a readback of unused capacity.
The device law injects an overdeep hierarchy into a larger output allocation
and requires the assertion to count it on both backends. Reset copies use
`CopyWrite`, followed by shader read/write access for the atomic increment.
`puck mesh scene` reports top/geometry depth and forced median splits. The
degenerate-bounds scene law reaches depth 62 and compares nearest and any hits
with brute force, with zero overflow.
The scene command reuses `LevelsOf(High)` and `SdfMeshProbeDepth.RayDirections`,
so its comparison traces use the volume's actual finest window and quadrature.
`world.budget` samples per-level median and maximum lighting ages when read,
in addition to the work-list cycle's combined ages.
Both work lists use the existing `Puck.Maths` three-way Morton interleave on
window-relative cells. Trace/relight class, lighting age and level retain their
priority; within each eight-cell radial band the order is spatial. A drain
still lists every cell and uses the same spatial order within each level.
For changing lighting, selection visits a stable cell phase every 1/4/8
completed sweeps by level. Phase uses the sweep ordinal, since lighting
versions can jump by a multiple of the period between sweeps and revisit the
same phase indefinitely. The sweep holds its initial phase
and interleave decision across empty or interrupted listings; a newer batch
lighting never fills the deferred phases within that sweep.
`sdf-probe-select.comp.hlsl` alone sets the GPU
due bit; missing geometry, rebase and unwritten publication bypass the phase,
and lighting age at the level's bound forces it even in the current sweep.
The bound is the authored age target times that level's interleave and is
reported in `world.budget`. A sweep beginning under the same lighting version
as its predecessor visits every phase so bounce propagation cannot stop on an
unvisited phase. The
CPU reference uses the same eligibility rule; lockstep always visits all cells.
Partial interleaved sweeps neither certify convergence nor consume the
full-window `MaxSweeps` limit.
The combined ages remain diagnostic totals; `behind` compares each level to
its own bound and reports the worst relative overrun. Its needed relight rate
is the sum of each level's probe count divided by that level's bound.
Relight priority uses age relative to the level's bound before level, capture
age and spatial band. Overload can exceed the targets; no probe retires on its
own small change.
Below the bound, age priority is weighted by 1 inside any residency view's
frustum and 0.25 outside all of them, and by 1.25 when the probe's last relight
moved past the existing convergence threshold. Overdue probes ignore these
weights. Camera snapshots come from the same frame as the work snapshot;
elsewhere cameras are excluded because their coordinates belong to another
world. The diagnostic additionally reports the first view's counts, median
and oldest ages against those outside that frustum at each level.

The runtime retains geometry addresses in `SdfMeshIrradianceScene.GpuArena`,
using the shared `GpuMemoryRanges` allocator. A replacement allocates its own
top hierarchy, braid entries and instance records, and only new geometry gets
uploaded. A worker packs these changed ranges into a bounded queue of
`SdfGpuWork.UploadChunkBytes` blocks. B1 admits their copies under its GPU,
host-time and byte budgets; `GpuRegion` remains the only uploader, with a
one-word CPU shadow and one staging slot per service slot. Chunk arrays are pooled
and returned only after their copy fence, or after cancellation drains the writer.
No hierarchy serialization runs on the frame thread.
`SdfMeshProbeGpuWork.TakeSceneUpload` excludes queries during replacement;
the old root and allocations stay intact until the new root's last upload
fence, then `CompleteSceneUpload` publishes and frees retired ranges. The root
is admitted only after the channel reports completion (all chunks read) and
every chunk fence retires. A stale scene cancels its writer; its obsolete prefix
is reclaimed after both writer and fences retire. Immutable geometry retains
fence-proven chunk progress in `GpuArena.Complete`, and the next export resumes
its remaining words, so repeated roster changes cannot restart a large geometry. Query
admission rechecks mesh residency and placement version after publication:
an intervening mesh relocation repacks instance addresses before any trace.
A changed mesh roster whose referenced vertex, index and material addresses
did not move owes no scene upload. The retained placement snapshot contains
only meshes that scene names.
`world.residency` reports completed scene changes, total payload bytes and the
last change's payload bytes, separately from staging allocation sizes.
One admitted query owns the shared scene and feedback regions until its fence completes;
they therefore have one staging slot. Service slots independently retain their
outputs through readback release. GPU hit-cache allocations carry nonrepeating
tokens, checked by selection before reuse. The cache is sized to the published
tier (`HitCapacityOf`: the window's cells plus two fresh slots per probe a drain
batch places). A placed probe's trace job copies its hits into a fresh cache
slot on the device (the first trace and the trial traces into separate slots,
`PlaceBatch`); its relight adopts the slot it keeps (`AdoptGpuHits`) and the
cache pass stamps the token, so hits read back for the CPU's surface map are
never uploaded again. A relight commits records only after `Stands` accepts
their scene/window, then frame upload copies the ready slots into the published
GPU table. The CPU retains depth/sky and diagnostic lighting, and releases full
hit arrays once the batch is relit. Exposure reads the GPU meter's small answer
rather than traversing the hierarchy on the frame. A job whose scene's meshes
are not resident within `AdmissionWait` is cancelled like an obsolete one, so it
never holds the coordinator or the meter behind it without bound.

A relight's GPU time is rays, not probes: each hit texel traces toward every
shadowing light that reaches the probe, and a probe that cannot reuse its
local cube traces toward every point light in range. `RelightGpu` predicts each
probe's rays and submits a batch as consecutive jobs of at most
`RelightRaysPerJob`, each committed before the next. A batch cut by probe count
alone (384 fresh probes beside the stage, 78 point lights in range of each) ran
one dispatch past the device timeout. The walk's step bound bounds one ray, never
a dispatch. A slow job prints `[sdf.gpu-work] slow`, a device loss lists the
jobs in flight, and a job whose walks stopped at their bound prints
`[sdf.mesh-gi] traversal faults`.

`SdfWorldResidency.LightingPending` latches convergence after preparation for
the whole frame, including the final meter. `SdfWorldPasses.WaitingOf` feeds it
to render-graph completion. A worker finishing later cannot certify an image
whose exposure was held. Readiness waits for the next prepared frame, while
the service keeps pumping. A settled PNG must have a capture completion line
after the main residency's done line; a nested residency's done line is insufficient.

| Part | Owner |
|---|---|
| Static scene and ray visibility | `SdfMeshIrradianceScene` |
| Volume coordination, CPU reference and bounce lookup | `SdfMeshIrradiance` |
| Device trace, hit cache, relight, selection and exposure meter | `SdfMeshProbeGpuWork` |
| Probe record and generated shader constants | `SdfMeshIrradianceProbe`, `SdfIsaHlsl.DeclareRender` |
| Pixel gather, indirect light, reflections and traced-light visibility | `shade/sdf-mesh-irradiance.hlsli` |

- The scene hierarchy fills its packed seven-primitive leaves before adding
  another pair of boxes. Bounds and triangle tests keep their original precision.
  Compare `puck mesh scene`'s hits, lit hits and mean distance after changing its
  storage or traversal, and walk the settled heap to measure retained arrays;
  working set includes temporary build storage and the mesh arrays as well.
- Keep `Incoming` and `sdfMeshProbesAt`/`sdfMeshIrradianceAt` together. A sky
  fallback in the CPU read feeds later bounce sweeps even if the final pixel
  read rejects it.
- Scene readiness is `SdfMeshPathBake.IrradiancePending`, not the whole bake's
  `Pending`: only opaque static groups enter the scene. An animated rebake,
  glass or a diagnostic slice must not postpone its first measurement or a
  refresh. While opaque static groups are still pending, a frame without lighting convergence
  builds the scene from those landed (`ScenePendingDue`: no build running, the
  previous scene's trace finished, at least `PendingSceneInterval` since the
  last build began, and at least one static draw: an empty scene would measure
  open sky where walls have yet to land), so the volume and the meter answer
  during a cold bake instead of after it. Waiting for the whole bake left every
  view `meter=none source=held` and every shaded face black for the ten minutes
  of a cold debug-world bake. A lockstep frame still waits for the whole bake.
  `world.wait bakes` still waits for all geometry; an initial
  `world.exposure` reading with `meter=none` is not a backend-parity result.
- The volume has three levels (`SdfMeshIrradianceProbe.Levels`), a table per
  tier (`SdfMeshIrradiance.LevelsOf`, rows of `SdfMeshIrradianceLevel`: a
  spacing per axis and a window's dims): the tier's window, a storey level of
  4×2×4 m cells over 32×24×32 and a 32×12×32 window of 32 m cells, their slots
  after the finer levels' in one table, each described by a
  `meshIrradianceLevels` row (first cell, packed dims, ten bits each) and a
  `meshIrradianceSpacings` row (xyz spacing, zero until the level first
  publishes); `meshIrradianceVolume` is the enabled flag. Each level publishes
  on its own once every slot of its window is measured. Every rule that sizes
  by spacing takes the vector: `Lattice`, `Beyond` and
  `sdfMeshIrradianceBeyond` divide per axis, `SurfaceReachOf` is 1.5 cell
  diagonals, `Place` searches a cell whose half extent is per axis.
  A receiver's stages run finest first (`Select`, `sdfMeshProbesAt`): each finer
  level whose boundary probes' surface maps reach it past their window
  (`SdfMeshIrradiance.SurfaceReachOf`), then the level whose window holds it
  (the coarsest past every window) bounded, the same probes unbounded
  (`SdfMeshProbeDepth.Visibility(bounded: false)`: a miss no longer rejects a
  receiver past its reach, but a measured hit still blocks), then the finest
  level's boundary probes unbounded. The first stage that answers is chosen in
  its share: its weight over the same cage's front weight before visibility
  (trilinear × the facing fade without the behind floor, over every present
  probe, invalid ones included), clamped to one, and for a finer level past its
  window times its reach share (`ReachShareOf`, `sdfMeshIrradianceReachShare`):
  whole up to `SurfaceReachFadeStart` (half) of its reach, smoothstep to none at
  the reach, so the remainder takes over across a band instead of the one-row
  sawtooth a hard cut at `SurfaceReachOf` drew along walls
  (`TheFinestLevelHandsAReceiverPastItsWindowOverAcrossItsReach`). The rest comes from the next
  stage over another cage, normalized to the rest; a later stage of the chosen
  level reads the same probes and is skipped, so every receiver inside the
  finest window keeps one gather. Where no remainder answers, the chosen stage
  is normalized to the whole. The receiver reads sixteen slots
  (`SdfMeshIrradiance.GatherSlots`: the chosen cage, then the remainder's),
  pre-scaled so they sum to one; a traced static light's visibility is the
  weight of the lit slots of bounded stages. A shadowing directional (its
  `Shadows` flag) has a bit too, the probe's own ray toward it at the hits' reach,
  marked on every relight (`Relight`, a reused local part included, since a moving
  sun moves it): `sdfLightResponse` reads it in place of the cascades in a view
  that draws none (`SdfShadeSurface.cascaded`: `meshShadowCount` zero, or soft
  shadows disabled, as a session view's `ReducedQuality` and `world.shadows off`
  are). The gather device law compares the sun's visibility on both backends. `world.probe` prints `level=`,
  `measured=`, `share=`, `remainder-level=` and `remainder-measured=`, and the
  remainder's eight corners after the chosen eight. An isotropic 8 m level could
  not resolve rooms a few metres tall: it had a probe plane in every other
  4.5 m storey, so the storeys between read one corner across a slab
  normalized to the whole. At the High tier the finest window about the middle
  of the gallery hall ends just short of its west wall (`body.pose 31.25 4.57
  28.52 273`, `world.indirect high`). A black or blown far wall is a level
  choice, which `world.probe` names, not a GPU/CPU disagreement. Never let a
  coarser level answer inside a finer window: at 25 m a 32 m probe's angular
  tap is metres wide, its patch test cannot resolve a thin wall the fine probe
  measured, and the wall law reads visibility 1 through it. A receiver with no
  answering probe never reads black: the extended stages answer far gallery walls
  and outdoor shaded faces past a window, and a receiver no stage answers reads
  the camera sample's light (`sdfMeshAnswers`, below).
  `SdfMeshIrradianceGatherDeviceLawTests` runs both gathers on one published
  volume and requires the same sixteen slots, weights, share and measured flags
  on both backends; `SdfMeshIrradianceLawTests.FarField.cs` holds the four-storey
  hall's segment-test counts.
- Each level's window centres on the box of the cameras of every view the
  residency renders when the box fits in the window's middle, and on the first
  view's alone when it does not (`WindowLow`). For the finest level the first
  view's camera is taken a quarter of the window's horizontal extent ahead of
  it along its horizontal forward (`WindowAnchor`); the coarser levels' windows
  already reach past the finest one's ahead. Centred on the camera, the Medium window ended 32 m ahead, and a
  view down level 1's hall from its west end read the far ceiling and floor from
  the storey level, whose 4 m probes 1.75 m under the ceiling see it past the
  hanging strips at grazing angles: jagged dark streaks beside each strip, and a
  floor 2 to 3 times too bright under the lamps. The first camera itself stays
  in the middle too: the window keeps its place only while the camera, its
  anchor and the views that fit all stand in the middle, and a recentred window
  shifts as little as puts them all there (`WindowLow`, `Centre`). Turning in
  place moves only the anchor, so the window recentres only as the anchor leaves
  its middle (about every third of a turn); walking backward recentres before
  the camera nears the back face (`TheFinestWindowKeepsItsCameraInItsMiddle`).
- A cold bake with `world.wait bakes` still pending, `sdf.mesh-gi.scenes=0`,
  and skipped `sdf.world$mask`/`sdf.world$bake` work is not by itself a stall.
  Read the wait's bake description twice: its meshes, pending groups,
  dispatches and samples distinguish progress from a stopped scheduler.
  A cold bake whose draw count keeps rising without finishing can be starved
  by simulation catch-up: `host.frames` shows several catch-up steps a frame,
  and `world.rate pause` on the boot instance restores frame time and bake
  progress. Pause only a
  diagnostic run, never the owner's play session, and disclose that change
  when comparing times. A nested world's `[sdf.mesh-gi] done` does not certify
  the gallery: check the main residency's counter and meter too.
  The scheduler's separate saved-pose/input-capacity defect has a focused
  boundary test; it is not the established cause of this slow gallery boot.
  See [mesh baking](mesh-bake.md#groups-poses-and-materials).
- A probe's geometry is one hit buffer (`SdfMeshProbeDepth.TraceHits`): one
  closest-hit ray per 12×12 octahedral texel at full reach, each hit an FP32
  distance (negative for a back face, infinity for a miss), its material and its
  normal's two signed octahedral bytes. Everything geometric derives from it:
  the six sky shares (misses, weighted by each texel's solid angle and cosine,
  `SdfMeshProbeDepth.RayWeights`), the inside share (back faces), and the
  surface map views test receivers against (`SdfMeshProbeDepth.Surfaces`): the
  hits within `1.5 * sqrt(3) * spacing + NormalBias`, FP16 distance beside the
  normal bytes, a negative distance marking a miss within that reach. Receivers
  beyond that measured support receive nothing. There is no second ray set. The
  hits are traced once per geometry (a scene build, a layout or reach change, or
  a cell new to a window) and never for a light change.
- A probe's light is a relight of its hits (`Relight`, `Shade`): the radiance
  cube, the sky-light cube and the traced-light bits, shaded under the current
  lighting. Every texel whose ray missed carries the sky's mean radiance over
  that texel's solid angle (`SdfMeshProbeDepth.SkyRadiance`, 4×4 samples of the
  lighting capture's sky map, the sky gain applied; the relight's
  `SdfMeshProbeRelight.SkyRow` table on the GPU) into the sky-light cube with
  the same cosine weights as a hit, so the indirect light a receiver reads
  (`sdfMeshIndirect`, `Incoming`) is the two cubes summed. The sky shares stay
  geometric and weigh only the reflected environment and the meter's open share.
  Never multiply a share by the hemisphere's SH irradiance for diffuse light:
  the sky's radiance and its occlusion are correlated (the horizon is bright,
  the ground below it is geometry, a window low in a wall sees the horizon), and
  the product read a north window's light at about half the path-traced value
  while reading the lawn through the same window exactly. The
  lighting version advances on a change of the directional or traced lights,
  the gains, the sources, the bounce switch, the scene's materials or the sky
  (the packed sky's key, then the sky texels themselves: `Lighting.SkyMatches`
  compares exactly what the relight and the meter read, so a kept capture never
  holds texels a replacement would change). `render.indirect.lightingGrid`
  authors direction steps in degrees and fractional radiance steps; zero is the
  neutral engine default. `SdfIndirectLightingGrid` snaps the directional records
  once for both the key and capture. The sky texels share a quantum per colour
  channel, anchored to the current channel peak's power-of-two range; tiny
  texels do not each trigger a new capture. These are functions of the
  current frame, never the last capture. Gridded lighting uses this policy in
  asynchronous and converged modes without the wall-clock capture throttle.
  Converged sky comparison is exact, with or without a grid. Only continuous
  asynchronous skies retain `SkyTolerance` against the preceding capture, per
  texel and channel relative to that texel's own value, so a probe that sees
  the sky through a few rays still relights when those rays' sky changes.
  A relight's bounce reads the volume about each hit as it stands, so
  the bounce is one iteration over the whole window: a sweep relights every
  probe once, and another follows while any probe of the last moved past
  `ConvergedShare` (plus `ConvergedFloor` nits) in any colour channel of any
  axis of either cube a relight reads (`Moves`: the radiance and the sky light,
  so an open probe whose sky light alone changed still owes the sweep its
  gatherers relight in), or was relit under a lighting since replaced; it stops only when a
  whole sweep under unchanged inputs moved nothing, or after `MaxSweeps` (an
  `[sdf.mesh-gi] unconverged` line). Convergence is never a probe's own: a
  probe that read zero before the light reached it is relit by the next sweep,
  and a geometry change wakes every probe. Never retire a probe on its own
  small change (the baffled-rooms and red-to-green laws in
  `SdfMeshIrradianceLawTests.Relight.cs`). A switched-off lamp's bounce decays
  over the sweeps, under a thousandth of the lit value in the point-light law.
  `render.indirect.bounces` zero switches the bounce off; any other value
  switches it on.
- The worker returns, never spins: when the budget leaves a probe's trace
  unfunded it waits for the next grant, and a lighting-converged frame asks it to yield
  at its next batch and takes over its work (`m_yield`, the spent-budget law).
- The cube is two parts. The local part (the traced point and spot lights'
  bounce under their shadow rays, and emission) is kept per probe with the
  lighting's local version, which advances only when those lights, the gains,
  the sources or the materials change; a relight under a moving sun reuses it
  and traces no shadow ray toward a point light. The directional lights and the
  bounce are shaded every relight. A probe rebased across a scene change (its
  hits kept) shades its local part again, since its point-light shadow rays may
  cross the change. `world.probe`'s volume line prints `lighting-version` and
  `local-version`.
- A scene build records the world boxes of the static draws it added or
  removed (`ChangedBoxes`). A probe traced against an earlier geometry is
  retraced only when one overlaps its placement region (the cell ± 1.5 spacings
  and the clearance) or meets one of its rays before the hit the ray recorded
  (`Affected`, with a millimetre margin so a hit on the draw's own face counts);
  every other probe keeps its hits and is relit. A layout or reach change, a
  draw with no box, or a probe older than the last `MaxChanges` changes
  retraces.
- Four reflected angular taps reconstruct local surface patches from first-hit
  positions and signed octahedral normal bytes. Join taps only when their normals
  agree and the symmetric point/normal residual is within `DepthSurfaceJoinGap`:
  opposite faces and separate parallel walls stay separate. Interpolate a patch's
  positions/normals before testing the signed receiver gap; extending each tangent
  plane separately shadowed visible curved receivers (the sphere law is red at
  visibility zero on that implementation). A sparsely sampled curved surface
  also needs its normal gradient. During the first sweep, fit curvature from
  compatible points/normals in each texel's reflected 3×3 angular neighbourhood
  and store one FP16 value per texel; an unsupported neighbourhood stores NaN.
  The gather interpolates compatible local quadratic coefficients and intersects their entering root along
  the receiver ray. Testing only a tangent/chord plane returned 0.574 visibility
  for an exact-segment-visible receiver in the small-curve witness. An unsupported
  tap casts no blocker; a distinct local hit supports the normal-gradient fit.
  Curvature zero retains the exact plane intersection. The common-plane fast path
  reduces that same quadratic to one signed gap and reuses the decoded normal,
  avoiding four general fits in CPU bounce reads and pixels. Fade a patch over
  `DepthSurfaceFade` (2 cm, less than normal bias), then combine its blocked angular coverage.
  The flat-surface law also rejects radial-distance interpolation: it returned
  48% visibility for a coplanar receiver at radius 1.2 m and angle 89 degrees.
  No radial moments or squared-distance field remain. Misses supply no blocker.
  Blocked taps close by one rule a level (`SdfMeshProbeCoverage.Visibility`,
  `sdfMeshProbeCoverageVisibility`). At a coarser level the taps on solids close
  from half to three-quarter support, so a metres-wide minority hit cannot extend
  a finite block over the footprint, times the taps on sheets from zero to an
  eighth. At the finest level the taps on solids close smoothly over their whole
  share (`DepthFinestCoverageStart`, `DepthFinestCoverageScale`), so a probe's
  weight changes across a texel as the receiver passes a furnishing's edge. The
  finest probes stand among a room's furnishings (level 1's 2 m lattice puts a
  plane 0.45 m over the floor, below the gallery's plinth tops); the majority
  ramp, closing over a quarter texel, turned each such probe's view past a
  plinth into a hard-edged wedge across the floor and the faces beside it,
  wherever that probe's light differed from its neighbours' (the floor under a
  strip reads 1.6 times the floor between strips, as a line source's cosine law
  predicts). The residue is a soft wedge where a probe stands just behind a
  furnishing's edge (`world.probe` names it with `reference-clear=0`): a point
  probe there is truly blocked, and only a probe of finite extent (a penumbra
  over its cell) would blend it out. A coarse level's
  trace (`SdfMeshIrradianceLevel.Sheets`) marks a hit a sheet in the low bit of
  its texel's curvature half (`SdfMeshProbeDepth.Surfaces`; every reader masks
  it off before decoding the curvature) when the scene holds another surface
  within 0.45 × `DepthSheetThickness` (2 m) of the point half that thickness
  behind the hit: a slab, a wall or a slab's edge. A hill's near bulge reads only
  its own surface there. Each measured case decided the rule: the conservative
  ramp on every 32 m tap closed 254 of 1,439 exact-clear corners on the CPU test
  hills (`TheCoarsestLevelKeepsACurvedSurfaceItSeesLit`), the tolerant one let 238
  answering 32 m corners light the four-storey hall through slabs
  (`ACoarseProbeAnswersNoReceiverAcrossASlabOrAWall`, `far-exact-blocked`); the
  sheet rule leaves 6 and 18. A thickness measured along the normal alone read a
  slab's edge 12.6 m thick and left 126 of those leaks. The finest level marks no
  sheets: marking them closed a receiver beside a 0.15 m hanging panel
  (`AFiniteBlockerShadowsItsOwnDirectionsAndAWallStopsTheFarProbe`).
  `ASurfaceMapMarksASlabASheetAndAHillsNearSideASolid` checks the marks.
  `Affected` extends each ray by the sheet thickness, so a change behind a hit
  retraces the probe. Tests remain confined to these four
  angular taps: no 26-ray whole-sphere planes, neighbour separation, suspect sets
  or cross-probe vetoes return. Keep `SdfMeshProbeDepth.Visibility` and
  `sdfMeshProbeDepthVisibility` together, including signed normal decoding,
  seam reflection, patch compatibility, gaps and coverage. Projection/addressing
  reuse `SdfOctahedral` / `field/sdf-octahedral.hlsli`.
- Gather weight is the cell's trilinear share times
  `max(smoothstep(0, |spacing * normal| / 2, dot(normal, probe - receiver)), BehindShare)` times depth visibility.
  This continuous positive-hemisphere weight turns down probes behind a surface
  without switching the whole gather between front and back sets. It also
  prevents grazing depth uncertainty behind a thin wall from admitting a bright
  exterior probe. The floor (`SdfMeshIrradianceProbe.BehindShare`) applies at
  the finest level alone, whose depth test resolves the receiver's own surface;
  a coarser probe behind the receiver's plane is as likely beyond the receiver's
  own wall, and the gather's normalisation would let one such probe light the
  whole receiver with the outdoors. The floor keeps a
  receiver whose every visible probe lies behind its plane from reading black:
  a downward rim at the ground (a root, a shrub's underside, a fern's base) has
  only the ground-hidden probes below in front of it, and without the floor both
  gather passes summed to zero. `world.probe x y z 0 -1 0` just above the ground
  reads `answering=0` when that regresses.
  Squared angular cosines gave probes relocated close to a floor a strong pole
  directly above each lattice centre; the distance fade removes those repeated
  floor spots without changing the stored lighting. Past the boundary, blend
  this distance fade toward one over the first cell outside, as below. The CPU
  bounce read and pixel read use the same rule, without squaring the final weight.
- At the volume boundary clamp the lookup, then test the actual receiver,
  pushed `NormalBias` (0.05 m) along its normal, against depth. Never bias by
  spacing or test the clamped position: either can carry a receiver through a
  thin wall. An unwritten or invalid probe contributes nothing. A depth miss has
  finite reach in the measured read; the extended read (above) drops only that
  bound.
- Past the boundary the facing term eases to one over the first cell outside
  (`outside` in `sdfMeshProbesAt` and `Gather`). Every boundary probe lies on the
  window's side of such a receiver, so the hemisphere weight would light only
  the normals turned toward the window: a body or surface out there reads half
  dark, split at the plane that faces the window. A body posed ahead of the
  window's refill stands outside the published window until the moved cells are
  measured (`world.probe` answers `unsettled` meanwhile): on a fresh boot whose
  first trace ran at the spawn, a pose across the hall reads the spawn's window
  for the refill's duration. Reproduce a boundary finding by posing past the
  published window and taking a presented capture before the refill lands; a
  settled capture or a pose before the first trace hides it.
  This easing applies only past the coarsest window, the one level a receiver
  outside reads; inside it the coarser levels supply far-field coverage, and the
  extended read lights a receiver past every probe's reach.
- Read `world.probe <x> <y> <z> [<nx> <ny> <nz>]` for each corner's origin,
  offset, back-face share, validity, sweeps, cubes and weight terms.
  Each valid corner also reports an exact mesh-segment `reference-clear` check,
  for diagnosing depth sampling separately from the lighting stored in its cube.
  This reference never changes the gather. A changing trace or pending publication reports `unsettled`, with both the
  published GPU window and live CPU window, rather than reporting the replacement
  cache as the rendered volume. Take lighting-finding captures only after bakes
  and irradiance settle; lockstep drains the window before rendering.
- Judge a visibility change at the `finding-sphere-step` and `gather-*` tour
  stops in `indirect` and lit views on both backends. Use the same fixed camera;
  a chase view without recentering can face the opposite way after teleporting.
  Include the boardroom's far doorway, stairwell flight B and teal roof/floor.
  Compare the settled GPU volume, exposure and frame time as well as pixels.
  Settlement means the configured sweeps finished, not global convergence of
  every cached cell. Match the visit order and record the published window:
  completed cells retain their measurements when a move adds new cells, and
  bounce reads use the current cache clamped to that window. A full tour and a
  direct teleport need not have the same bounce history.
  Match the executed camera commands too: replacing an unanswered input recenter
  with `view.recenter 1` resets the authored orbit/boom and can reverse a chase
  view. Recapture the baseline with the same measured commands before attributing
  those image differences to lighting. Explicit camera eyes/targets avoid this.
- The exposure meter checks each probe's visibility from the camera through
  the existing scene. A plain trilinear interpolation meters the room across
  a thin partition when the camera approaches it.
- When none of the eight probes answers, the meter traces a hit buffer at the
  camera with the same rays as a lattice probe and relights it with the same
  materials, shadow tests and bounce cache. A small enclosure may contain no
  lattice centre; searching farther or holding the previous room's exposure
  cannot measure its light. The hits are cached by camera cell and geometry,
  with at most one slot per view, and relit when the lighting version changes
  or the volume settles. Looking around reweights that measurement; moving
  within its cell reuses it only when the scene sees the camera from the
  sample's actual origin. A blocked segment requires a fresh measurement, even
  within one cell. `world.counters sdf.mesh-gi` counts `camera-traces` and
  `camera-relights`; a moving sun adds relights, never traces.
  At a camera cut the meter reads `fresh`: a sample is reused only from the very
  position, so the reading after a teleport depends on where the camera stands,
  not where it stood. `SdfViewExposures` keeps a cut owed until a reading
  arrives and snaps to that first reading. The runtime meters on the GPU
  asynchronously (`ScheduleGpuMeters`, at most every `LightingInterval`, at once
  after a cut): each view reads its own latest row wherever its camera now
  stands, so a moving camera stays `probes` or `camera-trace`, never `held`; a
  `fresh` read answers only from a row asked after the view's cut revision.
  This camera sample never writes the volume or relaxes surface visibility.
  If the scene is unavailable or the camera is inside opaque geometry, retain
  the previous exposure, local-adaptation meter and sky visibility. The authored
  EV is only the initial value before a reading. `world.exposure` distinguishes `probes`,
  `camera-trace`, `held` and `initial`; the last two report `meter=none`.
- A settled frame with no opaque static draws installs an empty scene and
  retraces its probes against open space. Empty is known geometry, distinct from
  a scene not built yet; removing the last draw must discard its occlusion and
  bounce light, even when dynamic or glass draws remain.
- A geometry or lighting change never clears the cache, and no landed batch is
  thrown away for a lighting change: a batch takes the latest lighting and
  lands whatever version it relit under. The worker (`Work`) lists the windows'
  probes that lack a trace against the current geometry (finest level and
  nearest first), then those whose lighting is stale or whose bounce has not
  converged (oldest lighting first), and works through the list a batch at a
  time while the frames' budget (`render.indirect.budget`, `SdfIndirectBudget`)
  grants relights and traces. A lighting that changes every frame keeps the
  relight cycling over the window; overload lengthens the lighting's age, not
  the frame. Each cycle's start records the window's age (median and oldest,
  `world.budget` and `Describe`); a level past its scaled age bound counts
  `behind` and prints `[sdf.mesh-gi] behind` (at most every ten seconds) with
  that level, its bound and the relit and needed rates. A re-traced probe keeps drawing its previous
  measurement until its trace lands. Clear the CPU cache and staging volume
  only when the layout changes. On a resize or window move, retain the uploaded
  GPU table **and its old coordinates** until every new slot has a measured
  sample. Never publish unwritten cells as darkness or initialize them to
  unoccluded sky. Until a level first publishes, no level has spacing, and views
  still shade by the volume's model, never by an unoccluded sky ambient. The volume
  measures at every tier: Off measures the Medium layout and applies none of its
  light (`SdfFrameBlock`'s `indirectApply` intensity zero, which also brings in
  the authored fill), while its probes keep occluding the traced lights, the
  reflected sky and, with no cascade, the sun. Without the volume nothing occludes
  any of those indoors. A surface no
  published probe answers (`sdfMeshAnswers` in `shade/sdf-light.hlsli`: every
  surface before the first publication, and surfaces past every published probe
  after a camera cut until the moved windows land) is lit by the exposure
  meter's sample at the camera as one probe. Its diffuse light is the sample's
  six-axis RGB light (`meterLight`, `SdfViewExposures.LightOf`: the radiance and
  sky-light cubes summed, its open rays carrying the sky's own radiance as a
  probe's do) read about the normal by the probe cube's rule (`sdfMeshCubeFace`);
  its reflection is the environment over the share it sees open (`airLight`)
  and its metered luminance (`meterLuminance`, grey) over the rest; its traced
  lights go unoccluded. No diffuse reader multiplies a share by the sky's SH. The
  meter sets the exposure from that same light, so a cold room reads as the
  meter measured it, never white and blue-washed under the unoccluded sky. `world.indirect high` (or back to medium) resets the layout,
  which reproduces the unpublished state on a warm boot. Before the meter's first reading `meterLight` reads W zero,
  `airLight` one and the luminance zero, so the first frames of a boot still read the open sky.
  Only pixel passes read it; a relight never does. `world.probe` answers `unsettled` only while geometry is owed (a scene
  build, a trace, a refill, a traced probe not yet published); a relight in
  progress reads as `relighting=1` on its first line.
- A frame requesting `SdfFrame.ConvergesLighting` (`Drain`) relights its whole window from no bounce, each
  later pass relighting every probe from the one before, until a whole pass
  moves nothing past the bound or `MaxSweeps` passes, whenever its geometry, lighting, windows or layout
  differ from the last drain or asynchronous work relit since. Its light is a
  function of those alone, never of bounce asynchronous frames left; the law
  `AConvergedLightingFrameRendersTheWindowTracedToTheEnd` compares a fresh volume's
  words with a warmed one's.
- A record is `SdfMeshIrradianceProbe.ProbeWords` words: the key, sky shares,
  radiance cube, light bits, origin and sky-light cube before
  `SdfMeshIrradianceProbe.DepthWord`, then the packed first-hit surface words and
  packed normal-gradient words; `world.budget` and `world.probe` print the
  table's bytes per tier. The GPU runtime releases the CPU hit array once the batch is relit, keeping
  depth/sky and diagnostic lighting (`ProbeBlock` and `LightBlock`); the CPU
  reference keeps the full hits too. `world.budget` derives the retained block
  cost from those types. A level's cache holds its window grown by the
  window's band (`MarginOf`, dims/8 a side) on every side: when the window moves,
  `ProbeCache.Retain` publishes a compacted table of the cells inside that box once
  the cache holds more probes than the box has cells (at Medium 92,800 probes over
  the three levels). `world.residency`'s `probe-cache-*` line reads it. A batch's
  depth/sky and light blocks stay alive while any kept probe names them, so
  bytes per kept probe can exceed their nominal payload.
  The published table (`SdfWorldTables.PublishMeshIrradiance`) is copied on the
  device from the feedback table; no frame uploads it from the host. It fits
  the published volume exactly, is cleared before its first copies, retires
  through the frame ring and increments the binding revision when replaced;
  until the volume first publishes into a residency's tables the World set
  binds the dummy buffer and the parameters are disabled. Keep the old table
  and coordinates until every new slot is measured. A device reattach brings
  new tables with no table: nothing publishes until a completed relight carries
  the volume's full snapshot into the new feedback, then every slot republishes
  at once (`RepublishAll`).
  The GPU feedback table and the published table are distinct: a relight reads
  completed prior batches, while views retain their last complete window.
  Feedback has a CPU shadow for reset snapshots; hierarchy staging streams
  from the scene arrays with only a root-word shadow. Upload buffers shrink
  after small flushes. `world.residency` reports scene, hit-cache and feedback
  device/upload/shadow bytes separately. `trace-scene-cpu-array-bytes` counts
  actual hierarchy array payload, including retained geometry, excluding
  dictionaries, shared bake meshes and temporary build workspace.
  `world.probe` reports publication and CPU-volume bytes. These logical holdings
  overlap process/account/backend readings; never add those readings together.
  Publication copies changed slots only, coalesced into runs.
- Each published probe contains its own measured cubes, exactly as the CPU bounce
  read sees them. Sky, surface radiance and the surface map come from the same
  hits, so an opening cannot count as sky in one sample set and a bounced hit in
  another. Every probe uses the same octahedral texel directions, weighted by
  each texel's solid angle, at both tiers; cell-specific random rotations
  stamped spatial noise into homogeneous lighting. Sharing directions
  correlates sampling error without borrowing any neighbour's light. Camera
  samples use the same quadrature. Placement uses shared short rejection rays bounded to one spacing, never lighting reach.
- A probe stands in the open (`SdfMeshIrradiance.PlaceBatch`). A centre inside geometry
  or within `Clearance` (0.1 m) first tries the nearest surface's clear exit, then
  the cell's quarter-spacing lattice nearest-first. Every candidate must stand
  clear of all surfaces and pass the back-face validity test; a short 32-ray
  local rejection precedes a full radiance trace. The former openness score is
  deleted: it moved a probe one metre off a 10 cm slab, and neighbouring floor
  cells could choose different storeys. Increasing clearance did not fix the
  sparse-curve shadow. The thin-slab witness takes the nearest safe exit (0.175 m
  at Medium), rather than the openness winner.
  Later sweeps keep the first origin. Offset XYZ rides the record and every
  gather/meter read uses the relocated origin; trilinear share stays the cell's.
  A cell no surface reaches within its half diagonal is wholly inside or outside
  and tries no candidates. A cell no candidate frees remains invalid at its
  centre; the shader rejects its invalid flag. Narrow enclosures without any
  valid lattice centre meter by camera trace. The placement and thin-slab CPU
  witnesses cover face, pillar, buried and nearest-clear cases.
- Angular surface support and the radiance cubes are sampled spatial support, not
  exact receiver visibility. Inspect corners and openings with
  `world.debug-view indirect`, separately from `ao` and `shadow`, and compare
  final shading and `world.exposure`. Check an outdoor view and move across
  the volume boundary as well as examining stills inside it.

`SdfMeshIrradianceLawTests` holds the live two-room/slab trace scene. A law makes
its volumes through `Volume()`, which disposes them when the law ends: a volume
subscribes to the shared memory account's trim event until `Dispose`, which
otherwise keeps every law's traced probes alive (the class reached 9 GB; disposed,
it peaks near 1.5 GB). The old
indirect canaries and comparison workloads under `experimental/` describe a
retired renderer and are not live verification scenes.

## Debug-world corner and boundary checks

Use a fixed camera for comparisons so chase collision cannot change the view.
Each row gives `body.pose` arguments, then `view.override camera look` arguments
(eye and target). Capture settled `indirect` and `off` views at each location.

| Location | Body pose | Camera look |
|---|---|---|
| Outer hall wall | `7.01 0.02 -5.35 307 0 0` | `4.6517 1.759 -3.5533 7.01 1.3 -5.35` |
| Along the hall | `17.33 0.07 24.30 271 0 0` | `14.3657 1.759 24.3516 17.33 1.3 24.30` |
| Teal room corner | `4.60 0.07 29.76 152 0 0` | `5.929 2.188 27.222 4.60 1.3 29.76` |
| Outdoor control | `-8 0.07 -10 225 0 0` | `-11 3 -13 -3 1 -5` |

For exposure, turn `gallery.lights.all on` and inspect the lift after
`gallery.lift 0` and `body.pose 16 0.1 38 0 0 0`. A fixed interior eye clear of
the avatar is `view.override camera pose 16 2.3 37 180 10`; the hanging
PRIMITIVES sign is visible from `view.override camera pose 18 2.8 16 270 0`.
Read the primary view's meter after the scene and lighting settle, and during
changes: an initial `meter=none` alone cannot establish that a settled room has
no visible probe. Keep the authored exposure bounds and world headroom intact.

A cold Vulkan boot shows a near-black shaft with no meter until the static draws
are ready and the three-sweep GI completes; capture only after that, and
compare Vulkan with Direct3D on the same source state with all geometry settled
([open items](../../../../docs/plans/open-items.md#vulkan-regressions-and-gpu-robustness)
records the remaining shaft difference).

The runtime trace and relight run on the GPU. To
time them, pose the teal corner (`world.lockstep on`, `body.pose 4.60 0.07 29.76
152 0 0`, `view.override camera look 5.929 2.188 27.222 4.60 4.2 29.76`), warm
once, then switch `world.indirect high`, `world.wait 5`, `world.indirect
medium`, `world.wait 5` and read the two `[sdf.mesh-gi] done … seconds=` lines.
Read `gpu-rays`, `gpu-trace-nanoseconds`, `gpu-relight-nanoseconds`, selection
and meter counters beside the main residency's done lines. Trace timestamps
include upload and readback; the ray count includes placement trial rounds.
Compare deltas over a matching workload, not startup totals from different
camera histories. Repeat on a quiet machine; a reading beside other CPU work
is not a backend speed comparison. GPU execution time alone does not establish
the full trace/relight wall cost or the lockstep frame target.

Read `world.exposure` in the hall. Double the outer-wall camera's distance,
and move the hall camera and target 8 m along +X, to check that moving the
toroidal volume creates no rectangular wall patch or bright distance band.
The teal corner must also stay closed high up where its walls meet the roof;
checking only the lower stepped strip misses the diagonal-probe case.
Aim the same eye at `4.60 4.2 29.76` to inspect that junction: a triangular
bright patch there, in both indirect and final shading, is the probe above the
roof admitted through the rays that graze past the roof's edge, which depth visibility must close. Do not infer a clean roof from the lower reference view.

## Gallery isolation checks

Turn `gallery.lights.all on` and leave the blinds down. Additional fixed
`view.override camera look` poses are:

| Exhibit | Body pose | Camera look |
|---|---|---|
| SPHERE, level 0 | `18 0.07 15 0 0 0` | `23 2.4 14 23 1.1 16` |
| REVOLVED, level 0 | `18 0.07 31 0 0 0` | `30.8 2.4 30 30.8 1.1 34` |
| SPIN roof junction, level 3 | `18 13.57 18 0 0 0` | `23 15.5 16.5 23 17.7 21` |
| COAT, level 2 | `18 9.07 22 0 0 0` | `25.6 11.2 23 25.6 10.3 25` |

The SPHERE (`finding-sphere-step`) must shade continuously across its front,
without a split or patch from the plinth or hanging sign. The floor is shadowed in
`shadow`; do not attribute an indirect-view line to sun cascades. Angular surfaces remain sampled support; a receiver all depth tests reject has
no measured light.
The SPIN dotted ceiling seam reproduces in
beauty but not in `indirect`, `normals` or `shadow`
([open items](../../../../docs/plans/open-items.md#lighting-and-gi)
records the experiments).

COAT's authored `specular: 0.35` is dielectric F0, not coat strength: it
reflects a substantial neutral environment over its red diffuse albedo.
`coat` currently adds a direct-light lobe in `sdfMaterialSpecular`; it is not
read by the environment-reflection branch in `sdf-light-stage.hlsli`. Compare
the material with coat disabled separately from changing its base F0 before
attributing a pale colour to the coat layer: coat zero leaves the salmon colour,
while coat 0.5 with base F0 reduced from 0.35 to 0.04 restores a saturated red.

See the [handbook](../../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
for the lighting model and [verification](../../verification/SKILL.md) for GPU
leases and named, device-free World runs.
