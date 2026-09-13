# Puck.AdvancedGamingBrick.Forge.Tests

This xUnit v3 suite exercises the AGB cartridge compiler and its native
artifacts. It covers cartridge data, graphics, maps, sound, state, capacity,
Thumb emission, and content-provider behavior through the production Forge
project; it does not certify arbitrary authored games or retail BIOS behavior.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests/Puck.AdvancedGamingBrick.Forge.Tests.csproj -c Release
```

The project references `Puck.AdvancedGamingBrick.Forge` and
`Puck.HumbleGamingBrick.Forge`. Use the Advanced Post battery for machine
accuracy and co-simulation evidence.

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
