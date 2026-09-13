# Puck.Input.Tests

This suite checks provider-neutral input capture in `Puck.Input`, including
gamepad parsing and coalescing, device and lane lifecycle, keyboard and mouse
sources, output effects, motion and source vocabulary, and the arbiter's
single-drain behavior. The fixtures use injected transport fakes; the suite
does not claim live hardware coverage.

## Running

```powershell
dotnet test tests/Puck.Input.Tests/Puck.Input.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Input](../../src/Puck.Input/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
