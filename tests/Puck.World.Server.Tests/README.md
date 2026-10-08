# Puck.World.Server.Tests

These laws check the authoritative simulation and what drives it: the tick and
its mutation journal, bodies and contact, authority and grants, admission,
crossings and federation between authorities, replay and history, the
server-only console modules, addons, storage, and the release coordinator. The
suite references `Puck.World.Server`, `Puck.World.Console`,
`Puck.World.Machines`, `Puck.World.Addons` and `Puck.World.Embeddings`, and no
client, renderer, graphics backend or transpiler, so a change there never
selects it.

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md). It
links the server vocabulary hooks, which cannot install the client's binding,
input and context-family checks, and the empty machine catalog, so a law that
needs either the client's refusals or a Gaming Brick core belongs in
`Puck.World.Client.Tests` or `Puck.World.Machines.Tests`.

`WorldCompilationAnalysisLawTests` checks that ticks retain installed cost and
hazard analysis, while a rule-order edit replaces it even with the same state
catalog. It also checks loader-to-server admission handoff, mismatched
definition/catalog refusals, and embedded-document validation.
`WorldAuthorityCheckpointLawTests` verifies that a malformed journal base is
refused before replacing live state. `SearchLawTests` checks that refused search
outputs are narrated both after boot and after replacing the arena; output rows
remain unchanged by that refusal. `WorldRigidDynamicsLawTests` checks
exact-touch floor spawns and grounded, rising, and falling facts through rest,
impulse, flight, and landing.

`SeamCrossingOrchestrationLawTests` exercises authored adjacency hysteresis
through the real instance host: a body inside the deadband retains its
authority, and one beyond it transfers within a bounded number of ticks.

Extension hosting tests use fake providers and controlled scheduling time.
`WorldConfiguredExtensionLawTests` composes multiple providers from
configuration, drives request/status tables through the real authority, and
checks restart, revocation, failed composition cleanup, and isolation of invalid
requests. `WorldObservationLawTests` checks complete collection projection into
rows of any cell kind (each field parsed and refused by its own row's kind),
unchanged-read suppression, retained state on failures, ordinary authority
refusal, and replay revocation. `ConfinedStorageLawTests` uses real files for
link, namespace, concurrent replacement, and conditional-write behavior. Run
that class on Windows and Linux x64: Windows covers junctions and hard links,
while Linux also covers file symlinks without the Windows symlink privilege. No
live Azure mutation is part of these tests.

`WorldReleaseMetadataTransitionLawTests` exercises metadata upgrades against a
real checkpoint with gameplay and journal history, continuation under the changed
definition, reverse application against latest state, and undo afterward. It
checks every other definition and checkpoint section, named conflicts, and custom
null presence. JSON object member order may change when a deleted custom key
returns; its value must survive. These laws establish the isolated preservation
rule; the [CLI fixture tests](../Puck.Cli.Tests/README.md) own packaged
qualification integration. `WorldReleaseMetadataPublicationLawTests` exercises
the directory-backed atomic publisher, including interruption before and after
the root write, competing authority, leaving activation during upload, retained
receipts, and retries after candidate progress. These do not exercise Azure
VMSS. The publication control also starts with unfilled draws in the published
package and initialized draws in its gameplay checkpoint.

`WorldReleaseCoordinatorContractLawTests` checks that every canonical manifest
carries the one coordinator contract and that a missing or different contract is
refused by name. `WorldReleasePinLawTests` checks that manifest pins and the
engine image digest refuse uppercase hex. `WorldAuthorityReceiptSnapshotLawTests`
checks a selected root's original index and complete receipt chain, excludes
later publications, and refuses corrupt or inconsistent graphs.
`WorldReleaseReceiptFixtureLawTests` reconstructs a disposable authority around a
real checkpoint with journaled edits. It verifies receipt lookups and
duplicate/conflicting operation decisions after continuation, plus interrupted
uploads, a competing root writer and an existing root. Independent receipt
lookups and duplicate retries inside each packaged engine are checked by the CLI
Docker qualification controls; different-build compatibility still needs
evidence from the actual release pair.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Server.Tests/Puck.World.Server.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
