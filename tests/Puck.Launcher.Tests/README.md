# Puck.Launcher.Tests

This xUnit suite checks [Puck.Launcher](../../src/Puck.Launcher/README.md) host
controls and release handling. It covers fixed-step pumping, backend switching,
console sessions, release manifests and signatures, content staging, update
application, rollout selection, and the
[stub's](../../src/Puck.Launcher.Stub/README.md) rollback policy.

`OverlayGlyphPackTests` also checks the fixed-grid text consumer: equal-sized
cells pack unchanged, while variable-size, fractional, and out-of-image cells
are refused, including appended glyphs.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test tests/Puck.Launcher.Tests/Puck.Launcher.Tests.csproj -c Release
```

A successful run reports passing tests and exits with code zero. Release
fixtures use temporary staging state and test signing material. This evidence
covers the exercised library and installation policies, not a complete
platform windowing or deployed-release verification.

## Documentation

📚 [Puck.Launcher](../../src/Puck.Launcher/README.md) · 🛠️ [Development](../../docs/development/README.md)
