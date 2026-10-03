# Puck.SdfVm.Tests

This xUnit v3 suite checks SDF view and camera laws, curve paths, pack
environment behavior, the engine's declared buffer transitions, and
deterministic follower helpers in `Puck.SdfVm`.
These tests exercise CPU-side contracts and packed data; GPU parity and live
world rendering use the rendering workflow.

`SdfWorkDetailLawTests` holds sky and shadow labels to the packed detail row
address and checks that sky evaluations, hashes and texture loads count beside
their kernel operations. Device execution remains with the GPU counter gates.

## Verification

```powershell
dotnet test tests/Puck.SdfVm.Tests/Puck.SdfVm.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
