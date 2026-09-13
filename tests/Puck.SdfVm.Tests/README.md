# Puck.SdfVm.Tests

This xUnit v3 suite checks SDF view and camera laws, curve paths, pack
environment behavior, and deterministic follower helpers in `Puck.SdfVm`.
These tests exercise CPU-side contracts and packed data; GPU parity and live
world rendering use the rendering workflow.

## Verification

```powershell
dotnet test tests/Puck.SdfVm.Tests/Puck.SdfVm.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
