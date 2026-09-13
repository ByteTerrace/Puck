# Puck.Platform.Windows.Tests

This xUnit v3 suite checks Windows platform seams, including camera discovery
and frame packing, capture publication, probe kernels and readings, Media
Foundation behavior, and HID input. It uses the Windows projection package and
does not replace a live device or application integration run.

## Verification

```powershell
dotnet test tests/Puck.Platform.Windows.Tests/Puck.Platform.Windows.Tests.csproj -c Release
```

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
