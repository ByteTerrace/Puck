# Puck.AdvancedGamingBrick.Tests

This xUnit v3 suite checks the Advanced GamingBrick core directly. A layout law
pins every component's snapshot bytes, and each APU channel's on its own: each
mutable field, including those inside a component's value-type units and the
parts it owns, is seeded from its own path. The saved bytes must match the
recorded length and SHA-256, and loading them into a differently seeded twin
must reproduce them exactly. An output-ring law overflows the APU's host
audio ring and checks that the newest emulated second survives, in whole
frames. Hardware accuracy and co-simulation evidence belong to the Advanced
Post battery.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Tests/Puck.AdvancedGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
