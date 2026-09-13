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

## Coverage and integration checks

The suite also checks parser report bounds and output-buffer floors, bounded
output, rumble stop semantics, scheduled effects during input silence, receiver
parking and state reset, disposal ordering, touch release, deterministic player
ordering, and empty LampArray behavior.

After changing the transport seam, build both the neutral library and the Windows
implementation from the repository root:

```powershell
dotnet build src/Puck.Input/Puck.Input.csproj -c Release
dotnet build src/Puck.Platform.Windows/Puck.Platform.Windows.csproj -c Release
```

Use [Device input](../../docs/reference/input.md#quick-start) for live hardware
inspection; transport-fake tests do not establish hardware compatibility.

## Documentation

📚 [Device input](../../docs/reference/input.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
