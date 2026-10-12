# Puck.World.Games.Tests

These laws check authored worlds: shipped games and fixture worlds compiled from
`.puck` source through the same `PuckDocumentComposer` path as the executable
host, then run on the authoritative server. They cover each game's state program
and rules, module imports and aliases, document round trips through the
decompiler, compiled worlds and the boot's counted work, navigation over the
shipped island, and the recorded state baselines. The suite references
`Puck.World.Transpiler`, the server, the console, the machine host and both
Gaming Brick cores, because the shipped worlds carry Gaming Brick screens; it
references no client, renderer or graphics backend.

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md), with the
server vocabulary hooks and the Gaming Brick machine catalog. That every shipped
world the World's own build compiled boots from its compiled world is `CompiledCatalogLawTests` in
[`Puck.World.Tests`](../Puck.World.Tests/README.md), beside the build that writes
the catalog.

Quarantined: the laws that boot the island, the game modules, the root `worlds/`
packages, the moth courtyard, `pipeline.puck`, the fixture worlds of
`experimental/tests/Puck.World.Fixtures` and the canaries, among them `PaddleballContactLawTests`
and `ShippedWorldStateBaselineTests` with its `ShippedWorldStateBaselines` records
(`puck baselines state`), live under
[`experimental/tests/Puck.World.Games.Tests`](../../experimental/tests/Puck.World.Games.Tests)
beside the documents they read.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Games.Tests/Puck.World.Games.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
