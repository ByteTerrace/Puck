# Puck.World.Browser.Tests

This xUnit v3 suite targets `net10.0` and checks the browser-hosted world engine. `BrowserComposerTests`, `BrowserEngineTests`, and `BrowserHostlessIslandTests` cover composition and hostless execution; `BrowserParityRecordingTests` covers parity recording; `ValidatorMessagePathRatchetTests` preserves validator diagnostics paths; and `BrowserWorkspaceTests` covers the `.puck` authoring surface (a mounted workspace, compile, compose, and the language server's idle-driven diagnostics) over the sources in `Fixtures/sources`.

`wasm/sources.test.mjs` drives the same surface through the published AppBundle under Node (`node --test tests/Puck.World.Browser.Tests/wasm/sources.test.mjs` after `dotnet publish src/Puck.World.Browser -c Release`), and skips itself by name when no AppBundle exists. Fixture sources are tracked, so they must compile cleanly and stay formatted; a broken source a test needs is written by the test itself.

## Verification

`BrowserCostReportLawTests` checks shared report parity, replacement on rebind,
and exact wire counts with no invented zero for missing evidence.

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Browser.Tests/Puck.World.Browser.Tests.csproj -c Release
```

The project references `Puck.State`, `Puck.State.Rules`, `Puck.World.Schema` and `Puck.World.Transpiler`. Browser publishing and AppBundle checks belong to `Puck.World.Browser` itself.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
