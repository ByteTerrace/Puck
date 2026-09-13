# Puck.ShaderVm.Tests

This xUnit v3 suite checks the backend-neutral four-lane shader program model,
its packed layout laws, SDF ISA comparisons, previews, and throughput probes.
The project is an experimental successor and is not used by the live SDF world
render path.

## Verification

```powershell
dotnet test tests/Puck.ShaderVm.Tests/Puck.ShaderVm.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
