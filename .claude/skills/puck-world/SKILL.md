---
name: puck-world
description: Guides work on Puck.World across its document and Protocol model, authoritative server simulation, composition root, mutation and authority systems, adjacency and federation, ordered submissions, HUD and views, engagement and session lifecycles, addons, replay, console verbs, and `.puck` world-DSL authoring. Use whenever changing or diagnosing any src/Puck.World* project, especially console verbs, mutation kinds, document sections, grants or refusals, transfers, seamless boundaries, HUD or view bindings, addon or replay behavior, client/server seams, and authoring or refusing a world's `.puck` source — rule/gate/effect sugar, `world.row.set` vs. `.puck` authoring, and PUCK0xx world-vocabulary refusals (route pure language/grammar/CLI questions to `puck-dsl` instead). Also use before writing stdin-driven game verification because it defines the supported run recipes and encoding, indexing, collision, drain, screenshot, and replay-proof constraints.
---

# Puck.World: the game of many games

Keep this skill factual and procedural: record settled contracts, their exact
seams, and how to verify them. Let the user's current instruction outrank this
file. If the skill contradicts a demanded change, update it in the same change.
Treat counts, inventories, quarantine status, and other repository-state claims
as snapshots: verify them against the current tree before relying on them.

Creation authoring (shapes and profiles, panels, domain folds, drivers,
contact tuning) belongs to `sdf-authoring`. Render validation, stamp
capacity, and the stamp pool are in
[references/documents-render.md](references/documents-render.md);
`world.sdf.dump` is in [references/console.md](references/console.md); the
sculpting library is in
[references/documents-composition.md](references/documents-composition.md).

## The model in one paragraph

Treat everything as data: versioned JSON documents (`puck.world.definition.v1` — the world
itself, and, seeded from it, one per owned identity) describe what runs; the
engine renders, composites,
validates, and replays them deterministically. The world is ONE bootable
experience — no sibling `--flag` modes; durable configuration is document
fields, live operation is console verbs, and there is no `PUCK_*`
configuration surface for this game. **A baked C# constant is the same
violation as a flag, and the commonest one**: the discriminator is whether Nexus, Dive, Kart, and Jump
would each want the value different — sensitivities, clamps, radii, timings,
speeds, which button arms a mode. If yes, it is a document field in its FIRST
commit, never a constant to migrate later. Before writing any feature carrying
a tunable number, search `src/Puck.World.Schema` for existing vocabulary: never build
a bespoke mouse-orbit with hardcoded sensitivity and
pitch clamps when the camera program's `orbit`/`clampPitch` ops and the
authored `views.seatRig` already exist. Legitimate constants: capacity bounds that size memory or the
wire, representation/determinism constants, and math. The console is the
control plane:
process stdin drives verbs, stdout/stderr echo results, and the on-screen
console is only a MIRROR of that pipe — nothing that draws (including a HUD
`replace` panel taking over the whole overlay) can take the control plane
away. Verify game behavior by RUNNING the game, never by a build gate
(`CLAUDE.md` rule 3).

A world document is authored in `.puck` source, not hand-written JSON.
`Puck.World.Transpiler` — the `puck.world.definition.v1` vocabulary, a peer of
`Puck.GamingBricks.Transpiler`'s `puck.cartridge.v1`, both riding the
schema-agnostic `Puck.Transpiler` core — lowers a parsed `.puck` document to
the same JSON this file describes; JSON stays the wire form and the
checked-in shape of every shipped world. `Puck.World`'s boot loader
(`PuckWorldLoader.TryResolveWorld`) transparently compiles a `--world
<x>.puck` path before composing and validating it exactly like a JSON boot.
Every door that needs only a source's documents (the boot, the composer's
basis and import reads, `world.reload`, `puck test`) compiles through
`WorldCompileCache`, which serves a held compile while every file fact it read
(`CompileInputs`) still holds and persists per user, so an unchanged source is
never compiled twice. A boot then takes its drawn definition from a compiled
world (`CompiledWorld`, `<name>.puckb`: a `PWLD` chunk container whose header keys
the engine build, catalog fingerprint, authored-definition hash and instance,
holding `DEFN`, `ASST` and `BAKE`, the keys of the creation bakes it needs in
the build's one bake pack, `bakes.puckbake`) beside its document or
in the per-user `compiled-worlds` cache (shared by every boot whatever its state
root), re-derives only the chunks whose version, inputs or dependencies moved,
ignores a file whose header differs, and writes what it derived into that cache
(`CompiledWorldCache`); a chunk that does not
derive on boot (`BAKE`) is left out there and covered by background work. The
build ships one beside every world in
`Assets/worlds` and `puck compile` writes one beside each document. A new
derived product is a chunk registered through `CompiledWorldChunks.With`, never
a second cache; the contract is in
[the worlds manual](../../../docs/architecture/worlds.md#compiled-worlds). A
boot's work is counted by the `world.boot` work source (`WorldBootWork`,
`world.boot.compiled-hits` and `world.boot.chunk-derivations` among its kinds);
a law attributes its own ledger to read it. The flagship `puck.world.json` itself has no `.puck` source today
— it remains hand-authored JSON, while the avatar/courtyard/tool worlds and
both shipped CGB cartridges are DSL-authored (`git ls-files '*.puck'` is the
current inventory; treat it, not this sentence, as the source of truth).
Grammar, `let`/`template`/modules, units, and diagnostics belong to
`puck-dsl`; this skill owns only the world vocabulary's own sugar and
semantics — the rule/gate/effect mapping in
[references/mutations.md](references/mutations.md).

Every construct of that vocabulary is described once, in
`src/Puck.World.Transpiler/Vocabulary/`: keyword, members with their kinds and
defaults, the document member it lowers to, and what the printer requires
before it may print a node back as that construct. The parser's
embedded-language test, the decompiler's sugar guards, and the language
server's completion and hover read it, and `puck vocabulary [--check]`
generates [the inventory](../../../docs/reference/world-vocabulary.md) from it.
Read the table before deciding a construct's spelling or its refusal, and add a
member there rather than at a reader.

## The world project family

| Project | Owns | Key types |
|---|---|---|
| `src/Puck.State` | The state and rule engine beneath the document, with no world or presentation concept | `IStateSection`/`StateRow`/`StateCell` and the traits (`StateAdvance`/`StateDynamics`/`StateCycle`), `StateDomain`, `StatePhase`/`PhaseGuard`, `StateVisibility`, `StateCatalog`/`StateHandle`, `StateReader`, `StateArena`, `LatticeTopology`/`CompiledTopology`/`TopologyCompilation`, `Draw`/`StateGenerator`, `PatternNode`, `DynamicsRow`, `ExpressionProgram`/`Instruction`/`ExpressionSpelling`, `ExpressionOp`/`ExpressionArithmetic`, `TableRow`, the `StateTransform` union, `SafeName`/`CellName`, `CellKind`, `RuleFacts`, `ExpressionComparisons` (the comparison subset of `ExpressionOp`)/`ActionTriggerMode`, `Search/SearchPlan` (the resolved job a search runtime walks) — no `World` name; consumers reach them through a project-wide `Using`. Its siblings: `Puck.State.Generators` (`GeneratorEngine`, `CompiledTable`, `TableDocument`/`TableCanonicalizer`), `Puck.State.Topology` (`PatternRow`/`CompiledPattern`, board queries), `Puck.State.Rules` (`RuleCompiler`), `Puck.State.Search` (the walk), `Puck.State.Vectors` |
| `src/Puck.World.Schema` | What a world IS — the document model | `WorldDefinition` + section records (`WorldStateSection`/`WorldStateRow` extend the engine's section and row with the body lanes and the `gatesDrive`/`field` traits; `WorldFieldTopology` is the physical lattice case), `WorldDefinitionValidator`, `WorldDefinitionSerialization` (`WorldJsonContext` over the generated `WorldJsonSourceContext`, `WorldJsonVocabulary` adding the document's arms to the engine's polymorphic bases); authored-to-fixed collider compilation; document-embedded wire vocabulary that keeps the `Puck.World.Protocol` namespace (`PlayerIntent`, `WorldGrant`/`Grantee`/`PrincipalTokens`, admission entries; the actor `Principal` itself lives in `Puck.Commands`) |
| `src/Puck.World.Protocol` | What a world SAYS — the wire/tape vocabulary | `WorldCommand`, `WorldMutation`, `SubmissionEnvelope`, `SessionRequest`, `WorldSnapshot`, `IServerLink`/`IClientSink`/`IWorldServerHost`, `LoopbackTransport`, `WorldAuthorityEndpoint`/`WorldSessionMirror`, the state mirror every presentation read of state goes through (`WorldStateMirror`, over `WorldDocumentStateView`, the delivered definition's `IWorldStateView`; a session mirror and an authority endpoint follow their own with `FollowState`), and the `IWorldAdjacencySource` family (`WorldAdjacencyFramePair`/`WorldAdjacencyProjection`/`IWorldAdjacencyNeighbour`) — `WorldAuthorityEndpoint`/`WorldSessionMirror`/`WorldStateMirror` are namespaced `Puck.World.Client` and the adjacency family `Puck.World.Server` |
| `src/Puck.Networking` | The dialect-agnostic wire substrate | `FrameCodec` (the socketless frame grammar), `WireReader`/`WireWriter`, `WireRefusal`/`WireFailure` |
| `src/Puck.World.Server` | The authoritative sim | `WorldServer` (the tick, the journal), `WorldGrants`, `WorldHandleTable`, `WorldPopulation`/`WorldBody`, World-specific contact orchestration and policy, `WorldEngagement`, `IWorldAddonHost`/`WorldAddonReceipt` (the addon seam interface), `IWorldMachineHost` (the screen-machine seam — the concrete host lives in `Puck.World.Machines`), `WorldOwnedWorlds` (the owned-world identity catalog), `WorldReplayTape`, `WorldOutputHub` |
| `src/Puck.World.Console` | The server-only console command modules | `IWorldConsoleAuthority` (resolves the addressed `WorldInstance`), `WorldGrantCommandModule`, `WorldGroupCommandModule`, `WorldLookCommandModule`, `WorldNetworkCommandModule`, `WorldReplayCommandModule` (the `replay.*` verb surface — the tape and its read-back stay in Server), `WorldRowCommandModule`, `WorldStateCommandModule`, `WorldUpdateCommandModule`, `WorldWaitCommandModule` + `WorldConsoleWaitGate`/`IWorldWaitGateResolver` |
| `src/Puck.World.Addons` | The addon guest host — scripting guests only, with no emulator surface at all | `WorldAddonRuntime`, `WorldAddonMutationDecoder`, `WorldAddonWire`, `AddonMutateRefusal`, `AddonSimulationPump` |
| `src/Puck.World.Machines` | The engine-neutral screen-machine host | `WorldMachineHost` (the `IWorldMachineHost` implementation — boot, per-tick stepping, cable-linking, memory peek/poke, the two-phase prepare/commit/finish lifecycle, cartridge symbol resolution), `WorldMachineCatalog` (immutable host-selected engines and neutral `IMachineContentProvider` registrations, built by `WorldMachineCatalog.From` from the host's composed extensions and also supplied explicitly to admission). References no Gaming Brick core or forge project |
| `src/Puck.World.Client` | The presentation-facing client seam | `PlayerRoster`/`WorldClient`/`SeatController`, the client's own `WorldStateMirror` (`WorldClient.StateMirror`, and `StateMirrorFor` the mirror of whichever authority a seat is routed to) and `WorldStateLease` (a body's or seat's acquired slots, released when it leaves), the camera-program translation (`WorldCameraRigCompiler`, over the document-blind IR in `Puck.SdfVm.Views`), `WorldFramePresenter` (the composed-frame producer)/`WorldSceneEmitter`/`WorldViewComposer`, `WorldSessionSceneEmitter`/`WorldAdjacencySceneEmitter`/`WorldSdfDocumentEmitter`, the stamp/animation pool (`WorldStampPool`/`WorldPlacementStamper`/`WorldScreenStamper`), the SDF document intake (`Sdf/`: `SdfDocumentDecoder`, `SdfDocumentProgram`/`SdfDocumentOp`/`SdfDocumentException`, `SdfRefusal`), `IWorldAudioFrameFeed`/`IWorldAudioCueSink` (the narrow seams the frame/scene producers hold the root's `WorldAudioDirector` through, the `IWorldAudioLever` pattern), and the binding-authoring layer (`WorldSeatBindings`/`WorldAffordances`, and `PlayerCommandNames`/`WorldWheelCommandNames` in `CommandVocabulary.cs`). References `Puck.World.Protocol` and `Puck.Audio`, never `Puck.World.Server`. |
| `src/Puck.World` | The sole composition root | `Program.cs`, `WorldClientSeats` (implements the Server seam `IWorldEmbodiedSeats`), `WorldAudioDirector` (stays here — imports `Puck.World.Audio` types directly; implements Client's `IWorldAudioFrameFeed`/`IWorldAudioCueSink`/`IWorldAudioLever` for the frame/scene producers and the session-lever sink), presentation and the screen-output binder, `Audio/` (document intake, tune hosting, the render device — the mixer core and voice synth live in `src/Puck.Audio`), the command modules that stayed here (`WorldCommandArguments`, the free-text-tail reconstruction shared with `Puck.World.Console`, lives in `Puck.World.Server` since both need it), and the shipped world/scenario documents under `Assets/` |

`src/Puck.Physics` owns the generic kernels the server drives: `Navigation/` (the budgeted, checkpointed A* over surface, volume, and medium grids) and `Fields/` (`FieldLattice`, the reaction integrator behind a `state.lattices` row). Server keeps pair selection, authority, and body-state writes. The static solid field bakes a distance grid at `collision.gridCellSize` (0 = none; the shipped world authors 0.5) and answers the exact program inside a contact band derived from the kit colliders, so a query never marches from scratch; `world.collision.status` echoes the grid. `host.journalDepth` bounds the undo journal (0 = unbounded; entries past the horizon fold forward into the base; `world.status` echoes it). Transfer leases, escrow rows, and parked entries expire off one sorted `WorldDeadlineTable`, never a per-tick sweep of a whole collection.

**The one world and its districts.** `puck.world.json` is the island; every district is a module under `Assets/worlds/modules/` imported under an alias. The district primitives and the derived limits that shape a district are in [references/documents-composition.md](references/documents-composition.md#the-one-world-and-its-districts).

The agent projects are an optional extension family, not members of the base world dependency closure:

| Project | Owns | Key types |
|---|---|---|
| `src/Puck.World.AgentBridge` | The provider-neutral autonomous-participant extension | `WorldAgentBridge`, `WorldAgentMailbox`, `IWorldAgentDispatcher`, `WorldAgentObservation`, `WorldAgentAffordances`, `WorldAgentActionReceipt`; bounded worker-to-simulation dispatch through a mailbox its host drains at closed boundaries (`WorldAgentMailbox.Drain`), explicit-principal reads through `IPrincipalServerLink`, typed body actions, no model or Harness dependency |
| `src/Puck.World.AgentHarness` | The optional Microsoft Agent Framework adapter and the `agent.harness` participant | `WorldAgentHarness`, `WorldAgentHarnessOptions`, `WorldAgentParticipant`, `ChatClientProvider` (kind keyed by provider name, `AddChatClient`); constrained `puck_*` tools over the bridge, the configured `approval` decides whether the action tools are offered at all (`refuse`, the default, offers only observation; `allow` offers move, press and stop unattended), caller-supplied skills, no credentials |
| `src/Puck.World.AgentHarness.Azure` | The optional `azure.openai` chat client provider | `AzureOpenAiChatClientExtension`; `DefaultAzureCredential`, settings `endpoint`/`deployment`/`tenantId`/`managedIdentityClientId`, no key member |

`Puck.World`, its core tests, Schema, Protocol, Server, Client, Console, and Addons must not reference either agent
project. How a host runs an agent participant, the Operator MCP adapter, remote MCP, and their verification are in
[references/hosting-and-release.md](references/hosting-and-release.md#agent-participants-and-the-mcp-attachment).

`src/Puck.Audio` is a sibling engine-services project: the deterministic fixed-point mixer/voice-synth core
(`Puck.Audio.Mixing` — `AudioMixer`/`VoiceSynth`/
`AudioSnapshot`/`MachineAudioRate`) plus sim-state music
(`Puck.Audio.Simulation` — `MusicClock`/`MusicDirector`/
`MusicSenseEdge`, stepped from `WorldServer.Step` right after
`WorldEventFeed.Collect()`), referenced by `Puck.World.Server` (machine audio
rate; `WorldAssetRowLoader` resolves each `WorldMusicRow`/
`WorldTune`/`WorldPatch` reference's document off disk, beside the world document that
authored the row (`puck.music.v1`,
`puck.audio.v1`, `puck.synth.v1` — the same name/source/hash
shape every world audio asset row carries), and
`MusicDirectorFactory` compiles the loaded document into the sim-side shapes
and projects `WorldEventFeed.Edges` into `MusicSenseEdge`) and `Puck.World`
(presentation glue). It parses no document. `music.state` is a
`WorldAudioCommandModule` query verb routed through seat 1's currently
claimed `WorldSeatAuthorityRouter` route — a transferred seat is followed the
same way `PlayerCommandModule`'s drive-a-player verbs are. A rhythm hit
window is an authored `compareState` range over the world-rule operand
`$clock:<music>:phaseError` (the signed tick distance from `MusicClock`'s
current position to the nearest beat), never a dedicated effect or section.

Dependency rules are enforced by the architecture gate (`PUCKARCH`
diagnostics from `build/Architecture.props`): `Puck.World.Schema` references
only its declared leaf/authoring closure plus `Puck.Physics`, which owns the fixed collider vocabulary, and
`Puck.State` with `Puck.State.Rules`, the state and rule engine it consumes and extends (the expression IR and
its spelling, the opcode enum and its arithmetic, state transforms, the validated-identifier family, `CellKind`,
the reserved fact channels, the rule compiler) —
structurally denied backends, presentation, `Puck.Overlays`, `Puck.Input`,
`Puck.World.Protocol`, and `Puck.World.Server`. `Puck.World.Protocol` adds
`Puck.World.Schema` and `Puck.Networking` (the transport-neutral frame/wire
grammar). `Puck.World.Server`
adds `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.Physics`, `Puck.Storage`,
`Puck.Hosting` — and knows nothing about rendering or input; `Puck.World.Addons` carries
`Puck.Scripting` (the addon guest ABI) and its own `AddonSimulationPump`, referencing
`Puck.World.Server` rather than the reverse. The optional `Puck.World.AgentBridge` adds Protocol and Schema
while remaining independent of model runtimes; `Puck.World.AgentHarness` adds the bridge and Microsoft Agent
Framework packages. No base world project references either extension. Physics owns generic contact geometry; Server owns
pair selection, authority, walkability/grounding, obstruction reporting, and body-state writes. The two seams
that legitimately cross: `BindingVocabularyHook` (a `[ModuleInitializer]`
injection so Schema validators reach the input vocabulary; the sibling
`MutationKindVocabularyHook` crosses the identical seam so a
`MutationKindMask` field can round-trip its kind names against Protocol's
mutation-kind catalog), and the overlay capacity the composition root hands
`Puck.Overlays` as constructor data
(`Puck.World.Client.WorldOverlayCapacity.FromSchema()` — see
[references/hud.md](references/hud.md)). Each project's README is the
current developer reference — start there for narrative depth this skill
deliberately does not duplicate.

## Hosting, extensions, and release

The extension contract and host composition, production silo verification, and
the release coordinator are in
[references/hosting-and-release.md](references/hosting-and-release.md).

## Cross-cutting contracts (every task)

**Preserve determinism.** Use no wall clock, RNG, or float in simulation state;
use fixed point from `Puck.Maths` and exact engine-tick durations throughout; the
simulation rate is an authored per-world document field
(`WorldDefinition.Simulation.RateHz`, MUST divide `FixedTickConversion.TicksPerSecond`
50400 exactly), defaulting to 30 Hz, the shipped game's own rate, for a world
that authors no `simulation` section; a world that wants the distinct
resident, non-stepping rate authors `rateHz: 0` by name, and a fixture or law
that needs a higher stress rate authors its own `rateHz`. Every entity is advanced on the server from
a `PlayerIntent` — poses are never accepted from outside the simulation;
drivers only produce inputs, poses flow out through the tick snapshot. The
guarantee pins the MAPPING, not the values: a deliberate correction to math
or logic is EXPECTED to change replay hashes — make the correction and
re-record any persisted tape it invalidates in the same change. Client-side
(`src/Puck.World.Client`, and `src/Puck.World`'s presentation glue) is presentation: floats are fine there, nothing
feeds back into the tick.

**Navigation is authored world truth.** `navigation.domains` owns bounded
`surface`, collision-free `volume`, and live-field-constrained `medium` grids,
and A* stays fixed-point, budgeted, stable-tied, checkpointed, and hashed. The
contract is in
[references/documents-state.md](references/documents-state.md#navigation--bounded-surface-flight-and-medium-routes).

**Enforce the acting-principal rule.** Make every mutating ingress consult the ACTING
principal before any mutation. The ingress stamps identity
(`SubmissionEnvelope.Principal` on the wire, `CommandContext.Principal` for
console text); handlers READ the stamp via `context.Principal` and
never construct a principal — constructing one is laundering an identity.
Client code never mutates local state before the server's verdict
(completions, not discarded replies). Details:
[references/authority.md](references/authority.md).

**Read state through the caller's disclosure.** A console read-back of state values
reads the `WorldStateReadView` its acting principal is minted
(`IWorldConsoleAuthority.ReadView`), never `WorldServer.Definition` directly; a
verb printing values the rules computed is `CommandAudience.Operator`. A dealt
placement is a reading of the cells it was dealt from, and a responsive (`respond`)
placement a reading of the cells its conditions read, so the view (and the wire
projection, through the same `WorldStateDisclosure.Disclose`) re-deals or re-reads
it from the reader's own rows. Every federation egress that knows its traveler
composes for it; the seatless `Observe` lane composes for the public observer. The
local HUD and view bindings stay unfiltered: they draw the one shared screen the author
chose. Details and the laws: [references/console.md](references/console.md).

**Rule writes land on the arena; the document installs once per tick.** During
`EvaluateWorldRules` every state effect writes the host's `StateArena`
(`WorldRuleHost.cs`, `WorldServer.Arena.cs`) inside its own firing's journal
scope, and rules read through the same arena; one rule firing is one scope,
committed on success and rewound on the first refusal. What the arena
accumulated across the tick's firings is published into the installed document
at the end of the tick (`WorldServer.PublishArena`), so every other reader
(bodies, fields, search, the console) sees a rule's write only after that
publication. Only rows whose version moved are read back out of the arena; the
rest of the installed rows are kept. Publication ends in
`WorldDocument.ReconcileStateConsumers`, the same routine a value mutation ends
in, so anything outside the arena that caches a state value (drive gates, field
input, body scale, inhabit counts) is added there, never to one door. A
document-row arm is `EffectNeeds.Transactional`: a firing's rows are prepared
as one `WorldMutation.Batch` through `WorldDocument.TryPrepareMutation` before
the arena scope commits, where any gate's refusal rewinds the firing, and
installed by `InstallPrepared` after it. A gate added to the mutation door goes
in `TryPrepareMutation`; nothing in `InstallPrepared` may refuse. Every other world arm is delivered after
the commit, and a failed delivery is counted and undoes nothing. Row versions on the arena drive the rule scheduler and memoized
bindings (`RuleSchedule`, `IStateReader.TryRowVersion`); a rule whose read
rows are unchanged keeps its closed verdict. A text cell, a removal, a draw, a
shuffle, and a random or slice transfer take the same arena kernels
(`WorldArenaTransforms.TryApply`), which compose but still export once.
`puck bench world` measures the tick path on the fixture and the shipped
world.

**Resolve a document's paths beside it.** Every relative path a world document
authors (basis, imports, `references`, asset-row `source`, addon `modulePath`,
pipeline/graph `source`, probe `track`, machine content, `host.icon`, schedule
instance `document`) resolves through `WorldDocumentPaths` against
`WorldDefinition.DocumentDirectory`, the directory the loader read the document
from; engine content the build ships (default world, fonts, shaders, probe
kinds) resolves through `PuckPaths.Shipped`, and neither falls back to the
other. A door that rebuilds a definition from JSON restores the directory from
the one it rebuilt (`with { DocumentDirectory = … }`); a directory-less
document (stdin, in-memory, hosted, peer-delivered) refuses a relative asset row by name at validation, and a relative addon module
by name at mount. Composition, staging and `world.save` re-express
a merged fragment's file paths (not its document names) to the receiving
document (`WorldDocumentPaths.RelocateDocumentFields`). A fixture outside
`src/Puck.World/Assets` names shipped assets with a `../` path or keeps
fixture-only assets beside itself. Contract:
[the worlds manual](../../../docs/architecture/worlds.md#paths-a-document-names).
**Narrate through the hub, never the console.** Server writes nothing to
`System.Console`; every line is a `WorldNarration` through
`WorldOutputHub.Narrate(channel, text)` under `HasNarrationSink` (a lambda that
captures locals allocates its closure on every call, sink or none), and a
composition root binds `Puck.World.Console`'s `WorldConsoleNarrationSink` to
stderr, or stdout for the few lines a script reads as answers — `Puck.World.Server`
cannot hold that type itself, since `build/Architecture.props`'s
`PuckArchitectureDeniedApi` denies it a `System.Console` reference and
`PuckArchitectureDeniedApiGate` fails the build (`PUCKARCH008`) on the compiled
output if one slips in. A fixture server narrates only if it is handed the
sink (`Fixtures.FreshServer` does), so a law that captures `Console.Error`
reads what the game prints.
A body sleeps after `bodies.sleepAfterSeconds` idle (0 never sleeps; a multiple of 1/800 s)
and wakes on intent, pose, transfer, admission, a designation, a dynamic
contact, or a contact-field version bump (`WorldBody.Sleep.cs`). A value write
delivers `DeliverState`; a shape change delivers `DeliverDefinition`; the
install marks which is pending and the step delivers once
(`WorldDocument.DeliverPending`, the only delivery door). A state delivery
carries a `WorldStateStamp` — the tick, the engine tick, and the row ordinals
whose versions moved since the last delivery, noted wherever the document and
the arena come to agree (`WorldServer.MarkPublished`) — which the client's state
mirror intersects with its bound slots.

**Add a read-back.** Do not land a new decision surface without a verb that
echoes it, in the same change — a decision nothing can echo can only be
asserted through downstream inference. `world.why`, `world.grants`,
`body.channels`, `world.hud`, `world.status`, `world.addons`,
`world.refusals`, `world.tables`, `world.rule.trace`, `world.rule.hazards`,
`world.budget.rules`, `world.binding-bar` are the pattern.

**Keep parsing strict and sweep shipped worlds.** Refuse unmapped JSON members by name on
every nested row; only the document root's `Extensions` bag round-trips
reserved-prefix (`$`/`_`) keys. Adding a top-level section refuses at boot
until every shipped world carries it; adding a nested member silently
defaults at parse and (usually) refuses at validation — sweep the shipped
worlds in the same change either way. `ShippedSourceLintLawTests`
(`tests/Puck.Cli.Tests`) runs `puck lint --strict` over every shipped `.puck`
source, so a sweep that leaves one red fails the suite. Any change to the
document model is regenerated with `puck schema`, which writes the JSON
Schemas, the dashboard portal's `worldDefinition.generated.ts` and the engine's
model shape (`WorldModelShape.generated.cs`, the table `WorldCallArguments` and
`WorldModuleNamespace.Visit` read instead of describing a type at run time)
together; `puck schema --check` (run in-process by `LedgerDriftTests`) fails
when any of them drifts, and none is ever hand-edited. Precise direction:
[references/documents.md](references/documents.md).

**Mint names through `GeneratedName`.** A name the engine or compiler writes
into a namespace an author also names — a row, a cell key, a rule, a group, a
placement, a prototype, an identity row, an instance, a capture, a view — is
joined by `Puck.State.GeneratedName` (`$` inside a document, `~` for a world,
instance or capture name that becomes a path), never concatenated with `_`,
`-`, `--`, `:` or `@`. Author doors refuse both characters in a document name
(`TryValidateAuthored`), the loader refuses `~` in one (`WorldAuthoredNames`), and
a document name spelled into a path goes through `GeneratedName.ToFile` (`$`→`~`,
injective because no document name holds `~`), so a generated name cannot collide
with an authored one, and `GeneratedNameLawTests` (State and World.Transpiler) hold
every minting site to the form. Presentation mints a capture as `<station>~<tick>`
(`WorldCaptureRow.CaptureName`; the validator refuses a station carrying `~`) and a
view as `session$<screen>` or `<camera>$seat$<seat>` (`WorldViewNames`; the validator
refuses a camera name in the generated form), pinned by `WorldPresentationNameLawTests`.
The decompiler inverts every minting site (`GeneratedNameReversalLawTests`): a generated name
prints back as the construct that minted it, or is refused by name, and a new minting call under `src/`
fails that law until a row reads its names back.

**Adding an authorable feature — six obligations, one change.** The contracts
above are each stated separately; a feature carrying tunable values owes all of
them together, and skipping the binding is how a bespoke mechanism gets built
beside an existing one:
1. **Search `src/Puck.World.Schema` for existing vocabulary first** — the record,
   the `$type` arm, or the section that already says this. Extending what exists
   beats a parallel mechanism, and the existing one is usually invisible from
   the call site you started at.
2. **Author the values as a document record** (never C# constants — see the
   model paragraph), with a `Default` carrying today's behavior so an
   unauthored world is unchanged.
3. **Validate** in `WorldDefinitionValidator`, refusing by name in the style of
   its neighbors.
4. **Sweep every shipped world** in the same change (strict parse, above).
5. **Add the read-back verb** (above) — the decision must be echoable.
6. **Echo the derived cost.** A feature whose declaration carries a price (a
   step clamp, an envelope reservation, a per-step loop) folds that price
   into the `world.budget` cost sheet in the same change — a derived cost
   nothing can echo is a silent frame tax.

**Doc hygiene, same commit.** [`docs/game/design.md`](../../../docs/game/design.md) is the one document that says what we are collectively building; correct it in the SAME commit as any landing that changes its truth. NEVER write a status column — a status claim duplicates what the code answers better, so record the DECISION and let the code answer "is it done". Component READMEs are developer references (no doctrine
prose); if a change stales one, or stales a comment, fix it in the same
change. A doc that would produce wrong behavior today is hostile, not stale —
delete it.

## Running and verifying

```
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds N --state-dir <tmp> < script.txt > out.log 2> err.log
```

- `--exit-after-seconds 0` (or absent) runs until the window closes. The
  full flag surface is parsed in `Program.cs` (`--backend`, `--width`,
  `--height`, `--exit-after-seconds`, `--present-mode`, `--unpaced`, `--world`,
  `--entry`, `--recording`, `--storage-uri`, `--storage-discovery-uri`,
  `--user-id`, `--state-dir`, `--headless`, `--capture-dir`, `--schedule-dir`,
  `--listen`, `--connect`, `--federation-key-file`,
  `--authentication-config-file`, `--extensions-config-file`,
  `--update-config-file`, `--debug-layers`); host-related flags are nullable
  deployment overrides. Absent host overrides leave the world document's
  `host` section in control. `--world` accepts a `.puck` path directly —
  `PuckWorldLoader` compiles it through the compile cache before boot — or an ordinary JSON
  world document; point at a worked example instead of hand-writing JSON:
  `src/Puck.World.Transpiler/Samples/*.synthetic.world.puck` (fixtures) and
  the shipped `Assets/worlds/avatars/moth.puck`, `moth-courtyard.puck`, and
  `tools/hgb-mirror.puck`/`hgb-compare.puck` (real assets; the game's build
  compiles them into its own output, `build/WorldAssets.targets`, never beside
  the source). A composition source boots too: `--world worlds/rulepush/rulepush.puck`
  stages every declared world under `<state-dir>/compositions/<stem>` through
  `WorldStaging` (the staging `puck test` uses) and boots the one declared
  `entry world`, or the declared world `--entry <name>` names (a canary leg's
  `entry` member); a composition with neither is refused by name. `host.presentation` has three values: windowed,
  `none` (`HeadlessWorldSimulation` — full authority, no GPU), and
  `offscreen` (full authority + GPU composition to images, no window —
  what `puck parity` boots; its pump steps no tick past an armed capture
  until the capture is served or refused). Tick-scheduled `captures`, the `schedule`
  section (armed only by `--schedule-dir`), verdict rows, and `.puck` `test`
  lowering are in [references/schedules-and-tests.md](references/schedules-and-tests.md).
- `--state-dir <dir>` redirects the on-disk state root (profile catalog,
  machine id, replays, pipeline caches) — use a temp dir for hermetic
  verification runs; parallel runs each need their own. Compiled worlds,
  bakes and `.puck` compiles are per-user device caches every boot shares
  (`WorldCacheRoots`). The root is a `WorldStateRoot` and the caches a
  `WorldCacheRoots`, both handed to the boot (`WorldBootInputs`) and taken by
  every consumer from its host; only `Program.cs` names their per-user
  defaults (`world`, `bakes`, `compiled-worlds`, `compilations`), and
  `WorldStateRootIsolationLawTests` holds every assembly
  `tests/Puck.World.Tests` links to that, so a fixture hands its own temporary
  roots. Stderr carries one `[world] compiled world:`
  line after the `[world] definition:` line.
- **Capture BOTH streams.** Read-back answers land on stdout; refusals,
  server narration, boot origin lines, host log lines, and `[world.mutation: …]`
  echoes land on stderr. Reading one stream is reading half the conversation.
- Blank lines and `#` comments in the piped script are skipped — annotate
  your scripts.
- **The drain barrier**: a following `Immediate` verb is held until pending
  `Simulation` traffic applies, so write-then-read pairs need no polling.
  `world.wait <ticks>` holds only its issuing session, clocked by completed
  host-work ticks; the console drains before every step, so the line after a
  wait releasing at R runs before tick R+1, and a piped script's lines up to
  its first wait run before tick 1. End a script with `quit` to stop the run when the
  script ends (see [references/console.md](references/console.md)).
- **Encoding, the two traps**: a pwsh spawned from Git Bash reads captured
  output under an OEM codepage and mangles the engine's em-dashes
  (false-FAIL); pin `[Console]::OutputEncoding` and `$OutputEncoding` to
  UTF-8 — but BOM-LESS (`[System.Text.UTF8Encoding]::new($false)`): a
  BOM'd pin writes its preamble into the piped stdin and silently corrupts
  the FIRST command.
- **Indexing**: `body.*` verbs and `world.grant body:<n>` address the 0-based
  entity index (0..4095 at the engine ceiling); seat-scoped `player.*` verbs (join/leave/assign/mode/
  bind/…) stay 1-based seat numbers. `body:1` is seat 2's entity.
- Scenery boulders HAVE collision — zero displacement with no refusal means
  the physical path, not a dead command. A zero-input boot drifts p1
  slightly (~(-0.04, 0, -0.82) over ~300 ticks) — do not assert exact rest
  poses without accounting for it.
- `world.screenshot <path.png>` REQUESTS the next composed frame including
  the overlay — the cheap pixel assertion. It arms; it does not capture:
  the stdout echo says `pending`, the file is announced on STDERR
  (`[capture] unified overlay -> …`), so **fence a frame (`world.wait`)
  before reading it**, and a second shot armed before the first composes is
  refused by name.
  The terminal console starts hidden; if a script opens its seat session
  (`console [on|off] <player>` from stdin), it may cover the frame — close it
  before judging pixels.
- **Two windowed captures are never byte-identical, even of identical
  simulation state.** Silhouette shading carries ±1-LSB variance across a
  boot-time transition, so a byte comparison of two fenced captures reports a
  difference about one run in three. The unified overlay also composites the OS
  pointer's cursor (`WorldCursorFeed`) whenever the pointer sits inside the
  window, and window placement varies per launch. Compare frames by
  CHANGED-PIXEL COUNT (`CanaryFrameNoise`, the `framesAgree` canary assertion:
  pixels moving ≥2 LSB, budget 64), never by bytes. Do NOT reach for
  `ParityEnvelope` here — its whole-frame mean guard is for diffuse
  cross-backend noise, and a body relocation covering 0.06% of the frame
  measures ~0.03 LSB mean and slips under it.
- **Search content with `puck search`.** The `content-search` skill owns it,
  including the fallback when the CLI cannot bootstrap.
- **A verification that cannot fail is a lie.** Pair every denial case with
  a control (actor holds the grant → succeeds), keep actor ≠ target (every
  seat is seeded wide, so self-targeting discriminates nothing), and prove
  a new assertion once by breaking it. This repo's recorded dominant
  failure mode is verification scripts that lie silently.
- `replay.verify` MATCH proves the explicitly hashed authoritative state-system
  trajectory, not the whole document, grant table, or HUD
  ([references/replay.md](references/replay.md)).
- Committed proofs: `puck canary` manifests under `tests/Puck.World.Canaries/`
  for every load-bearing seam, including `world.grant`-driven claims (a
  command claim's `stream` override lets an accepted outcome expect its
  confirmation on stderr, the shape server narration always uses) and
  multi-authority federation (`four-corners-sharded`: a leg's `authorities`
  array names N real listener processes, each with its own dynamic endpoint
  and generated identity, and a `line`/`response`/`sequence` assertion's
  `authority` selector reads a specific one's transcript) — re-run whichever
  proofs your change touches. The runner closes every leg's script with
  `wire.errors` and `quit`, so a leg lasts as long as its script; a manifest's
  `timeoutSeconds` is only the kill ceiling (each child World also gets
  `--exit-after-seconds` at it as a backstop), `--jobs` runs legs
  concurrently (a windowed, offscreen, or requirement-declaring leg runs
  alone), and Ctrl+C or a leg that throws kills every child the run
  started (`CanaryCommand.RunLegsConcurrently`, exit 2). `--plan` prints a
  selection's World boots, spawns, builds and leg budget without running;
  the automatic set and `--merge` are refused past their ceilings in
  `src/Puck.Cli/Canary/CanaryCeilings.cs`, which a deliberate growth raises
  in the same change. The
  acting-principal/administration and control-application authority contracts
  are proved in `tests/Puck.World.Tests` (`AuthorityAdministrationLawTests`,
  `EngageAuthorityLawTests`, `ControlApplicationLawTests`); a retired battery leaves no record directory
  behind — its history is in git, and its contract is validated by running
  the app until a law or canary owns it. Ask before creating new persisted
  runner/battery artifacts or other permanent verification infrastructure, and
  do not repair a rotted fixture — quarantine it and move on (validation currency
  is run-the-app, owner-in-the-loop). A retired runner is deleted with its
  directory, never kept alive to announce that it no longer runs.

A minimal smoke session:

```
printf 'world.status\nbody.where 0\nworld.grants console\n' |
  dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6 --state-dir "$TMP/puck-state"
```

## Where state changes — the one pipeline

All durable change flows through the mutation substrate: a `WorldMutation`
buffers through the ordered domain, drains FIFO at the tick boundary,
composes a candidate → revalidates the WHOLE document → capacity-checks →
swaps atomically and rebuilds the changed derived state → journals →
delivers to clients. `world.undo` replays journal-minus-tail
through the same gates, all-or-nothing. Rendering derives from the
delivered definition on revision moves — a mutation's visual effect is a
side effect, never a draw call. The exact `WorldServer.Step` order, the
apply pipeline, the kind catalog with its declared ordinals, and the
add-a-kind procedure: [references/mutations.md](references/mutations.md).

## Subsystem index — working on X, read references/X.md

| Working on | Read |
|---|---|
| Document schema, serialization, validators, player profiles, binding layers, capacity constants | [references/documents.md](references/documents.md) |
| The tick order, mutation kinds/ordinals, journal/undo, adding a mutation kind end to end | [references/mutations.md](references/mutations.md) |
| Grants, principals, verdicts, co-driving fold/consent, budgets, handles, refusal catalog, `world.why` | [references/authority.md](references/authority.md) |
| `SubmissionEnvelope`, the one queue, completions, echo routing, the intent buffer | [references/ordered-domain.md](references/ordered-domain.md) |
| HUD schema caps, overlay reservation arithmetic, bands/`replace`, bindings, HUD verbs | [references/hud.md](references/hud.md) |
| Camera rigs, world-owned `views.seatControl`, portable `playerDefaults.seatLook`, the seat-owned movement/render/read-back state, pointer/cursor stack, radial action menu, layouts, and `world.row.set views.*`/`view.override` verbs | [references/views.md](references/views.md) |
| Invisible reciprocal boundaries, derived overlap/corner peers, frame isometries, generation-addressed authority routes, reserve/commit handoff, action continuity, neighbour contact, seam liveness (`livenessGraceSeconds`, the `$link:` reserved rule channel, `world.links`), and the five-authority quilt | [references/adjacency-and-federation.md](references/adjacency-and-federation.md) |
| `body.engage`, control applications (the (target, kit) set a principal holds; capture as own-body membership), the kit pad map, server-internal merged pads, possession's co-drive path, machines, a screen route's pointer `input` destination and its `Passthrough` refusal | [references/engagement.md](references/engagement.md) |
| Join/leave (local seat and peer), park-with-grace, the `$parked:` reserved rule channel, body-resume's identity match rule | [references/session-lifecycle.md](references/session-lifecycle.md) |
| The replay tape: version-1 format, capture scope, population hash, verify semantics, receipts | [references/replay.md](references/replay.md) |
| Addon rows, the prepare/commit mount transaction, pump points, channels, fuel, ABI verdicts, `world.row.set addons`/`.remove` | [references/addons.md](references/addons.md) |
| Command modules, routing, the stdin barrier, output contract, verb grammar, screenshots, `world.sdf.dump` | [references/console.md](references/console.md) |
| Render validation, stamp capacity and the stamp pool, the `rigid`/`carry`/`tether` facets | [references/documents-render.md](references/documents-render.md) |
| Navigation, the tabletop primitive, records, pools, and retained turns | [references/documents-state.md](references/documents-state.md) |
| The districts, the sculpting library | [references/documents-composition.md](references/documents-composition.md) |
| Extensions, agent participants, the MCP attachment, the silo, release/qualification/rollback | [references/hosting-and-release.md](references/hosting-and-release.md) |
| `captures`, `schedule`, verdict rows, `.puck` `test` lowering, `puck test` | [references/schedules-and-tests.md](references/schedules-and-tests.md) |

Adjacent skills: `puck-dsl` for the `.puck` language core (grammar,
`let`/`template`/modules, units, the `compile`/`lint`/`format`/`lsp` verbs,
PUCKnnn diagnostics) — this skill owns only the world vocabulary's own sugar
and semantics, not the language it rides; `rendering` for the renderer and
SDF VM the frame source feeds; `gaming-bricks` for the emulators behind
engaged screens; `rom-forge` for the SM83 framework and the Tune cart;
`maths-usage` for choosing fixed-point primitives on sim value paths.

## Boundaries worth knowing

- A kit's `rigid`, `carry`, and `tether` facets hand a body to the rigid
  solver, a carrier, or a rope; their contracts and read-backs (`body.impulse`,
  `world.rigid`, `body.carry`/`body.release`, `body.attach`/`body.detach`/
  `body.reel`, `body.tether`) are in
  [references/documents-render.md](references/documents-render.md#a-kits-rigid-facet).
- The tabletop primitive (a placement's `board` facet over a `Grid` lattice,
  `$board:cellOf`/`offset`, `$upright:`, `$fact:`) is in
  [references/documents-state.md](references/documents-state.md#the-tabletop-primitive-board-facet).
- `WorldPlacementPolicy.MaxShapesPerStamp` is 367 shapes per stamp (a panelled
  shape charges 2), and the shipped world's composed boot probe must leave at
  least 4096 instances under the 65536 ceiling
  ([references/documents-render.md](references/documents-render.md#render-validation-and-stamp-capacity)).
- `WorldBodiesLimits.CapacityCeiling` is 4096 (the largest authored
  `population.capacity` the validator admits), and `WorldClient.EntityCapacity`
  is single-sourced from it (`= WorldBodiesLimits.CapacityCeiling`), so the
  validator's admitted capacity and the client's fixed per-entity view arrays
  are the same number by construction. The client reserves detailed rigs for
  the first `WorldBodiesLimits.DetailedRenderBand` (128, also
  `WorldPlacementPolicy.MaxStampRegistrations`) indices and emits later active
  bodies through the coarse crowd representation. Existing shipped worlds may
  still author 128 with seats 0–3 local and 124 simulated.
- `SdfProgramBuilder.MaxInstances = 65536` — the per-tile mask width scales
  with DECLARED instances, which is why the frame source emits active
  avatars only and the render envelope is probed at construction
  (`WorldRenderEnvelope.TryFit` is the apply-time capacity gate).
- The per-pixel soft-shadow gather addresses ≤2048 mask words (all 65536
  instance slots); beyond that the engine falls back to coarser camera-tile
  masking.
- `OffscreenRenderBudget.RegisteredViews = 64` (Puck.Abstractions.Presentation; the validator caps `cameras` by the same constant) — never register a rendered view per
  population entry.
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`:
  the document-global CPU/instance-grid ceiling. GPU cost is the author's
  frame budget, not an admission term.
- XInput caps at 4 Xbox-family pads locally; HID pads are uncapped.
- The overlay reservation (`OverlayChannelLeases`) sums every channel's worst
  case against `OverlayFrameBuilder`'s panel, element, clip and text-word
  backstops and throws at construction when a total exceeds one, so a HUD or
  binding-surface capacity bump fails
  `OverlayLeaseTableFitsBackstopsLawTests` and every boot until the leases move
  with it ([references/hud.md](references/hud.md)).
