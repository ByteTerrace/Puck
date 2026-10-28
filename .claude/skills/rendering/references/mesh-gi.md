# Mesh irradiance

The CPU trace and the views shader share one probe lookup. Read this before
changing indirect light, probe visibility, the volume boundary or exposure.

| Part | Owner |
|---|---|
| Static scene and ray visibility | `SdfMeshIrradianceScene` |
| Trace, cache, filter, bounce lookup and exposure meter | `SdfMeshIrradiance` |
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
  refresh. `world.wait bakes` still waits for all geometry; an initial
  `world.exposure` reading with `meter=none` is not a backend-parity result.
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
- A direction's plane is its ray's first hit. Averaging it with rays that
  escape beside a blocker, or choosing the farther hit, erases that blocker.
- At the volume boundary, clamp the lookup to the boundary probes, then test
  the **receiver position** (pushed `NormalBias` metres off its surface, never
  the clamped lookup) against their planes. Clamping the visibility test too
  extrapolates through walls. No answering probe means no measured indirect
  light; an unwritten cell is not evidence of open sky.
- The push off the surface is metres, under the thinnest wall. A push that
  scales with the probe spacing (0.35 of a 2 m cell) carries a surface facing a
  near wall through it, and the room beyond lights the surface: white slivers
  along a silhouette turned toward a wall, and the exterior's light as rims
  on a body in every room.
- Visibility is never a half-space. An axis depth that bounds a probe along a
  whole axis rejects everything beyond a finite blocker on that one ray (a
  plinth, a stair's nosing, an exhibit), and selecting one of the 26 planes by
  nearest direction snaps between them at the half-component thresholds: a
  plinth then casts hard-edged, lattice-aligned rectangles of darkness and the
  floor around an exhibit's base goes black. The read blends the four planes
  about the receiver's direction (`PlaneVisibility`,
  `sdfMeshProbePlaneVisibility`); a wall every ray meets is still exact, and a
  finite blocker darkens only its own directions.
- A blended plane test alone leaks where a ray clears a blocker's edge: a
  probe just above a thin roof whose horizontal rays graze past it, or beside a
  doorway, admits the room's upper walls in jagged, plane-cut patches, and the
  under-floor probe draws a sawtooth along the wall–floor junction. The
  separation rule (`sdfMeshProbeSeparate`, `SdfMeshIrradiance.Separate`) closes
  it with the other probes' planes: where probe A cannot see probe B, A's plane
  toward B is the blocker between them, and a receiver on A's side of it takes
  nothing from B. It applies only by B's suspect share (`PlaneVisibility`'s
  `suspect`): the blend of B's rays toward the receiver that reached open
  space or went on more than a quarter of the spacing past it. A ray that met
  the receiver's own surface saw it, so a stair flight between two probes
  separates neither from the walls beside it (applied to every admission, the
  rule blacked out the stairwell's level 3 flight), while a tall exhibit
  between two probes still separates them over the floor beyond it (the
  SPHERE step keeps a soft line along the exhibit's face plane).
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
  cache. The tracer counts only that generation's sweeps; bounce reads use its
  intermediate samples while metering and filtering keep the previous completed
  sample until the cell's replacement finishes. An invalid replacement rejects
  immediately. Clear the CPU cache and staging volume only when the layout
  changes. On a resize or window move, retain the uploaded GPU table **and its
  old coordinates** until every new slot has a measured sample. Never publish
  unwritten cells as darkness or initialize them to unoccluded sky. Before the
  first scene has supplied any volume, there is no measured indirect light.
- The depth fade stays thinner than the thinnest floor. A wall sample at a
  floor junction is level with the floor, so a broad fade admits the probe
  underneath and draws a bright line along the junction.
- The 26 neighbour rays retain their first hit planes, and the receiver's
  direction blends the four about it (the 3×3 directions of the cube face it
  lies toward, bilinear in the face's two minor components, which the CPU and
  the shader arrange the same way). Plane distance, rather than ray distance,
  keeps a flat blocker in the same place as the receiver's direction changes.
  The record is 70 words (280 bytes), including 52 words of half-precision
  planes. The neighbour rays trace to the lighting reach and their first hit
  also determines the neighbour mask; no second scene is built.
- Directional planes and neighbour visibility are coarse spatial support, not
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
([open items](../../../../docs/plans/open-items.md#debug-world-walkthrough-findings)
records the remaining shaft difference).

Read `world.exposure` in the hall. Double the outer-wall camera's distance,
and move the hall camera and target 8 m along +X, to check that moving the
toroidal volume creates no rectangular wall patch or bright distance band.
The teal corner must also stay closed high up where its walls meet the roof;
checking only the lower stepped strip misses the diagonal-probe case.
Aim the same eye at `4.60 4.2 29.76` to inspect that junction: a triangular
bright patch there, in both indirect and final shading, is the probe above the
roof admitted through the rays that graze past the roof's edge, which the
separation rule closes. Do not infer a clean roof from the lower reference view.

## Gallery isolation checks

Turn `gallery.lights.all on` and leave the blinds down. Additional fixed
`view.override camera look` poses are:

| Exhibit | Body pose | Camera look |
|---|---|---|
| SPHERE, level 0 | `18 0.07 15 0 0 0` | `23 2.4 14 23 1.1 16` |
| REVOLVED, level 0 | `18 0.07 31 0 0 0` | `30.8 2.4 30 30.8 1.1 34` |
| SPIN roof junction, level 3 | `18 13.57 18 0 0 0` | `23 15.5 16.5 23 17.7 21` |
| COAT, level 2 | `18 9.07 22 0 0 0` | `25.6 11.2 23 25.6 10.3 25` |

The SPHERE step shows in `indirect` as a soft line along the exhibit's face
plane; the floor is shadowed in `shadow`. Do not attribute it to sun cascades.
The 26 planes are coarse support, and can reject visible receivers beside a
finite occluder; normalization cannot recover light when all accepted weights
vanish. The SPIN dotted ceiling seam reproduces in
beauty but not in `indirect`, `normals` or `shadow`
([open items](../../../../docs/plans/open-items.md#debug-world-walkthrough-findings)
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
