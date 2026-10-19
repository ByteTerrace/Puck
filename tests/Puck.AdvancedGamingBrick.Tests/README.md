# Puck.AdvancedGamingBrick.Tests

This xUnit v3 suite checks the Advanced GamingBrick core directly. An
output-ring law overflows the APU's host audio ring and checks that the newest
emulated second survives, in whole frames. Hardware accuracy and co-simulation
evidence belong to the Advanced Post battery.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Tests/Puck.AdvancedGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
