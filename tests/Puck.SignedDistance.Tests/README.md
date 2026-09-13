# Puck.SignedDistance.Tests

This xUnit v3 suite verifies the signed-distance program format, shape and
material laws, fixed-point field evaluation, domain and culling bounds, and
world-query plumbing. It keeps the CPU query contract independent from GPU
rendering and shader compilation.

## Verification

```powershell
dotnet test tests/Puck.SignedDistance.Tests/Puck.SignedDistance.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
