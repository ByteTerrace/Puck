# Puck.SdfVm.Tests

This xUnit v3 suite checks SDF view and camera laws, curve paths, native
lighting packing, the engine's declared buffer transitions, and
deterministic follower helpers in `Puck.SdfVm`.
`SdfLightingUploadLawTests` compares every decoded field against saved bytes
from the preceding row layout; `PackLightingLawTests` holds slot identity and
anchored occluder positions. Frame-block laws keep lighting out of the common
block and hold its generated size and consumer declarations.
The byte-backed upload model checks that changing a light color and a full-width
sky seed copies only their changed words, retains all five native tables across
ring slots, and writes nothing on subsequent still frames. It also reconciles
the lighting memory readout used by `world.budget` with every observed table
buffer, the CPU shadows and the pack/upload scratch on both region policies.
The sky upload cases additionally check structural growth without new descriptor
pools, exact high-bit seeds, per-view quality and mute admission, static field
invalidation, and zero uploads or row rewrites after unchanged inputs.

These tests exercise CPU-side contracts and packed data; GPU parity and live
world rendering use the rendering workflow.

## Verification

```powershell
dotnet test tests/Puck.SdfVm.Tests/Puck.SdfVm.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
