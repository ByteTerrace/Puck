# Puck.State.Generators.Tests

This suite exercises the authored-randomness engine in
`Puck.State.Generators`: the extended-generator draw families `GeneratorEngine`
runs, a draw site's cursor and drawn masks as `ArenaDraws` reads and writes
them off the arena, and the tile kind, ribbon, and thin-chain rows
`PenrosePatch` derives over a seeded patch.

## Running

```powershell
dotnet test tests/Puck.State.Generators.Tests/Puck.State.Generators.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
