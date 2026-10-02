# Puck.SdfVm.Tests

This xUnit v3 suite checks SDF view and camera laws, curve paths, pack
environment behavior, the engine's declared buffer transitions, and
deterministic follower helpers in `Puck.SdfVm`.
These tests exercise CPU-side contracts and packed data; GPU parity and live
world rendering use the rendering workflow.

`SdfCatalogueMemoryLawTests` load a set with only the selected backend's files
present, then hold two view pipeline sets to one catalogue and the same byte
arrays. Resident payload is counted once per backend and survives the release
of either view, matching the composition's catalogue lifetime.

## Verification

```powershell
dotnet test tests/Puck.SdfVm.Tests/Puck.SdfVm.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
