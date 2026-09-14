# Puck.GamingBricks.Transpiler.Tests

This xUnit v3 suite verifies the `puck.cartridge.v1` vocabulary and its
round-trip boundary. It compiles production cartridge sources, checks
diagnostics and lowered documents, and compares committed `.puck` sources with
their committed JSON artifacts byte for byte.

## Verification

```powershell
dotnet test tests/Puck.GamingBricks.Transpiler.Tests/Puck.GamingBricks.Transpiler.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
