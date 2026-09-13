# Puck.World.Browser.Tests

This xUnit v3 suite targets `net10.0` and checks the browser-hosted world engine. `BrowserComposerTests`, `BrowserEngineTests`, and `BrowserHostlessIslandTests` cover composition and hostless execution; `BrowserParityRecordingTests` covers parity recording; `EngineTimingTests` covers tick timing; and `ValidatorMessagePathRatchetTests` preserves validator diagnostics paths.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Browser.Tests/Puck.World.Browser.Tests.csproj -c Release
```

The project references `Puck.State` and `Puck.World.Schema`. Browser publishing and AppBundle checks belong to `Puck.World.Browser` itself.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
