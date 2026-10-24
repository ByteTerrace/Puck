# Mesh irradiance

The CPU trace and the views shader share one probe lookup. Read this before
changing indirect light, probe visibility, the volume boundary or exposure.

| Part | Owner |
|---|---|
| Static scene and ray visibility | `SdfMeshIrradianceScene` |
| Trace, cache, filter, bounce lookup and exposure meter | `SdfMeshIrradiance` |
| Probe record and generated shader constants | `SdfMeshIrradianceProbe`, `SdfIsaHlsl.DeclareRender` |
| Pixel gather, indirect light, reflections and traced-light visibility | `shade/sdf-mesh-irradiance.hlsli` |

- Keep `Incoming` and `sdfMeshProbesAt`/`sdfMeshIrradianceAt` together. A sky
  fallback in the CPU read feeds later bounce sweeps even if the final pixel
  read rejects it.
- A depth axis stores its first hit. Averaging it with rays that escape beside
  a blocker, or choosing the farther distance, erases that blocker.
- At the volume boundary, clamp the lookup to the boundary probes, then test
  the **actual receiver position** against their depth limits. Clamping the
  visibility test too extrapolates through walls. No answering probe means no
  measured indirect light; an unwritten cell is not evidence of open sky.
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
- The 26 neighbour rays also retain their first hit planes. The receiver's
  nearest lattice direction selects a plane, tested in addition to the six
  axis depths. A probe diagonally outside a roof/wall corner can miss both
  surfaces on every axis; the diagonal hit closes that hole. Plane distance,
  rather than ray distance, keeps a flat blocker in the same place as the
  receiver's direction changes. CPU and shader use the same selection.
  Components halfway between directions select the diagonal, away from zero:
  `NearestPlane` on the CPU and an explicit half threshold in HLSL. HLSL's
  documented `round()` is ties-to-even, so it does not express that rule.
  The record is 73 words (292 bytes), including 52 words of half-precision
  planes. The existing neighbour rays now trace to the lighting reach and
  their first hit also determines the neighbour mask; no second scene is built.
- Axis depths, directional planes and neighbour visibility are coarse spatial support, not
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

Read `world.exposure` in the hall. Double the outer-wall camera's distance,
and move the hall camera and target 8 m along +X, to check that moving the
toroidal volume creates no rectangular wall patch or bright distance band.
The teal corner must also stay closed high up where its walls meet the roof;
checking only the lower stepped strip misses the diagonal-probe case.
Aim the same eye at `4.60 4.2 29.76` to inspect that junction. The `codex-expo2`
before/after comparison found a remaining triangular bright patch there in both
indirect and final shading; its cause is unconfirmed and tracked in
[open items](../../../../docs/plans/open-items.md). Do not infer a clean roof
from the lower reference view.

See the [handbook](../../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
for the lighting model and [verification](../../verification/SKILL.md) for GPU
leases and named, device-free World runs.
