# Puck.AdvancedGamingBrick.Tests

This xUnit v3 suite checks the Advanced GamingBrick core directly. A round-trip
law seeds every mutable field of each component, including those inside its
value-type units and the parts it owns, from its own path, saves it, loads the
bytes into a differently seeded twin and requires the twin to save the same
bytes with the reader consumed exactly, so a field saved but not loaded fails;
no layout or digest is recorded. An output-ring law overflows the APU's host audio ring and checks that the newest
emulated second survives, in whole frames. Hardware accuracy and co-simulation
evidence belong to the Advanced Post battery.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Tests/Puck.AdvancedGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
