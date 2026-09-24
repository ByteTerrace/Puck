# Puck.World.Schema.Tests

This xUnit v3 suite targets `net10.0` and checks the `puck.world.definition.v1` document model and its egress shapes. The laws cover wire-shaped bindings and body programs, radial and HUD binding targets, body producer parameters, closed bitsets, deterministic mirrored fields, music documents, parked-body values, state cycles and dynamics, world composition and exports, placements and lattices, rules and work budgets, schema completeness, names, Silo definitions, and state catalogs and values.

## Verification

`WorldAdmissionEnvironmentLawTests` checks single full admission after literal or
drawn state bindings resolve, through bytes, files, and asynchronous loading. It also
checks late vocabulary refusals and ownership of the final document's programs.
`WorldBootValidationLawTests` checks invalid draw inputs before they can be replaced,
backend-row settlement and reload, and unchanged document identity without allocation.

`WorldCostReportLawTests` also checks that a compilation shares one report and
its evidence identity, while replacement compilations use their own rate policy.
`WorldRuleHazardLawTests` checks shared hazard and budget reads without warm
allocations, and fresh analysis after a rule edit that keeps the same state catalog.
`WorldCallArgumentsLawTests` checks that warm enum lookups and queries for unknown authored members allocate
nothing. The metadata cache holds declared schema members, never a growing list of misspelled source names.

`ViewSlotDefaultsLawTests` keeps full-window defaults consistent between C# construction and JSON loading.
`WorldScreenInputLawTests` refuses a screen route that declares `Passthrough` by name and maps a `Simulation` screen's
pointer ray to its source pixel from the row alone, with the glass bezel mapping to no pixel.
`WorldFactOperandLawTests` also checks that an identity fact reads its lane cell's live value at the evaluation's time.

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Schema.Tests/Puck.World.Schema.Tests.csproj -c Release
```

The project references `Puck.World.Schema` and `JsonSchema.Net`. Round-trip and live document behavior that needs the composition root is exercised through `Puck.World`.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)

Missing spatial inputs are refused before geometry compilation: screen frame vectors, speaker positions, and placement positions. The edit-state-backed-row executable canary also checks that malformed references and null positions leave the command host running.
