# Puck.World.Tests

These tests check the document, protocol, authoritative simulation, and the
shipped games' state programs. Rendering and complete game interaction still
need verification by running Puck.World.

## Keep the feedback loop short

Use the smallest fixture that exercises the behavior under test:

- `Fixtures.BuildDocument` supplies a compiler-maintained world for engine laws.
  `FreshServer` validates its serialized document and owns a fresh server and
  scratch directory for every case.
- `AuthoredGameFixtures.Program` loads a shipped game's state, rules, patterns,
  and tables into that minimal world. A poker hand must not build the Nexus
  navigation graph or simulate unrelated creatures.
- `RuleFrameFixture` compiles a state program once per test, then reloads every
  candidate's values and derived boards. Exhaustive rule checks retain all
  candidate combinations; physical sampling and mutation admission use server
  tests alongside them.
- Composition checks load the complete Nexus once. Placement-identity checks
  retain its complete placement order and kit assignments, but use one-cell
  navigation domains because they do not advance the simulation.

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
`ConfinedStorageLawTests` uses real files for link, namespace, concurrent
replacement, and conditional-write behavior. Run that class on Windows and
Linux x64: Windows covers junctions and hard links, while Linux also covers
file symlinks without the Windows symlink privilege. No live Azure mutation is
part of these tests.

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
