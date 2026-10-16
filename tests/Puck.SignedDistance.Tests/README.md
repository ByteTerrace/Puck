# Puck.SignedDistance.Tests

This xUnit v3 suite verifies the signed-distance program format, shape and
material laws, fixed-point field evaluation, domain and culling bounds, and
world-query plumbing. It keeps the CPU query contract independent from GPU
rendering and shader compilation. March laws also pin terminal-sample accounting
and the fixed-point coordinate-progress bound used for banded visibility work.

It does not substitute for a rendered glyph pixel-conformance test.

`SdfBakerLawTests` holds prototype bakes to their field, their tile-aware texture
chains and exact one-material tiles. `Fixtures/bake-sampling.json` holds the
BC7, BC5 and BC6H textures of one real bake with a probe texel per level;
`BakeSamplingDeviceLawTests` in `tests/Puck.World.Tests` samples its probes on
Vulkan, Direct3D 12 and WARP.

## Verification

```powershell
dotnet test tests/Puck.SignedDistance.Tests/Puck.SignedDistance.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
