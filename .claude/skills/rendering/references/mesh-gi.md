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
- The depth fade stays thinner than the thinnest floor. A wall sample at a
  floor junction is level with the floor, so a broad fade admits the probe
  underneath and draws a bright line along the junction.
- Six axis depths and neighbour visibility are coarse spatial support, not
  exact receiver visibility. Inspect corners and openings with
  `world.debug-view indirect`, separately from `ao` and `shadow`, and compare
  final shading and `world.exposure`. Check an outdoor view and move across
  the volume boundary as well as examining stills inside it.

`SdfMeshIrradianceLawTests` holds the live two-room/slab trace scene. The old
indirect canaries and comparison workloads under `experimental/` describe a
retired renderer and are not live verification scenes.

See the [handbook](../../../../docs/rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
for the lighting model and [verification](../../verification/SKILL.md) for GPU
leases and named, device-free World runs.
