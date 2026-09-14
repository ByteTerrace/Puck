# Puck.World.Transpiler.Tests

This xUnit v3 suite targets `net10.0` and checks the `.puck` world vocabulary pipeline. Parser, lowering, emitter, formatter, decompiler, linter, diagnostics, LSP, lambda, machine, shape, sugar, source-position, and round-trip tests cover canonical source fidelity and compiled world output; shipped-world tests compare committed sources with their generated JSON. A build-only CLI dependency generates those documents before the tests run; see [generated assets](../../build/README.md).

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Transpiler.Tests/Puck.World.Transpiler.Tests.csproj -c Release
```

The project references `Puck.Transpiler`, `Puck.World.Transpiler`, and `Puck.World.Schema`; grammar and CLI behavior remain owned by the transpiler projects.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
