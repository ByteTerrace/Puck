# Puck.World.Schema.Tests

This xUnit v3 suite targets `net10.0` and checks the `puck.world.def.v1` document model and its egress shapes. The laws cover wire-shaped bindings and body programs, radial and HUD binding targets, body producer parameters, closed bitsets, deterministic mirrored fields, music documents, parked-body values, state cycles and dynamics, world composition and exports, placements and lattices, rules and work budgets, schema completeness, names, Silo definitions, and state catalogs and values.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Schema.Tests/Puck.World.Schema.Tests.csproj -c Release
```

The project references `Puck.World.Schema` and `JsonSchema.Net`. Round-trip and live document behavior that needs the composition root is exercised through `Puck.World`.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
