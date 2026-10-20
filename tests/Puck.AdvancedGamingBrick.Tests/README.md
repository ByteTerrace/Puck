# Puck.AdvancedGamingBrick.Tests

This xUnit v3 suite checks the Advanced GamingBrick core directly. A round-trip
law seeds every mutable field of each component, including those inside its
value-type units and the parts it owns, from its own path, saves it, loads the
bytes into a differently seeded twin and requires the twin to save the same
bytes with the reader consumed exactly, so a field saved but not loaded fails;
no layout or digest is recorded. An output-ring law overflows the APU's host audio ring and checks that the newest
emulated second survives, in whole frames. Transfer laws call the bundled
firmware's CpuSet and CpuFastSet from ARM and Thumb cartridges and compare the
destination with the GBATEK contract: copy and fill, 16- and 32-bit units,
counts, alignment and CpuFastSet's rounding to eight words. Fault laws drive a
hosted core into Undefined mode, Abort mode and an unmapped fetch and require
the named `AgbExecutionFaultException`. Hardware accuracy and co-simulation
evidence belong to the Advanced Post battery.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Tests/Puck.AdvancedGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
