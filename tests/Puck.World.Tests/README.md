# Puck.World.Tests

These tests check the document, protocol, authoritative simulation, and the
shipped games' state programs. Rendering and complete game interaction still
need verification by running Puck.World.

`WorldCompilationAnalysisLawTests` checks that ticks retain installed cost and
hazard analysis, while a rule-order edit replaces it even with the same state catalog.
It also checks loader-to-server admission handoff, mismatched definition/catalog refusals, and
embedded-document validation. `WorldAuthorityCheckpointLawTests` verifies that a
malformed journal base is refused before replacing live state.

`SearchLawTests` checks that refused search outputs are narrated both after boot
and after replacing the arena; output rows remain unchanged by that refusal.

`WorldRigidDynamicsLawTests` checks exact-touch floor spawns and grounded, rising,
and falling facts through rest, impulse, flight, and landing. `PaddleballContactLawTests`
checks the shipped ball and court from both touching and elevated spawns.

`BakeSamplingDeviceLawTests` uploads the bake sampling fixture's BC7, BC5 and
BC6H textures with every mip level through the image upload and samples each
probe texel at its level with `Assets/Shaders/bake-sampling.comp.hlsl`, on the
first Vulkan device with a graphics queue, the first Direct3D 12 adapter and
WARP, skipping by name on a host without one. `StagedRegionDeviceLawTests`
runs an uploaded source's conversion on the same three devices with its region
staged, chosen by handing the runtime the device's own memory profile with no
host-visible device-local bytes, and holds a capture of each tick's image to the
CPU reference byte for byte; its Direct3D 12 hardware leg turns the debug layer
on and fails on any `[d3d12-debug]` line, so the law runs alone in
`DebugLayerCollection`. The device laws share `tests/Shared`'s
`HeadlessVulkanDevice` and `DirectXTestDevices`. `SharedFenceLawTests` orders a
Direct3D 11 writer and a Direct3D 12 or Vulkan reader by a shared fence alone.

`SeamCrossingOrchestrationLawTests` exercises authored adjacency hysteresis through
the real instance host: a body inside the deadband retains its authority, and one
beyond it transfers within a bounded number of ticks. `AuthoredAdjacencyHysteresisLawTests`
checks that reciprocal documents cannot disagree about that deadband.

## Keep the feedback loop short

Use the smallest fixture that exercises the behavior under test:

- `Fixtures.BuildDocument` supplies a compiler-maintained world for engine laws.
  `FreshServer` validates its serialized document and owns a fresh server and
  scratch directory for every case.
- `AuthoredGameFixtures.Program` loads a shipped game's state, rules, patterns,
  and tables into that minimal world. A poker hand must not build the Nexus
  navigation graph or simulate unrelated creatures.
- `RuleArenaFixture` compiles a state program once per test, then loads each
  candidate into the arena and judges it as one tick, with the derived boards
  the arena's own import recomputes. A row the arena already holds unchanged —
  the same row instance, its generation unmoved — is not reloaded, so build
  candidates by replacing only the rows that vary and reuse the rest. Exhaustive
  rule checks retain all candidate combinations; physical sampling and mutation
  admission use server tests alongside them.
- A deterministic run several laws read is computed once and shared as its
  immutable results, never as a live server: `ShippedWorldIdleRuns` holds two
  independent idle boots of the island, and `ShippedWorldStateBaselines.Run`
  one replay per shipped world.
- Composition checks load the complete Nexus once. Placement-identity checks
  retain its complete placement order and kit assignments, but use one-cell
  navigation domains because they do not advance the simulation.
  `AuthoredGameFixtures.Load` loads each shipped or fixture world once per run;
  derive from the shared definition with `with`, never mutate it.

Build a law's raw material from the shared fixtures rather than a private
copy: `CreationFixtures` for creation documents, prototypes, and both emission
paths (including the one worst-case pool probe), `StateFixtures` for slot rows,
cells, state reads and writes, `AudioAssetFixtures` for music, tune, and patch
rows, `ClientFixtures` for a server-less client, `WorldFixture.JoinSeat` and
`SettleSearch` for a live server, and `Laws` for every denial with its control.
A family of laws that differ only in data is one theory whose rows name the
cases.

Step until the observable operation completes, with a finite failure bound.
Use a fixed tick window when elapsed simulation time is itself the claim.
Independent card games run in separate test collections; live servers and
shuffle streams are never shared between tests.

Allocation checks run with tiered compilation disabled, so optimized code is
available from startup. Warm enough to cover initialization and a complete
relevant cadence, rather than running thousands of ticks to wait for JIT
promotion. Preserve population sizes, work limits, and allocation controls.
Network deadline tests use controlled timers and wait for the relevant work
to arrive before expiring it; production timeout lengths need not elapse.

Extension hosting tests use fake providers and controlled scheduling time.
`WorldConfiguredExtensionLawTests` composes multiple providers from configuration,
drives request/status tables through the real authority, and checks restart,
revocation, failed composition cleanup, and isolation of invalid requests.
`WorldObservationLawTests` checks complete collection projection into rows of any
cell kind (each field parsed and refused by its own row's kind), unchanged-read
suppression, retained state on failures, ordinary authority refusal, and replay
revocation. `WorldRenderEnvelopeLawTests` exercises the real scene capacity probe
without a GPU, including new per-shape placements within authored headroom.
`ConfinedStorageLawTests` uses real files for link, namespace, concurrent
replacement, and conditional-write behavior. Run that class on Windows and
Linux x64: Windows covers junctions and hard links, while Linux also covers
file symlinks without the Windows symlink privilege. No live Azure mutation is
part of these tests.

`WorldReleaseMetadataTransitionLawTests` exercises metadata upgrades against a
real checkpoint with gameplay and journal history, continuation under the changed
definition, reverse application against latest state, and undo afterward. It checks
every other definition and checkpoint section, named conflicts, and custom null
presence. JSON object member order may change when a deleted custom key returns;
its value must survive. These laws establish the isolated preservation rule;
the [CLI fixture tests](../Puck.Cli.Tests/README.md) own packaged qualification
integration. `WorldReleaseMetadataPublicationLawTests` exercises the
directory-backed atomic publisher, including interruption before and after the
root write, competing authority, leaving activation during upload, retained
receipts, and retries after candidate progress. These do not exercise Azure VMSS.
The publication control also starts with unfilled draws in the published package
and initialized draws in its gameplay checkpoint. Silo lifecycle controls cover
local-only rows signing as their stable instance names through replacement and
stale-writer refusal.

`WorldReleaseRewindProbe.cs` contains one focused local lifecycle check through
actual hosted rows, release controls, and the directory store. It deploys metadata,
advances gameplay, rolls back while retaining the latest ticks, and intentionally
rewinds a later internal transfer. It interrupts restore after the first durable
world publication, resumes with admission closed, and compares complete restored
checkpoints. Disconnected players' documented parking is checked explicitly,
including reconnect; current operation receipts and fresh writer generations
survive. Restart and completed-operation resume must retain subsequent progress.
A rejected private rewind recovers its fresh drain state; interrupting recovery
before activation must preserve that choice when a new coordinator resumes.
Repeating a fully published admission still checks fences and the group CAS,
and completes after one pump boundary without registering routes again.
This checks the coordinator and storage behavior in one compiled engine; packaged
runtime and live Azure acceptance remain separate.

`WorldReleaseCoordinatorContractLawTests` checks that every canonical manifest
carries the one coordinator contract and that a missing or different contract is
refused by name. `WorldReleasePinLawTests` checks that manifest pins and the
engine image digest refuse uppercase hex.

`WorldAuthorityReceiptSnapshotLawTests` checks a selected root's original index
and complete receipt chain, excludes later publications, and refuses corrupt or
inconsistent graphs. `WorldReleaseReceiptFixtureLawTests` reconstructs a disposable
authority around a real checkpoint with journaled edits. It verifies receipt
lookups and duplicate/conflicting operation decisions after continuation, plus
interrupted uploads, a competing root writer and an existing root. `WorldReleaseCutoverLawTests`
also delays the live export's root read while a later mutation arrives, and checks
that cancellation leaves the publication queue usable. Archive laws cover receipt
pins, interrupted uploads, canonical decoding and a row missing its receipt pin. Independent
receipt lookups and duplicate retries inside each packaged engine are checked by
the CLI Docker qualification controls; different-build compatibility still needs
evidence from the actual release pair.

## What an assertion must prove

Assert behavior, not an incidental implementation shape. Counts that belong to
a game or capacity contract are meaningful; enum cardinalities and private
field lists are not. Denial tests need an accepted control. Determinism checks
compare independent runs of the same inputs rather than a historical hash.
Changing a fixture must preserve the condition that can make its law fail.

Measure execution separately from restore and build:

```powershell
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --no-build --logger trx
```

Review slow TRX cases before reducing workloads. Do not make the default run
fast by silently excluding functional coverage.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
