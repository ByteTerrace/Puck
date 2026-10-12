# Puck.World.Browser.Tests

This xUnit v3 suite targets `net10.0` and checks the browser-hosted world engine. `BrowserEngineTests` covers the engine core over inline documents; `ValidatorMessagePathRatchetTests` preserves validator diagnostics paths; and `BrowserWorkspaceTests` covers the `.puck` workspace's path refusals and source provenance.

Quarantined: the laws that composed the shipped island or `standard.puck` with the tictactoe module (`BrowserHostlessIslandTests`, `BrowserHostOwnedRowLawTests`, `BrowserGateReadsTruthLawTests`, `BrowserParityRecordingTests`, and the island and tictactoe laws of `BrowserEngineTests` and `ValidatorMessagePathRatchetTests`), the `BrowserWorkspaceTests` laws over the sources in `Fixtures/sources`, and the Node suite `wasm/sources.test.mjs` live under [`experimental/tests/Puck.World.Browser.Tests`](../../experimental/tests/Puck.World.Browser.Tests) with the fixtures they read.

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
