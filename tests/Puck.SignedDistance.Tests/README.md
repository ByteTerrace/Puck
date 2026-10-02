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
(`Puck.SignedDistance.Illumination`) to closed forms and to each other: the
furnace's finite-bounce series, form factors, sealed rooms that stay exactly
dark through 0.05 m walls, a sealed hall whose middle never reads the sky, a
continuation that never counts an interval twice, the lattice's layout and the
host schedule. Review laws cover pockets between samples, actual evaluator hit
positions, world exits and geometry invalidation beyond fine reach and around
relocated origins. Counterexamples identify the result each withheld fix admits.
The CPU laws are the GPU cache's reference, not a check of it.

## Verification

```powershell
dotnet test tests/Puck.SignedDistance.Tests/Puck.SignedDistance.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
