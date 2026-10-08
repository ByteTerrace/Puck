# World test fixtures

These are the fixtures the World suites share, in the `Puck.World.Testing`
namespace. They are linked into each suite as source rather than built as a
library, so a suite compiles only the fixtures its laws use and references only
the projects those fixtures need. A change to one of these files selects
exactly the suites that link it ([`puck affected`](../../../docs/reference/cli.md#puck-affectedthe-checks-a-change-needs)).

## How the fixtures are split

A fixture that reached past its subject is split into parts by what each part
needs, so a suite below the server or the client can link the part it uses:

| Fixture | Part | Needs |
|---|---|---|
| `Fixtures` | `Fixtures.cs`: the compiler-maintained document builders | the document model |
| | `Fixtures.Server.cs`: `FreshServer`, `WorldFixture`, the stepping helpers | the server, console and machine host |
| `CreationFixtures` | `CreationFixtures.cs`: creation documents and prototypes | creation authoring |
| | `CreationFixtures.Emission.cs`: the stamp pool and its worst-case probe | the client |
| `StateFixtures` | `StateFixtures.cs`: slot rows and cells | the document model |
| | `StateFixtures.Server.cs`: arena transforms and live reads | the server |
| `AuthoredGameFixtures` | `AuthoredGameFixtures.cs`: a shipped game's state program | the transpiler |
| | `AuthoredGameFixtures.Load.cs`: loading and composing a shipped world | the transpiler and machine host |
| | `AuthoredGameFixtures.Server.cs`: a population for identity checks | the server |
| `CompiledWorldFixtures` | `CompiledWorldFixtures.cs`: the compiled-world fixture document | the document model |
| | `CompiledWorldFixtures.Compile.cs`: compiling and booting it | the transpiler and machine host |

Two pairs are chosen at link time. A suite links one `VocabularyHooks`: the
`Client` variant installs the composition roots' own
`WorldSchemaVocabularyHooks`, and the `Server` variant, for a suite below the
client, installs the mutation-kind vocabulary and defers the post-process
checks. A suite links one `TestMachines`: `Bricks` carries both Gaming Brick
cores, and `Empty` carries no machine, so a world declaring a screen machine is
refused there by name.

`SceneProbeScan` and `PerUserRootScan` are the scans the suites hold their own
classes and output to: scene probes run one class at a time, and no assembly a
suite links resolves a per-user world state root.

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
cases. A helper two suites' laws both need moves here as a named fixture rather
than staying a member of one law class.

Step until the observable operation completes, with a finite failure bound.
Use a fixed tick window when elapsed simulation time is itself the claim.
Independent card games run in separate test collections; live servers and
shuffle streams are never shared between tests.

## Collections

Three collections decide what may run together. A class that owns a full host
or a scene probe (`WorldBootHarness`, `WorldSceneEmitter`, `WorldFramePresenter`,
`SdfCompositionFrameSource` or `ComposedSdfWorldFixture`) holds a large reserved
scene, so it joins `SceneProbeCollection`, which runs one class at a time beside
the other parallel classes; each suite that owns such classes holds them to it
through `SceneProbeScan`. A class that bounds the calling thread's allocation
over a host or scene, or that changes process-wide state, joins
`AllocationCollection`, which runs alone after the parallel classes, and a class
that swaps the console writer joins `ConsoleRedirectionCollection`. Device laws
run alone after both, as the
[device-law collection](../../../docs/development/contributing.md#device-laws)
places them. Keep a class out of the serial collections unless one of these
reasons holds: every class there adds its whole duration to the suite's wall
time.

Allocation checks run with tiered compilation disabled, so optimized code is
available from startup. Warm enough to cover initialization and a complete
relevant cadence, rather than running thousands of ticks to wait for JIT
promotion. Preserve population sizes, work limits, and allocation controls.
Network deadline tests use controlled timers and wait for the relevant work
to arrive before expiring it; production timeout lengths need not elapse.

## What an assertion must prove

Assert behavior, not an incidental implementation shape. Counts that belong to
a game or capacity contract are meaningful; enum cardinalities and private
field lists are not. Denial tests need an accepted control. Determinism checks
compare independent runs of the same inputs rather than a historical hash.
Changing a fixture must preserve the condition that can make its law fail.

## Documentation

📚 [Worlds and federation](../../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../../docs/development/contributing.md)
