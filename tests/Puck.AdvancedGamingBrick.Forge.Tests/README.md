# Puck.AdvancedGamingBrick.Forge.Tests

This xUnit v3 suite exercises the AGB cartridge compiler and its native
artifacts. It covers cartridge data, graphics, maps, sound, state, capacity,
Thumb emission, and content-provider behavior through the production Forge
project; it does not certify arbitrary authored games or retail BIOS behavior.

The suite runs both targets, so the Color compiler's execution tests live here
too. `CartridgeProbe` compiles a document with its target's compiler and boots
it on that machine. `CartridgeRefusal` names each case validation must refuse,
with the error's document path and message text; a class keeps its refusals in
one table behind one theory.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests/Puck.AdvancedGamingBrick.Forge.Tests.csproj -c Release
```

The project references `Puck.AdvancedGamingBrick.Forge` and
`Puck.HumbleGamingBrick.Forge`. Use the Advanced Post battery for machine
accuracy and co-simulation evidence.

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
