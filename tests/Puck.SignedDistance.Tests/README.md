# Puck.SignedDistance.Tests

This xUnit v3 suite verifies the signed-distance program format, shape and
material laws, fixed-point field evaluation, domain and culling bounds, and
world-query plumbing. It keeps the CPU query contract independent from GPU
rendering and shader compilation. March laws also pin terminal-sample accounting
and the fixed-point coordinate-progress bound used for banded visibility work.

`GlyphSamplingLawTests` checks the host's decoded-atlas derivative correction,
stretched-cell mappings, metadata-only fallback, and packed-lane admission. It
does not substitute for a rendered glyph pixel-conformance test.

`SdfBakerLawTests` holds prototype bakes to their field, their tile-aware texture
chains and exact one-material tiles. `BakeSamplingFixtureLawTests` holds
`Fixtures/bake-sampling.json`, the BC7, BC5 and BC6H textures of one real bake with
a probe texel per level, to a fresh bake and to the CPU decoder; it is the GPU-free
half of a sampling check whose device half, `BakeSamplingDeviceLawTests` in
`tests/Puck.World.Tests`, samples the same probes on Vulkan, Direct3D 12 and WARP.

The `Irradiance*LawTests` hold the radiance cache's CPU reference and CPU model
(`Puck.SignedDistance.Illumination`) to the cache's contract. Two properties are
required exactly: no light through sealed geometry (sealed rooms dark through
0.05 m walls, a sealed pocket inside one cell, a slab inside a launch interval,
a slab thinner than the accept threshold beneath a launch's first sample,
receiver proofs and their reuse) and conserved energy (the furnace's finite-bounce series
on one level and two, with unresolved grazing rays present). Every other error
is a number held against the reference: interpolation within a cell,
continuation merging, light-view sampling, grazing rays and the proof
allowance. The lattice, the schedule and the evaluator's own hit points have
laws of their own. `IrradianceAdversarialLawTests` also exercises conservative
gauges, failed-proof reuse, the swept depth range and unresolved light-view
texels. A gauge's small field value needs a nearby zero certificate before a
GI hit is accepted; unresolved light-view texels require a shadow ray. The
grazing bound checks a fully resolved reference sample set and the analytic
open-floor answer. Each law's red leg is a model mutation shown failing; the
CPU laws are the GPU cache's reference, not a check of it.

## Verification

```powershell
dotnet test tests/Puck.SignedDistance.Tests/Puck.SignedDistance.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
