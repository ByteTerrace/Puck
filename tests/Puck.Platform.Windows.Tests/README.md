# Puck.Platform.Windows.Tests

This xUnit v3 suite checks Windows platform seams, including camera discovery
and frame packing, capture publication, probe kernels and readings, Media
Foundation behavior, and HID input. It uses the Windows projection package and
does not replace a live device or application integration run. `Win32PointerTests`
uses hidden native windows to verify cursor coordinates, capture transfer,
interrupted drags, and fractional wheel events without moving the system cursor.
`Win32PassthroughWindowTests` sends a passthrough source's pointer events to a
hidden Puck window, which reads them back at their client points, reads the
messages a hidden recording window receives (a key's `WM_KEYDOWN`, `WM_CHAR`
and `WM_KEYUP`, Alt's system messages, each modifier side, and a drag held by the
child it was pressed on), and holds the virtual-key table to a round trip for
every key.

## Verification

```powershell
dotnet test tests/Puck.Platform.Windows.Tests/Puck.Platform.Windows.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
