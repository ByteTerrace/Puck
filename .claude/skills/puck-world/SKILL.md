---
name: puck-world
description: "Maps Puck.World: .puck worlds, rules, grants, console verbs, extensions, the live MCP World. Use to run or verify a World change (stdin script, stale binary, headless run) or debug a refusal."
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

**One live world.** The debug world, the asset package `worlds/debug/` (entry
`debug.puck`, basis `avatars/moth`), is the only live world, with the
garden (`garden.puck`) and the night field (`night.puck`) its painting wing's portals open onto
(`paintings.puck`, in the gallery hall); `debug.md` and `paintings.md` beside them are the
portal demo and its inspection verbs. It boots on the Moth's seat chase
camera: the first authored `views` layout is what a boot shows, and the Moth's
is `play`, followed by inspection layouts (`three-quarter`, `front`, …) that
`view.override layout <name>` selects. The Moth basis (`avatars/moth.puck`)
authors the body, its motion programs, its camera (`seatControl`,
`seatRig "moth-camera"`) and its seat camera feel. A new rule or feature for
the live world goes in `debug.puck` (or a scratch `.puck` world booted with
`--world`). Every other world (the island
`puck.world.json`, its districts, the games) and their canary, parity and
verdict fixtures are quarantined under `experimental/`; do not boot, repair or
retarget them, and read the references' mentions of them as prior art
([decision](../../../docs/decisions/engine-design.md#one-live-world-while-the-campaign-runs)).

**The live World is shared.** The World the owner pairs in runs the main
checkout's build and sources, not a worktree's. Reach it through the `puck` MCP
tools:
`puck_worlds` lists the running Worlds, and `puck_exec`, `puck_capture_frame`
and `puck_state_vector_write` take a `world` argument (attachment id or
process id) that reaches exactly that World; name it whenever more than one
runs. Reads and captures are free. A mutation (`world.row.set`, `world.reload`,
`body.*`, `player.*`, `view.override`) changes what the owner sees: make one
only when the task allows it, and restore what you changed. To run your own
edits, boot your own World from your worktree with its own `--state-dir`,
`--devices none` ([the ruling](#an-agents-world-opens-no-physical-device)) and
`--control` to attach to it, name it on every call, and stop only that
process ([references/hosting-and-release.md](references/hosting-and-release.md)).

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
[the inventory](../../../docs/reference/world-vocabulary.md) from it.

A rule is a `when` gate, a trigger `mode`, an optional `forEach`, and effect
statements. A deadline is a cell holding a due tick: `schedule` writes it and a
later rule gates on `$tick >=` it (the Moth's `boot-boost-*` and `blink-*` rules
are the live pattern), never a remainder decremented each tick:

```text
state {
  world {
    table armed { "0" = 1 }
    table cooldownEnd { "0" = 0 }
    table stale { "0" = 7 }
  }
}

rule "cooldown-arm" {
  when armed[$each] == 1
  mode: Edge
  forEach: armed
  schedule cooldownEnd[$each] in 2s   // scheduleState: Write of the due tick, folds as UpsertStateCell
}

rule "cooldown-expire" {
  when armed[$each] == 1 and $tick >= cooldownEnd[$each]
  mode: Level
  forEach: armed
  remove stale[$each]                 // removeStateCell: Remove, folds as RemoveStateCell
  armed[$each] = 0
}
```

`puck compile <file> --validate` compiles that source and runs the boot
validator over it. Every effect writes the tick's `StateArena` through the rule
frame and folds into an existing state mutation kind, never a path of its own;
the full effect table is in
[references/mutations.md](references/mutations.md#rule-effect-sugar-puck--state-mutation-kind).
`world.row.set` edits a live document row; it is not the way to author a new
rule. Grammar, units and the compile/lint verbs belong to `puck-dsl`.

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
| `src/Puck.World.Vocabulary` | The input vocabulary a world document is validated against (`WorldAffordances`), below Client |
| `src/Puck.World.Transpiler` | The world `.puck` vocabulary: `WorldCompiler`, `WorldCompileCache`, `Vocabulary/` |
| `src/Puck.World.Authoring` | The embedded content families (`puck.creation.v1` `CreationDocument`/`CreationCanonicalizer`) and the sculpting library |
| `src/Puck.World` | The sole composition root: `Program.cs` (every host flag), `PuckWorldLoader`, audio, the screen binder; it ships no world (the debug world is the package `worlds/debug/`) |
| `src/Puck.World.Silo` | The hosted multi-world silo (Orleans activation lifecycle only; game traffic rides the World's own wire) |
| `src/Puck.World.Browser` | The engine in a browser tab: a `browser-wasm` publish of Schema, State and the `.puck` toolchain |
| `src/Puck.World.AgentBridge`, `.AgentHarness`, `.AgentHarness.Azure`, `.Azure`, `.Embeddings` | Optional extensions (agent participants, Azure resource operations, text embeddings); no base world project references them |

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

**An import names; the named file decides what it brings.** `import "skies"`,
never `import "skies.puck"` (PUCK121). A module library (a source emitting no
document) hands its `let`s and templates to the compile; anything else is a
document composed into the importer at load. A library imports only libraries
([composition](references/documents-composition.md#document-composition-basis-and-imports)).

**A composed console operation is a `commands` row, not a C# verb.** The
owner's ruling: console commands are easy to author and compose for safe
operations, without C# work for each. A document's `commands` section names a
command (`debug.go`), its typed parameters and the console lines it runs; the
registry answers it beside the engine's verbs (`help`, bindings, wheel
sectors, `run <name>`) and `world.reload` swaps the set. It runs each line
under the caller's own principal and session policy, calls only what a binding
may name, and never takes an engine verb's name
([reference](../../../docs/reference/commands.md#document-authored-commands);
code: `Puck.Commands/AuthoredCommand.cs`, `CommandRegistry.Authored.cs`,
`Puck.World.Schema/WorldConsoleCommand.cs`). Write a C# verb only for what no
composition of existing verbs expresses. The debug world's `places.puck`
(`debug.go <place>`) is the live example. Adding, renaming or changing the
bindability or audience of a C# verb owes `puck verbs` (regenerates the
catalog `puck compile --validate` checks authored commands against).

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

1. Search `src/Puck.World.Schema` for existing vocabulary first; the value
   often exists already. A camera tunable belongs to the seat camera path:
   framing and pitch limits in `views.seatControl` and the camera program's ops
   (`views.seatRig`), input feel in `seatDefaults.seatCameraFeel`
   (`WorldSeatCameraFeel`: pointer `yawSensitivity`/`pitchSensitivity` in
   radians per pixel, `stickLookRate`, inversion, gyro; absent means zero
   response). A seat reads the feel of the document that owns it: the world's
   for an unclaimed seat, the claiming identity document's own
   `seatDefaults.seatCameraFeel` for a claimed one. So a value for one seat (one
   player's preference, which travels with them) belongs in that identity
   document, and a value in a world's basis changes every world built on it.
   Its console row path is `playerDefaults.seatLook` and its read-back is
   `world.view.camera`.
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
dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices none --exit-after-seconds N --state-dir <tmp> < script.txt > out.log 2> err.log
```

### An agent's World opens no physical device

The owner's ruling: any World an agent boots passes `--devices none`. Without it
a windowed World opens every physical controller (GameInput, XInput, HID pads)
and keeps reading them while its window is in the background, so the owner's
pad presses drive the agent's test World (one boot opened the owner's Xbox pad
as player 1). Focus gating is rejected: the owner's whole-desktop feature needs
the pad read while another window is in front, so the default stays `all`, and
the choice is the operator's per run, never a document member (a document must
not decide whose hardware a run reads). `--devices none` opens no pad, registers
no gamepad manager, arbiter or capture, and drops every OS key, text and pointer
event the window receives before a seat sees it
(`WorldWindowInputFilters.ConsumesEveryEvent`). A framed desktop screen claims no
system-wide hotkey either (`WorldSourcePassthrough.ClaimsSystemChord`), so the
owner's Control+Alt+Escape never reaches the World. stdin, the console,
`player.signal`, `body.*`, schedule rows and MCP drive the World as before. The
boot says `[world.host] physical input devices are off` on stderr; a stderr with
a `[gamepad]` or `[gameinput]` line means the flag was missed. A headless or
offscreen boot opens no device either way, and the flag is harmless there. The
verbs that boot a windowed or offscreen World for you (`puck canary`, `puck
parity`, `puck counters`, `puck verbs`, `puck docs citations`, `puck bench
startup`) pass it themselves (`WorldDevices.NoneArguments`). The owner's own World
(`.claude/launch.json`'s `debug-world`) omits it. Never inject real input to
check the flag: the owner may be holding the pad.

- **Build first.** A failed build leaves the previous binaries in place, so a
  run after it exercises old code and shows nothing about the change, however
  well it behaves. The change is not verified until the build exits 0 after the
  last edit and the same script, rerun through this recipe with its own
  `--state-dir`, shows the behavior. Report what ran (build, command, script,
  what it showed); the commit body carries it.
- `--exit-after-seconds 0` or absent runs until the window closes. `--world`
  is required: a package folder (its `manifest.json` names the entry), a
  `.puck` source or a JSON document; without it the boot refuses. `Program.cs` parses the full flag surface;
  host flags are deployment overrides of the document's `host` section.
- `--state-dir <dir>` gives a hermetic state root; parallel runs each need
  their own.
- **Presentation modes** (`host.presentation`): windowed, `offscreen` (GPU
  composition to images, what `puck parity` boots) and `none`
  (`HeadlessWorldSimulation`, full authority, no GPU, so no capture). The Moth
  basis authors `Windowed`, so the debug world boots windowed. `--headless`
  (or `--headless true`) forces `none` for one run and `--headless false`
  forces windowed; offscreen is a document's `host.presentation`. Check the
  effective mode, overrides included.
- **Sharing the GPU.** A run whose effective mode is `none` uses no GPU: while
  another session holds the GPU grant for a parity, counters or windowed leg, it
  may run beside that leg with its own `--state-dir`, as may builds and other
  CPU work. Windowed and offscreen runs are GPU work and wait for the grant and
  an idle GPU (`puck host load` prints `GPU busy` or `GPU idle`). A process
  search of your own matches its own command line, so exclude it before
  concluding another job is running
  ([verification](../verification/SKILL.md#what-uses-the-gpu)).
- **Capture both streams.** Read-back answers land on stdout; refusals,
  narration, boot lines, capture announcements and `[world.mutation: …]`
  echoes land on stderr.
- **Narrow a large read.** An MCP result carries 1 MiB, and `world.counters gpu`
  alone passes that in a world with many render-graph instances. Ask with
  `summary`, `node <name>`, `pass <label>` or `counter <prefix>` (they combine;
  [the rendering skill](../rendering/SKILL.md#performance-and-cost) names the
  forms), and a source name (`world.counters state`) for the rest.
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
  Compare windowed captures by changed-pixel count: outside the tick-exact
  procedure below two are never byte-identical, and inside it stills of one
  tick in fresh Worlds are, but for a 1-level residue on a few pixels.
- **A capture is a settled still unless asked otherwise.** The ordinary capture
  (`world.screenshot`, `puck_capture_frame`) holds presentation and lets
  temporal views converge, so it never shows flicker, ghosting or trails. For
  those, read what the display presented: `puck_capture_frame` with
  `mode: "presented"` and `frames: 8` (optionally a `region`), or
  `world.screenshot <path.png> presented 8`, which report each frame's changed
  pixels and luma change against the one before
  ([console](references/console.md#settled-stills-and-presented-bursts)).
- **Capture motion on exact ticks.** `puck motion` does it for the Moth in one
  command (scenario file `worlds/debug/motion.scenarios.json`, contact sheets,
  `--reproduce`); the rest of this item is the procedure under it. Calls over MCP land wherever the World
  has stepped to, so a script whose inputs and stills must land on exact ticks
  is armed in one line with `world.at` and waited for with `world.wait until
  <tick>`; `world.lockstep on` makes a windowed World's presentation repeat
  with the ticks, and `view.reseed <body>` beside a `body.pose` restarts its
  gait. Every settled still names its tick and state hashes
  (`[still] … tick= hash= pose= shown=`), and `slots` writes each viewport of a
  multi-camera layout. The procedure and what repeats:
  [tick-exact debug capture](references/schedules-and-tests.md#tick-exact-debug-capture).
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
  dotnet run --project src/Puck.World -c Release -- --world worlds/debug --devices none --exit-after-seconds 6 --state-dir "$TMP/puck-state"
```

## Subsystem index

| Working on | Read |
|---|---|
| Document schema, serialization, validators, profiles, binding layers, capacity constants | [documents.md](references/documents.md) |
| Tick order, mutation kinds, journal and undo, rule sugar lowering | [mutations.md](references/mutations.md) |
| Grants, principals, verdicts, budgets, refusals, `world.why` | [authority.md](references/authority.md) |
| `SubmissionEnvelope`, the one queue, completions, echo routing | [ordered-domain.md](references/ordered-domain.md) |
| HUD schema, overlay reservation, bindings, HUD verbs | [hud.md](references/hud.md) |
| Camera rigs, seat control and seat camera feel, layouts, `world.view.*` and `view.override` | [views.md](references/views.md) |
| Boundaries, federation, handoff, crossing logs | [adjacency-and-federation.md](references/adjacency-and-federation.md) |
| `body.engage`, control applications, sitting at a tabletop (board `engage`, `board(pointer, …)` and its seat reads, the board view), machines, a machine's `contentFolders` (shipped and local, which may be absent), `machine.swap`, a folder's local boot default (`folder.json`, `machine.default`, `preferFolderDefault`), the debug world's device row | [engagement.md](references/engagement.md) |
| Join, leave, park, resume | [session-lifecycle.md](references/session-lifecycle.md) |
| The replay tape, verify semantics, `world.history` | [replay.md](references/replay.md) |
| Addon rows, mount transaction, channels, fuel | [addons.md](references/addons.md) |
| Command modules, routing, output contract, screenshots | [console.md](references/console.md) |
| Render validation, stamp capacity, rigid/carry/tether facets | [documents-render.md](references/documents-render.md) |
| Navigation, the tabletop primitive, records and pools | [documents-state.md](references/documents-state.md) |
| Boot-only sections (gravity, portals, simulation rate, references), `basis`/`imports` composition, the validator, serialization, the sculpting library | [documents-composition.md](references/documents-composition.md) |
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
- The debug world uses the analytic contact provider (`collision.requirements`
  empty): a body collides as its volumes' world box (the Moth is 0.7 m square
  and 2.06 m tall) and a placement's primitive as its own box, so a box is
  exact and every boolean carve collides as the uncarved shape. Build a doorway,
  window or stairwell as a union of boxes around the opening, never a
  subtraction, and keep 2.4 m of headroom over every tread and landing (her
  height plus the 0.3 m step-up raise). `worlds/debug/hall.puck` is the
  worked building: switchback stairs of 0.25 m risers, slabs holed by unions,
  and a lift.
- Input has independent budgets of 32 numeric axes and 64 packed buttons. The
  debug modules import one vocabulary in `worlds/debug/inputs.puck`; chess uses
  shared `confirm` and `cancel`, independently of machine `select`. Resolve
  names through `WorldChannelTable`: a declaration index is not a storage
  address. Target-register reach has its own mask and budget. Relative input
  shares the numeric bank as Q32.32 displacement: use the table's typed reader,
  never a held-level reader. The host accumulates samples and drains once per
  routed tick; rules read a Q48.16 projection with authoritative fractional
  residue. Binding compilation must receive the routed channel shapes.
  [Input channels](../../../docs/decisions/input-channels.md) owns this model
  and the remaining authoritative-mode, release and timed-history steps.
  Switching modes releases by default; declared same-meaning holds and toggles
  may continue, while changed meanings and transition edges never cross the
  epoch barrier.
- A kit whose collider differs from the body's last one (a kit `select` by a
  body register: the suit, noclip) steps the body to the nearest pose up its
  axis or along world X or Z where the new volume is not embedded
  (`WorldBody.EscapeEmbedding`), never down: the ordinary solve's shallowest
  push from deep inside a box can be down through the floor. The debug world's
  noclip (`worlds/debug/noclip.puck`) is a collider-less free-flight kit
  selected through the `form` register (suit + 2 × noclip).
- A moving floor is an attached solid placement riding a body (`attach` plus
  `solid`, analytic provider only); a body grounded on it rides it. The gallery
  lift moves its car body with a rule that `poseCell`s it along a box topology
  of 1/16 m layers each tick, so its speed and floor stops are exact.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-dsl` | `.puck` grammar, modules, units, the compile/lint/format verbs, PUCKnnn diagnostics. |
| `sdf-authoring` | Creation shapes, materials and rigs. |
| `rendering` | The renderer and SDF VM the frame source feeds. |
| `gaming-bricks`, `rom-forge` | Emulators behind engaged screens; cartridges. |
| `maths-usage` | Fixed-point primitives on simulation value paths. |
| `verification` | The landing bar, the private CLI copy, GPU grants and flake-versus-failure. |
