---
name: puck-world
description: Maps Puck.World across its document and Protocol model, authoritative server simulation, composition root, mutation and authority systems, adjacency and federation, ordered submissions, HUD and views, engagement and session lifecycles, addons, replay, extensions, console verbs, and `.puck` world-DSL authoring. Use whenever changing or diagnosing any src/Puck.World* project, especially console verbs, mutation kinds, document sections, grants or refusals, transfers, seamless boundaries, HUD or view bindings, addon or replay behavior, client/server seams, and authoring or refusing a world's `.puck` source — rule/gate/effect sugar, `world.row.set` vs. `.puck` authoring, and PUCK0xx world-vocabulary refusals (route pure language/grammar/CLI questions to `puck-dsl` instead). Also use before writing stdin-driven game runs, because it gives the run recipe and the encoding, indexing, drain, screenshot and replay-proof rules.
---

# Puck.World

A map of the World: where each part lives, the contracts that bite, and how to
run it. Each project's README is the developer reference; the files under
`references/` hold each subsystem's contracts. When this file and the code
disagree, the code wins and this file is corrected in the same change. Treat
counts and inventories here as snapshots and check them against the tree.

Creation authoring (shapes, profiles, panels, folds, contact tuning) belongs to
`sdf-authoring`; the `.puck` language core to `puck-dsl`; the renderer to
`rendering`.

**One live world.** The debug world (`src/Puck.World/Assets/worlds/debug.puck`,
basis `avatars/moth`) is the only live world and the default boot, with the
garden (`garden.puck`) its portal frame opens onto; `debug.md` beside them is
the portal demo and its inspection verbs. Every other
world (the island `puck.world.json`, its districts, the games) and their
canary, parity and verdict fixtures are quarantined under `experimental/`; do
not boot, repair or retarget them
([decision](../../../docs/decisions/engine-design.md#one-live-world-while-the-campaign-runs)).

## The model

Everything is data. A versioned JSON document (`puck.world.definition.v1`)
describes what runs; the engine validates, simulates, renders and replays it
deterministically. The world is one bootable experience: durable configuration
is a document field, live operation is a console verb, and there is no
`--flag` mode and no environment-variable surface. **A baked C# constant is the
same violation as a flag:** if different worlds would want the value different
(a sensitivity, clamp, radius, timing, speed, which button arms a mode), it is
a document field in its first commit. Legitimate constants are capacity bounds,
representation and determinism constants, and math.

The console is the control plane: process stdin drives verbs, stdout and stderr
echo results, and the on-screen console is only a mirror of that pipe.

A world is authored in `.puck` source. `Puck.World.Transpiler` (the world
vocabulary, a peer of the cartridge vocabulary over the `Puck.Transpiler` core)
lowers it to the same JSON. `PuckWorldLoader.TryResolveWorld` compiles a
`--world <x>.puck` path in memory through `WorldCompileCache` before composing
and validating it exactly like a JSON boot, and `world.reload` recompiles it
from disk. A world source has no committed JSON beside it; the build compiles it
into its output. Every construct of the world vocabulary is described once in
`src/Puck.World.Transpiler/Vocabulary/`, and `puck vocabulary` generates
[the inventory](../../../docs/reference/world-vocabulary.md) from it. The
rule/gate/effect sugar and what each lowers to (`schedule` → `scheduleState`,
`remove` → `removeStateCell`, folding through the rule frame) are in
[references/mutations.md](references/mutations.md). `world.row.set` edits a
live document row; it is not the way to author a new rule.

## Projects

| Project | Owns |
|---|---|
| `src/Puck.State` (+ `.Generators`, `.Topology`, `.Rules`, `.Search`, `.Vectors`) | The state and rule engine beneath the document, with no world concept: sections, rows, cells, the expression IR, `StateArena`, the rule compiler |
| `src/Puck.World.Schema` | What a world is: `WorldDefinition` and its section records, `WorldDefinitionValidator`, serialization, document-embedded wire vocabulary (`PlayerIntent`, `WorldGrant`) |
| `src/Puck.World.Protocol` | What a world says: `WorldCommand`, `WorldMutation`, `SubmissionEnvelope`, `WorldSnapshot`, `IServerLink`, `WorldStateMirror` (every presentation read of state), the adjacency family |
| `src/Puck.Networking` | The wire substrate: `FrameCodec`, `WireReader`/`WireWriter` |
| `src/Puck.World.Server` | The authoritative simulation: `WorldServer` (the tick, the journal), `WorldGrants`, `WorldPopulation`/`WorldBody`, `WorldEngagement`, `WorldReplayTape`, `WorldHistory`, `WorldOutputHub` |
| `src/Puck.World.Console` | Server-side console modules (`world.grant`, `world.row`, `world.state`, `world.wait`, `replay.*`, `world.history`) |
| `src/Puck.World.Addons` | The scripting-guest addon host |
| `src/Puck.World.Machines` | The engine-neutral screen-machine host (`WorldMachineHost`, `WorldMachineCatalog`) |
| `src/Puck.World.Client` | The presentation seam: `WorldClient`, `SeatController`, `WorldSeatBindings` (over `Puck.World.Vocabulary`'s `WorldAffordances`), `WorldCameraRigCompiler`, `WorldFramePresenter`, the stamp pool. References Protocol, never Server |
| `src/Puck.World` | The sole composition root: `Program.cs`, audio, the screen binder, the shipped worlds under `Assets/` |
| `src/Puck.World.AgentBridge`, `.AgentHarness`, `.AgentHarness.Azure` | Optional agent-participant extensions; no base world project references them |

`build/Architecture.props` enforces the layering (`PUCKARCH` diagnostics):
Schema never references Protocol or Server, Server knows nothing about
rendering or input, Client never references Server.

## Contracts that bite

**Determinism.** No wall clock, RNG or float in simulation state; fixed point
from `Puck.Maths`. The simulation rate is a document field
(`WorldDefinition.Simulation.RateHz`, dividing 50400 exactly, default 30). Every
entity advances on the server from a `PlayerIntent`; poses never come from
outside. A deliberate correction to logic is expected to change replay hashes.
Client code is presentation: floats are fine there and nothing feeds back.

**The acting principal.** Every mutating ingress consults the acting principal
before any mutation. The ingress stamps identity (`SubmissionEnvelope.Principal`
on the wire, `CommandContext.Principal` for console text); handlers read
`context.Principal` and never construct a principal, which would launder an
identity. A refusal distinguishes what a grant composes from whether the subject
is legitimate for the actor. Client code never mutates local state before the
server's verdict. [references/authority.md](references/authority.md)

**Read through the caller's disclosure.** A console read-back reads the
`WorldStateReadView` its acting principal is minted
(`IWorldConsoleAuthority.ReadView`), never `WorldServer.Definition` directly.

**One pipeline for change.** A `WorldMutation` buffers through the ordered
domain, drains FIFO at the tick boundary, composes a candidate, revalidates the
whole document, capacity-checks, swaps atomically, journals and delivers. Rule
effects write the tick's `StateArena` and publish once at the end of the tick.
Rendering derives from the delivered definition. The step order and the
add-a-kind procedure: [references/mutations.md](references/mutations.md).

**Paths resolve beside the document** that names them (`WorldDocumentPaths`);
shipped engine content resolves through `PuckPaths.Shipped`.

**Narrate through the hub.** Server never writes to `System.Console`; every line
is a `WorldOutputHub.Narrate` call, bound to stderr (or stdout for answers) by
the composition root.

**Mint generated names through `GeneratedName`** (`$` inside a document, `~` in
a path), never by concatenation.

**The screen binder's device work lives in `WorldScreenBinder.Device.*`**, the
prefix `build/CoverageGpuOnly.txt` lists: what needs a GPU device or its shared
resources, the frame presenter, the render graph's running instances, or a
camera or capture handing over frames. `WorldScreenBinderLawTests` reaches every
line of the other binder files on the CPU, so a binder member that needs no
device lands there with a test.

**Adding an authorable feature owes, in one change:**

1. Search `src/Puck.World.Schema` for existing vocabulary first (a camera
   tunable, for example, belongs to the seat camera path:
   `views.seatControl`/`playerDefaults.seatLook` and the camera program's ops).
2. Author the value as a document record with a default that preserves today's
   behavior. Behaviour is an operation, or an operation's parameter, in the
   program vocabularies (the owner's ruling): body movement in
   `bodyMotionPrograms` (the program carries its ops' parameters, as `target`
   and `planarTarget` do), camera framing and smoothing in the camera program's
   ops. Never add a knob to a kit or tuning record beside them; where a
   hard-coded behaviour blocks a program from expressing something, move it
   into an op the program authors.
3. Validate it in `WorldDefinitionValidator`, refusing by name.
4. Sweep the shipped worlds; parsing is strict and refuses unmapped members.
5. Add a read-back verb that echoes the decision (`world.why`, `world.grants`,
   `world.status`, `world.hud` are the pattern).
6. Fold any derived cost into `world.budget`.

Then regenerate with `puck schema` (JSON Schemas, the dashboard types and
`WorldModelShape.generated.cs`); never hand-edit those outputs.

## Running it

```text
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds N --state-dir <tmp> < script.txt > out.log 2> err.log
```

- **Build first.** A failed build leaves the previous binaries in place, so a
  run after it exercises old code and shows nothing about the change. Fix the
  build until it exits 0 after the last edit, then rerun the same script.
- `--exit-after-seconds 0` or absent runs until the window closes. `--world`
  takes a `.puck` or JSON path. `Program.cs` parses the full flag surface;
  host flags are deployment overrides of the document's `host` section.
- `--state-dir <dir>` gives a hermetic state root; parallel runs each need
  their own.
- **Presentation modes** (`host.presentation`): windowed, `offscreen` (GPU
  composition to images, what `puck parity` boots) and `none`
  (`HeadlessWorldSimulation`, full authority, no GPU). A headless run uses no
  GPU and may run beside GPU work; windowed and offscreen runs are GPU work.
  Check the effective mode, deployment overrides included.
- **Capture both streams.** Read-back answers land on stdout; refusals,
  narration, boot lines, capture announcements and `[world.mutation: …]`
  echoes land on stderr.
- **The drain barrier.** A following immediate verb waits for pending
  simulation traffic, so write-then-read pairs need no polling.
  `world.wait <ticks>` holds the issuing session; a windowed script that reads
  rendered work waits on `world.wait ready <seconds>`, one that reads the baked
  frame (every residency's mesh-path bake done) on `world.wait bakes <seconds>`,
  and one that reads a capture on `world.wait captures <seconds>`. Each form needs its seconds deadline: a
  bare `world.wait ready` is refused as not a tick count, and the script then
  runs on without waiting. End a script with `quit`. Blank lines and `#`
  comments are skipped.
- **Encoding.** Pin `[Console]::OutputEncoding` and `$OutputEncoding` to UTF-8
  without a BOM (`[System.Text.UTF8Encoding]::new($false)`): a BOM written into
  piped stdin corrupts the first command.
- **Indexing.** `body.*` verbs and `world.grant body:<n>` take the 0-based
  entity index; `player.*` verbs take 1-based seat numbers.
- **Screenshots arm, they do not capture.** `world.screenshot <path.png>`
  echoes `pending` on stdout; the file is announced on stderr
  (`[capture] … -> <path>`). Fence a frame with `world.wait` before reading it.
  Two windowed captures are never byte-identical; compare by changed-pixel
  count.
- **A denial check needs a control.** Pair it with an actor who holds the grant
  and succeeds, and keep actor and target different (every seat is seeded wide).
- **Pixels are not a replay proof.** `replay.verify` MATCH proves the hashed
  authoritative state trajectory, not the HUD or the image
  ([references/replay.md](references/replay.md)).
- `puck test <source>` boots each `.puck` `test` block headless and prints one
  verdict per line ([references/schedules-and-tests.md](references/schedules-and-tests.md)).

Verify game behavior by running the game (`AGENTS.md` rule 3). Write a test only
where running cannot show the behavior. Standalone runners, record directories
and `--validate-*` flags are banned.

Smoke session:

```text
printf 'world.status\nbody.where 0\nworld.grants console\n' |
  dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6 --state-dir "$TMP/puck-state"
```

## Subsystem index

| Working on | Read |
|---|---|
| Document schema, serialization, validators, profiles, binding layers, capacity constants | [documents.md](references/documents.md) |
| Tick order, mutation kinds, journal and undo, rule sugar lowering | [mutations.md](references/mutations.md) |
| Grants, principals, verdicts, budgets, refusals, `world.why` | [authority.md](references/authority.md) |
| `SubmissionEnvelope`, the one queue, completions, echo routing | [ordered-domain.md](references/ordered-domain.md) |
| HUD schema, overlay reservation, bindings, HUD verbs | [hud.md](references/hud.md) |
| Camera rigs, seat control and seat look, layouts, `views.*` verbs | [views.md](references/views.md) |
| Boundaries, federation, handoff, crossing logs | [adjacency-and-federation.md](references/adjacency-and-federation.md) |
| `body.engage`, control applications, machines | [engagement.md](references/engagement.md) |
| Join, leave, park, resume | [session-lifecycle.md](references/session-lifecycle.md) |
| The replay tape, verify semantics, `world.history` | [replay.md](references/replay.md) |
| Addon rows, mount transaction, channels, fuel | [addons.md](references/addons.md) |
| Command modules, routing, output contract, screenshots | [console.md](references/console.md) |
| Render validation, stamp capacity, rigid/carry/tether facets | [documents-render.md](references/documents-render.md) |
| Navigation, the tabletop primitive, records and pools | [documents-state.md](references/documents-state.md) |
| Districts and the sculpting library | [documents-composition.md](references/documents-composition.md) |
| How a body's look animates: creation drivers, swings, effectors and look followers, all read in `WorldStampPool.PackTransforms` (presentation only); the moth's turning, directional gait and stepping feet are authored there, never in client code | [documents-composition.md](references/documents-composition.md), [sdf-authoring rig](../sdf-authoring/references/rig.md#how-the-moth-moves) |
| Extensions (`IPuckExtension`, `PuckExtensionSet`, installed `extensions/<name>/<name>.dll`), agent participants, the silo, MCP, release | [hosting-and-release.md](references/hosting-and-release.md) |
| `captures`, `schedule`, verdict rows, `puck test` | [schedules-and-tests.md](references/schedules-and-tests.md) |

## Limits worth knowing

- `WorldPlacementPolicy.MaxShapesPerStamp` is 367 (a panelled shape charges 2).
- `WorldBodiesLimits.CapacityCeiling` is 4096 bodies; detailed rigs cover the
  first 128 indices.
- `SdfProgramBuilder.MaxInstances` is 65536; `WorldRenderEnvelope.TryFit` is the
  apply-time capacity gate.
- XInput caps at four pads locally; HID pads are uncapped.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-dsl` | `.puck` grammar, modules, units, the compile/lint/format verbs, PUCKnnn diagnostics. |
| `sdf-authoring` | Creation shapes, materials and rigs. |
| `rendering` | The renderer and SDF VM the frame source feeds. |
| `gaming-bricks`, `rom-forge` | Emulators behind engaged screens; cartridges. |
| `maths-usage` | Fixed-point primitives on simulation value paths. |
