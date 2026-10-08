# Puck.World.Machines.Tests

These laws check the engine-neutral screen-machine host driven by the real
Gaming Brick cores: the machine catalog built from extensions, transactional
prepare and commit, memory peek and poke and named memory, binding checkpoints
and their restore, hardware access, the light gun and the instrument clock. The
suite references `Puck.World.Machines`, `Puck.World.Console` and both Gaming
Brick forges, on the authoritative server, and no client, renderer or
transpiler. Machine laws that also compile an authored `.puck` world, such as
`MachineCartridgeLawTests`, are in
[`Puck.World.Games.Tests`](../Puck.World.Games.Tests/README.md).

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md), with the
server vocabulary hooks and the Gaming Brick machine catalog.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Machines.Tests/Puck.World.Machines.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Machines and cartridges](../../docs/plans/machines-and-cartridges.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
