# Puck.Shaders.Tests

This xUnit v3 suite verifies shader manifests, pipeline graphs and liveness,
binding and push-constant layouts, compiler adapters, probe kinds, and
compiled-bytecode freshness. It tests the document and build contracts without
claiming correctness for every GPU driver.

## Verification

```powershell
dotnet test tests/Puck.Shaders.Tests/Puck.Shaders.Tests.csproj -c Release
```

The native compiler checks require `dxc` when the corresponding test is
available; the test discovery and managed contract checks remain useful
without a GPU.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
