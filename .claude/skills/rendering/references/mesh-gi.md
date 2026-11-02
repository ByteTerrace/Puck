# Mesh irradiance

The CPU trace and the views shader share one probe lookup. Read this before
changing indirect light, probe visibility, the volume boundary or exposure.

| Part | Owner |
|---|---|
| Static scene and ray visibility | `SdfMeshIrradianceScene` |
| Trace, cache, bounce lookup and exposure meter | `SdfMeshIrradiance` |
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
  refresh. While opaque static groups are still pending, a non-lockstep frame
  builds the scene from those landed (`ScenePendingDue`: no build running, the
  previous scene's trace finished, at least `PendingSceneInterval` since the
  last build began, and at least one static draw: an empty scene would measure
  open sky where walls have yet to land), so the volume and the meter answer
  during a cold bake instead of after it. Waiting for the whole bake left every
  view `meter=none source=held` and every shaded face black for the ten minutes
  of a cold debug-world bake. A lockstep frame still waits for the whole bake.
  `world.wait bakes` still waits for all geometry; an initial
  `world.exposure` reading with `meter=none` is not a backend-parity result.
- The volume has three levels (`SdfMeshIrradianceProbe.Levels`): the tier's
  window, then two 32×12×32 windows of 8 m and 32 m cells
  (`SdfMeshIrradiance.LevelSpacing`), their slots after the finer levels' in one
  table, each described by a `meshIrradianceLevels` row (first cell, packed
  dims) and a spacing in `meshIrradianceVolume` (zero until the level first
  publishes). Each level publishes on its own once every slot of its window is
  measured. A receiver reads the finest level whose window holds it within its
  boundary probes (`Select`, `sdfMeshProbesAt`), the coarsest past every window.
  Never let a coarser level answer inside a finer window: at 25 m a 32 m probe's
  angular tap is metres wide, its patch test cannot resolve a thin wall the fine
  probe measured, and the wall law reads visibility 1 through it. Where the
  level's bounded gather is zero, the same probes answer unbounded
  (`SdfMeshProbeDepth.Visibility(bounded: false)`): a miss no longer rejects a
  receiver past its reach, but a measured hit still blocks. That read reports
  `measured=0` in `world.probe` and gives traced static lights zero visibility.
  It replaces the earlier rule that a receiver with no answering probe reads
  black: far gallery walls and outdoor shaded faces past the old 64 m window
  drew pure black.
- Each level's window centres on the box of the cameras of every view the
  residency renders when it fits in the window's middle, and on the first view's
  camera alone when it does not (`WindowLow`).
- A cold bake with `world.wait bakes` still pending, `sdf.mesh-gi.scenes=0`,
  and skipped `sdf.world$mask`/`sdf.world$bake` work is not by itself a stall.
  Read the wait's bake description twice: its meshes, pending groups,
  dispatches and samples distinguish progress from a stopped scheduler.
  Plain `lead/catch-up` at `3870e5e0e`, without the two `codex-gi2` fixes,
  also failed to finish a cold Direct3D bake within nearly nine minutes. Its mesh
  draw count kept rising. `host.frames` showed eight simulation catch-up steps
  taking roughly 400 ms a frame; `world.rate pause` on the boot instance
  reduced frame time to about 19 ms and accelerated baking. Pause only a
  diagnostic run, never the owner's play session, and disclose that change
  when comparing times. A nested world's `[sdf.mesh-gi] done` does not certify
  the gallery: check the main residency's counter and meter too.
  The scheduler's separate saved-pose/input-capacity defect has a focused
  boundary test; it is not the established cause of this slow gallery boot.
  See [mesh baking](mesh-bake.md#groups-poses-and-materials).
- Visibility is a per-probe 12×12 octahedral first-hit surface map. Each texel
  traces one closest-hit ray, bounded to `1.5 * sqrt(3) * spacing + NormalBias`:
  full in-window receiver support including relocation. Each word holds an FP16
  ray distance and two signed octahedral normal bytes. A
  negative distance explicitly marks a miss and its reach. Receivers beyond
  that measured support receive nothing. Depth is traced on a probe's first
  sweep of each lighting generation and reused for that generation's later
  bounces; sky still uses the full-reach radiance rays. A generation starts
  every cell at sweep zero (`TraceProbe`'s `sweep > 0` test), so a lighting
  change alone re-places each probe and re-traces its geometry-only depth rays:
  moving the held sun at Medium re-traced 4,796,784 depth rays, the count a
  layout change traces.
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
  Coverage closes from half to three-quarter support; a minority hit cannot
  extend a finite block over the footprint. Tests remain confined to these four
  angular taps: no 26-ray whole-sphere planes, neighbour separation, suspect sets
  or cross-probe vetoes return. Keep `SdfMeshProbeDepth.Visibility` and
  `sdfMeshProbeDepthVisibility` together, including signed normal decoding,
  seam reflection, patch compatibility, gaps and coverage. Projection/addressing
  reuse `SdfOctahedral` / `field/sdf-octahedral.hlsli`.
- Gather weight is the cell's trilinear share times
  `smoothstep(0, spacing / 2, dot(normal, probe - receiver))` times depth visibility. This continuous
  positive-hemisphere weight excludes probes behind a surface without switching
  the whole gather between front and back sets. It also prevents grazing depth
  uncertainty behind a thin wall from admitting a bright exterior probe.
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
- When none of the eight probes answers, the meter traces radiance at the
  camera with the same rays, materials, shadow tests and bounce cache as a
  lattice probe. A small enclosure may contain no lattice centre; searching
  farther or holding the previous room's exposure cannot measure its light.
  Cache the six directional values by camera cell and scene/lighting generation,
  with at most one slot per view. Looking around reweights that measurement;
  moving within its cell reuses it only when the scene sees the camera from
  the sample's actual origin. A blocked segment requires a fresh measurement,
  even within one cell. Refresh once when the volume's bounce sweeps finish.
  `world.counters sdf.mesh-gi` counts `camera-traces`; steady reads add none.
  At a camera cut the meter reads `fresh`: a sample is reused only from the very
  position, so the reading after a teleport depends on where the camera stands,
  not where it stood. `SdfViewExposures` keeps a cut owed until a reading
  arrives and snaps to that first reading.
  This camera sample never writes the volume or relaxes surface visibility.
  If the scene is unavailable or the camera is inside opaque geometry, retain
  the previous exposure, local-adaptation meter and sky visibility. The authored
  EV is only the initial value before a reading. `world.exposure` distinguishes `probes`,
  `camera-trace`, `held` and `initial`; the last two report `meter=none`.
- A settled frame with no opaque static draws installs an empty scene and
  retraces its probes against open space. Empty is known geometry, distinct from
  a scene not built yet; removing the last draw must discard its occlusion and
  bounce light, even when dynamic or glass draws remain.
- A lighting change or scene rebuild advances a generation, never clears the
  cache. The lighting key (`RefreshLighting`) hashes the directional and traced
  light records, the indirect gains and sources, the tier and the scene's
  materials, never the sky: a sky-only change keeps the generation, so the sky
  baked into the radiance cubes stays stale until some other change retraces
  (a muted sky gradient left an outdoor probe's cube at 10,477 nits until a
  0.004-degree sun move retraced it to 9,212). A light that changes every frame
  (a day cycle's sun) advances the generation every frame, and every batch the
  trace lands is thrown away (the `generation != m_generation` test), so the
  volume never publishes while the sun moves. The tracer counts only that generation's sweeps; bounce reads use its
  intermediate samples while viewers keep the previous completed
  sample until the cell's replacement finishes. An invalid replacement rejects
  immediately. Clear the CPU cache and staging volume only when the layout
  changes. On a resize or window move, retain the uploaded GPU table **and its
  old coordinates** until every new slot has a measured sample. Never publish
  unwritten cells as darkness or initialize them to unoccluded sky. Until a
  level first publishes, the parameters are disabled and views light as without
  the volume (harmonic sky ambient and authored fill).
- A record is 235 words (940 bytes): 19 header/cube/light/origin words, 144
  packed first-hit surface words and 72 packed normal-gradient words.
  Medium's three levels hold 36,864 probes in 33.046875 MiB; High's 61,440 in
  55.078125 MiB.
  The table starts at one inert record and both
  it and CPU staging fit the published tier. Reuse `GpuRegion` grow/shrink and
  existing frame-ring retirement; increment the binding revision when its buffer
  changes. Keep the old table and coordinates until every new slot is measured.
  Five full payloads (device, two uploads, native shadow, CPU volume) total
  165.234375 MiB at Medium or 275.390625 MiB at High, before headers, cache,
  batches and temporary retired buffers. Upload buffers can shrink after small flushes;
  live `world.probe` GI diagnostics report actual device/upload/shadow/header
  and CPU-volume bytes. Cache blocks hold 244 words (976 bytes) per probe.
  Publication compares/uploads changed slots only, coalesced into runs.
- Each published probe contains its own measured cubes, exactly as the CPU bounce
  read sees them. Sky and surface radiance use the same full-reach rays, so an
  opening cannot count as sky in one sample set and a bounced hit in another.
  Every probe uses the same precomputed spherical Fibonacci quadrature per tier;
  cell-specific random rotations stamped spatial noise into homogeneous lighting
  (the uniform-ceiling witness spans 1.256–1.476 before this correction). Sharing
  directions correlates sampling error without borrowing any neighbour's light.
  Camera traces use that same quadrature. Depth rays do no sky measurement.
  Later bounces recompute the two radiance cubes together and reuse only the
  geometry-only surface map. Placement uses shared short rejection rays bounded to one spacing, never lighting reach.
- A probe stands in the open (`SdfMeshIrradiance.Place`). A centre inside geometry
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

`SdfMeshIrradianceLawTests` holds the live two-room/slab trace scene. The old
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

The probe trace runs on the CPU and costs about the same on both backends. To
time it, pose the teal corner (`world.lockstep on`, `body.pose 4.60 0.07 29.76
152 0 0`, `view.override camera look 5.929 2.188 27.222 4.60 4.2 29.76`), warm
once, then switch `world.indirect high`, `world.wait 5`, `world.indirect
medium`, `world.wait 5` and read the two `[sdf.mesh-gi] done … seconds=` lines.
On the lead machine, quiet (load under 15 %), three runs each: High 8.35 s and
Medium 1.21 s on Direct3D 12, High 7.84 s and Medium 1.13 s on Vulkan, with
about 90-98 s of World process CPU per High+Medium pair on both, and identical
ray counts. A reading taken beside other CPU work is not a backend comparison.

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
