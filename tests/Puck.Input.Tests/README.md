# Puck.Input.Tests

This suite checks provider-neutral input capture in `Puck.Input`, including
gamepad parsing and coalescing, device and lane lifecycle, keyboard and mouse
sources, output effects, motion and source vocabulary, the arbiter's
single-drain behavior, and keyboard focus between the game and a passthrough
source, reserved chord included (`SourceFocusLawTests`). The fixtures use injected transport fakes; the suite
does not claim live hardware coverage. The fake transport's timed reads and a
device's own deadlines (receiver silence, rumble expiry, the disposal join) run
on one manual `TimeProvider`, so a deadline expires only when a test advances
it, and each observation waits for the device loop to park on its next read.

## Running

```powershell
dotnet test tests/Puck.Input.Tests/Puck.Input.Tests.csproj -c Release
```

## Coverage and integration checks

The suite also checks parser report bounds and output-buffer floors, bounded
output, rumble stop and expiry, scheduled effects during input silence, receiver
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
