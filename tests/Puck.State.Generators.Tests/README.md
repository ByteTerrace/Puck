# Puck.State.Generators.Tests

This suite exercises the authored-randomness engine in
`Puck.State.Generators`: the extended-generator draw families `GeneratorEngine`
runs, a draw site's cursor and drawn masks as `ArenaDraws` reads and writes
them off the arena, a site's stream drawing exactly what single draws one cursor
apart would, a host's seed table agreeing with the seed ladder, and the tile
kind, ribbon, and thin-chain rows
`PenrosePatch` derives over a seeded patch. A per-tick extended site draws
without allocating once its generator is built, including right after a
collection.

## Running

```powershell
dotnet test tests/Puck.State.Generators.Tests/Puck.State.Generators.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
