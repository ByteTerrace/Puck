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
- Visibility is a per-probe 16×16 octahedral first-hit distance map. Each texel
  traces one closest-hit ray, bounded to `1.5 * sqrt(3) * spacing + NormalBias`:
  the full in-window receiver support including relocation. Two FP16 distances
  share each word. A negative value marks a miss and its measured reach. A
  receiver beyond that reach receives no support. Boundary extrapolation is
  finite; do not silently turn a bounded miss into unlimited visibility.
  The map is traced on the first sweep and reused for subsequent bounces.
- Four bilinear taps reflect across identified octahedral edges. Miss taps
  never inflate a blocker's distance or variance. Compute the weighted mean
  of hit distances and their central variance in FP32; independently rounded
  FP16 mean-squared metres created false variance on flat surfaces at 3–5 m.
  Before the hit mean visibility is one; beyond it the one-sided Chebyshev bound
  is cubed and low values crushed. A minority hit footprint must not extend a
  finite block across the opening: smooth its occlusion from half to three-quarter
  angular coverage (`DepthCoverageStart`, `DepthCoverageScale`, generated).
  Keep `SdfMeshProbeDepth.Visibility` and `sdfMeshProbeDepthVisibility` together,
  including seam taps, hit/miss handling and central variance. Projection and
  addressing reuse `SdfOctahedral` and `field/sdf-octahedral.hlsli`.
  There are no infinite first-hit planes or cross-probe separation vetoes.
- Gather weight is the cell's trilinear share times
  `max(0, dot(normal, directionToProbe))²` times depth visibility. This continuous
  positive-hemisphere weight excludes probes behind a surface without switching
  the whole gather between front and back sets. It also prevents grazing depth
  uncertainty behind a thin wall from admitting a bright exterior probe.
  The CPU bounce read and the pixel read use the same rule.
- At the volume boundary clamp the lookup, then test the actual receiver,
  pushed `NormalBias` (0.05 m) along its normal, against depth. Never bias by
  spacing or test the clamped position: either can carry a receiver through a
  thin wall. An unwritten or invalid probe contributes nothing. No answering
  probe means no measured indirect light, and a depth miss has finite reach.
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
  intermediate samples while viewers keep the previous completed
  sample until the cell's replacement finishes. An invalid replacement rejects
  immediately. Clear the CPU cache and staging volume only when the layout
  changes. On a resize or window move, retain the uploaded GPU table **and its
  old coordinates** until every new slot has a measured sample. Never publish
  unwritten cells as darkness or initialize them to unoccluded sky. Before the
  first scene has supplied any volume, there is no measured indirect light.
- A record is 147 words (588 bytes): 19 words of key, cubes, light visibility
  and origin, then 128 words packing 256 FP16 distances. Medium's 12,288 probes
  use 6.890625 MiB; High's 36,864 use 20.671875 MiB. The table's staged region
  still reserves High capacity: one device buffer, two host upload buffers and
  a native shadow total 82.6875 MiB, before copy headers. `m_volume` allocates
  the active tier (6.890625 or 20.671875 MiB), rather than High unconditionally.
  Its five High payloads total 103.359375 MiB, down from 193.359375 MiB.
  A cached probe block uses 156 words (624 bytes). Previous generations and
  retained cells add to this. Only changed slots, coalesced into contiguous
  runs, are compared/uploaded on publication; no full-table comparison per batch.
  Tier-sized GPU capacity would require changing residency allocation, so the
  remaining High reservation belongs to that separate resource-lifetime change.
- Each published probe contains its own measured cubes, exactly as the CPU
  bounce lookup reads them. Its sky and surface radiance come from the same
  full-reach radiance rays, so a small opening cannot simultaneously count as
  both a missed sky ray and a bounced hit in another sample set. Depth rays do
  no sky measurement. Later sweeps recompute both radiance cubes together and
  reuse only the geometry-only depth map. Placement scores up to 32 candidates
  with 32 rays each, but those rays stop at one spacing, never lighting reach.
- A probe stands in the open (`SdfMeshIrradiance.Place`). A lattice centre
  inside geometry (the PRIMITIVES sign at x = 23, y ≈ 3; a stair flight) or
  within `Clearance` (0.1 m) of a surface (a partition face at an odd metre)
  moves to the nearest point of its cell that stands at least `Clearance` from
  every surface with its rays mostly meeting front faces: first pushed off the
  nearest surface along its normal, then over a lattice a quarter of the
  spacing apart out to the cell's faces. Among nearby candidates, prefer fewer
  first hits within a spacing, charging for distance from the centre. Keep this
  openness score with depth moments to condition the map near surfaces. The
  nearest-clear-only experiment did not eliminate grazing marks; those captures
  also still had neighbour cube filtering, so they do not isolate placement as
  the cause of light leaks. Later sweeps stand where the first did.
  The offset rides the record; the gather (`sdfMeshProbesAt`, `Gather`) and the
  meter (`TryMeter`) read the centre plus the offset. The trilinear share stays
  the cell's. A cell no surface reaches within its half diagonal is wholly inside
  or wholly outside and tries no candidate; a probe no candidate frees stays
  invalid at its centre, and the invalid flag makes the shader's written-key
  test fail, so its share goes to the corners that answer. An enclosure
  narrower than twice the clearance still holds no probe and meters by camera
  trace. `SdfMeshIrradianceLawTests.AProbeOnAWallFaceOrInsideGeometryMovesIntoTheOpenAndABuriedOneStaysInvalid`
  pins the on-face, pillar and buried cases.
- Depth moments and the radiance cubes are sampled spatial support, not
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
`shadow`; do not attribute an indirect-view line to sun cascades. Depth moments remain sampled support; a receiver all depth tests reject has
no measured light.
The SPIN dotted ceiling seam reproduces in
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
