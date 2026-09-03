---
name: puck-world
description: Guides work on Puck.World across its document and Protocol model, authoritative server simulation, composition root, mutation and authority systems, adjacency and federation, ordered submissions, HUD and views, engagement and session lifecycles, addons, replay, and console verbs. Use whenever changing or diagnosing any src/Puck.World* project, especially console verbs, mutation kinds, document sections, grants or refusals, transfers, seamless boundaries, HUD or view bindings, addon or replay behavior, and client/server seams. Also use before writing stdin-driven game verification because it defines the supported run recipes and encoding, indexing, collision, drain, screenshot, and replay-proof constraints.
---

# Puck.World: the game of many games

Keep this skill factual and procedural: record settled contracts, their exact
seams, and how to verify them. Let the user's current instruction outrank this
file. If the skill contradicts a demanded change, update it in the same change.
Treat counts, inventories, quarantine status, and other repository-state claims
as snapshots: verify them against the current tree before relying on them.

## The model in one paragraph

Treat everything as data: versioned JSON documents (`puck.world.def.v1` — the world
itself, and, seeded from it, one per owned identity) describe what runs; the
engine renders, composites,
validates, and replays them deterministically. The world is ONE bootable
experience — no sibling `--flag` modes; durable configuration is document
fields, live operation is console verbs, and there is no `PUCK_*`
configuration surface for this game. **A baked C# constant is the same
violation as a flag, and the commonest one** (owner ruling, 2026-08-03,
re-issued 2026-08-07): the discriminator is whether Nexus, Dive, Kart, and Jump
would each want the value different — sensitivities, clamps, radii, timings,
speeds, which button arms a mode. If yes, it is a document field in its FIRST
commit, never a constant to migrate later. Before writing any feature carrying
a tunable number, search `src/Puck.World.Schema` for existing vocabulary: the
2026-08-07 relapse built a bespoke mouse-orbit with hardcoded sensitivity and
pitch clamps while the camera program's `orbit`/`clampPitch` ops and the
authored `views.seatRig` already existed. Legitimate constants: capacity bounds that size memory or the
wire, representation/determinism constants, and math. The console is the
control plane:
process stdin drives verbs, stdout/stderr echo results, and the on-screen
console is only a MIRROR of that pipe — nothing that draws (including a HUD
`replace` panel taking over the whole overlay) can take the control plane
away. Verify game behavior by RUNNING the game, never by a build gate
(`CLAUDE.md` rule 3).

## The world project family

| Project | Owns | Key types |
|---|---|---|
| `src/Puck.World.Schema` | What a world IS — the document model | `WorldDefinition` + section records, `WorldDefinitionValidator`, `WorldDefinitionSerialization`; authored-to-fixed collider compilation; document-embedded wire vocabulary that keeps the `Puck.World.Protocol` namespace (`PlayerIntent`, `WorldGrant`/`WorldPrincipal`, admission entries) |
| `src/Puck.World.Protocol` | What a world SAYS — the wire/tape vocabulary | `WorldCommand`, `WorldMutation`, `SubmissionEnvelope`, `SessionRequest`, `WorldSnapshot`, `IServerLink`/`IClientSink`/`IWorldServerHost`, `LoopbackTransport`, `WorldAuthorityEndpoint`/`WorldSessionMirror`, and the `IWorldAdjacencySource` family (`WorldAdjacencyFramePair`/`WorldAdjacencyProjection`/`IWorldAdjacencyNeighbour`) — all four namespaced `Puck.World.Server` still, moved here as files without a rename |
| `src/Puck.Networking` | The dialect-agnostic wire substrate | `FrameCodec` (the socketless frame grammar), `WireReader`/`WireWriter`, `WireRefusal`/`WireFailure` |
| `src/Puck.World.Server` | The authoritative sim | `WorldServer` (the tick, the journal), `WorldGrants`, `WorldHandleTable`, `WorldPopulation`/`WorldBody`, World-specific contact orchestration and policy, `WorldEngagement`, `WorldMachineHost`, `IWorldAddonHost`/`WorldAddonReceipt` (the addon seam interface), `WorldOwnedWorlds` (the owned-world identity catalog), `WorldReplayTape`, `WorldOutputHub` |
| `src/Puck.World.Console` | The server-only console command modules, moved out of `Puck.World` | `IWorldConsoleAuthority` (resolves the addressed `WorldInstance`), `WorldGrantCommandModule`, `WorldGroupCommandModule`, `WorldLookCommandModule`, `WorldMarketCommandModule`, `WorldNetworkCommandModule`, `WorldRowCommandModule`, `WorldStateCommandModule`, `WorldUpdateCommandModule`, `WorldWaitCommandModule` + `WorldConsoleWaitGate`/`IWorldWaitGateResolver` |
| `src/Puck.World.Addons` | The addon guest host | `WorldAddonRuntime`, `WorldAddonMutationDecoder`, `WorldAddonWire`, `AddonMutateRefusal` |
| `src/Puck.World.Client` | The presentation-facing client seam, physically split out of `Puck.World` | `PlayerRoster`/`WorldClient`/`SeatController`, the camera-program translation (`WorldCameraRigCompiler`, over the document-blind IR in `Puck.SdfVm.Views`), `WorldFramePresenter` (the composed-frame producer)/`WorldSceneEmitter`/`WorldViewComposer`, `WorldSessionSceneEmitter`/`WorldAdjacencySceneEmitter`/`WorldSdfDocumentEmitter`, the stamp/animation pool (`WorldStampPool`/`WorldPlacementStamper`/`WorldScreenStamper`), the SDF document intake (`Sdf/SdfDocumentDecoder`/`SdfDocumentModel`/`SdfRefusal`), `IWorldAudioFrameFeed`/`IWorldAudioCueSink` (the narrow seams the frame/scene producers hold the root's `WorldAudioDirector` through, the `IWorldAudioLever` pattern), and the binding-authoring layer (`WorldSeatBindings`/`WorldAffordances`/`CommandVocabulary`). References `Puck.World.Protocol` and `Puck.Audio`, never `Puck.World.Server`. |
| `src/Puck.World` | The sole composition root | `Program.cs`, `WorldClientSeats` (implements the Server seam `IWorldEmbodiedSeats`), `WorldAudioDirector` (stays here — imports `Puck.World.Audio` types directly; implements Client's `IWorldAudioFrameFeed`/`IWorldAudioCueSink`/`IWorldAudioLever` for the frame/scene producers and the session-lever sink), presentation and the screen-output binder, `Audio/` (document intake, tune hosting, the render device — the mixer core and voice synth live in `src/Puck.Audio`), the command modules that stayed here (`WorldCommandArguments`, the free-text-tail reconstruction shared with `Puck.World.Console`, lives in `Puck.World.Server` since both need it), and the shipped world/scenario documents under `Assets/` |

The agent projects are an optional extension family, not members of the base world dependency closure:

| Project | Owns | Key types |
|---|---|---|
| `src/Puck.World.AgentBridge` | The provider-neutral autonomous-participant extension | `WorldAgentBridge`, `WorldAgentMailbox`, `IWorldAgentDispatcher`, `WorldAgentObservation`, `WorldAgentAffordances`, `WorldAgentActionReceipt`; explicit opt-in composition through `AddPuckWorldAgentBridge`, bounded worker-to-pump dispatch through `ISnapshotInputCapture`, explicit-principal reads through `IPrincipalServerLink`, typed body actions, no model or Harness dependency |
| `src/Puck.World.AgentHarness` | The optional Microsoft Agent Framework adapter | `WorldAgentHarness`, `WorldAgentHarnessOptions`; constrained `puck_*` tools over the bridge, Harness approvals on mutations, caller-supplied skills and `IChatClient`, no provider credentials or lifecycle policy |

`Puck.World`, its core tests, Schema, Protocol, Server, Client, Console, and Addons must not reference either agent
project. An agent-capable composition root opts into them from above; agent lifecycle commands and an MCP adapter,
if built, also live in that extension layer.

`src/Puck.Audio` is a sibling engine-services project: the deterministic fixed-point mixer/voice-synth core
(`Puck.Audio.Mixing` — `AudioMixer`/`VoiceSynth`/
`AudioSnapshot`/`MachineAudioRate`) plus sim-state music
(`Puck.Audio.Simulation` — `MusicClock`/`MusicDirector`/`RhythmJudge`/
`MusicSenseEdge`, stepped from `WorldServer.Step` right after
`WorldEventFeed.Collect()`), referenced by `Puck.World.Server` (machine audio
rate; `WorldAssetRowLoader` resolves each `WorldMusicRow`/`WorldJudgeRow`/
`WorldTune`/`WorldPatch` reference's document off disk (`puck.music.v1`,
`puck.judge.v1`, `puck.audio.v1`, `puck.synth.v1` — the same name/source/hash
shape every world audio asset row carries), and
`MusicDirectorFactory` compiles the loaded documents into the sim-side shapes
and projects `WorldEventFeed.Edges` into `MusicSenseEdge`) and `Puck.World`
(presentation glue). It parses no document. `music.state`/`judge.state` are
`WorldAudioCommandModule` query verbs routed through seat 1's currently
claimed `WorldSeatAuthorityRouter` route — a transferred seat is followed the
same way `PlayerCommandModule`'s drive-a-player verbs are.

Dependency rules are enforced by the architecture gate (`PUCKARCH`
diagnostics from `build/Architecture.props`): `Puck.World.Schema` references
only its declared leaf/authoring closure plus `Puck.Physics`, which owns the fixed collider vocabulary —
structurally denied backends, presentation, `Puck.Overlays`, `Puck.Input`,
`Puck.World.Protocol`, and `Puck.World.Server`. `Puck.World.Protocol` adds
`Puck.World.Schema` and `Puck.Networking` (the transport-neutral frame/wire
grammar). `Puck.World.Server`
adds `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.Physics`, `Puck.Storage`,
`Puck.Hosting` — and knows nothing about rendering or input; `Puck.World.Addons` carries
`Puck.Scripting` (the addon guest ABI) and its own `AddonSimulationPump` now, referencing
`Puck.World.Server` rather than the reverse. The optional `Puck.World.AgentBridge` adds Commands, Protocol, and Schema
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

## Cross-cutting contracts (every task)

**Preserve determinism.** Use no wall clock, RNG, or float in simulation state;
use fixed point from `Puck.Maths` and exact engine-tick durations throughout; the
simulation rate is an authored per-world document field
(`WorldDefinition.Simulation.RateHz`, MUST divide `FixedTickConversion.TicksPerSecond`
50400 exactly), defaulting to 240 Hz — the fixed rate every world ran before
that field existed — for a world that authors none. Every entity is advanced on the server from
a `PlayerIntent` — poses are never accepted from outside the simulation;
drivers only produce inputs, poses flow out through the tick snapshot. The
guarantee pins the MAPPING, not the values: a deliberate correction to math
or logic is EXPECTED to change replay hashes — make the correction and
re-record any persisted tape it invalidates in the same change. Client-side
(`src/Puck.World/Client/`) is presentation: floats are fine there, nothing
feeds back into the tick.

**Navigation is authored world truth.** `navigation.domains` owns bounded
`surface`, collision-free `volume`, and live-field-constrained `medium` grids.
A `BodyTargetSource.Navigated` producer points at one domain and one ordinary
authority-checked target register. Keep A* fixed-point, budgeted, stable-tied,
checkpointed, and hashed; bake static solid clearance once, but recheck a
medium field before traversing its cached edge. Extend this vocabulary for
engine-integral movement semantics; addons/agent extensions remain the home
for arbitrary policy and planning, not collision/path correctness.

**Enforce the acting-principal rule.** Make every mutating ingress consult the ACTING
principal before any mutation. The ingress stamps identity
(`SubmissionEnvelope.Principal` on the wire, `CommandContext.Principal` for
console text); handlers READ the stamp via `context.ActingPrincipal()` and
never construct a principal — constructing one is laundering an identity.
Client code never mutates local state before the server's verdict
(completions, not discarded replies). Details:
[references/authority.md](references/authority.md).

**Add a read-back.** Do not land a new decision surface without a verb that
echoes it, in the same change — a decision nothing can echo can only be
asserted through downstream inference. `world.why`, `world.grants`,
`body.channels`, `world.hud`, `world.status`, `world.addons`,
`world.refusals`, `world.binding-bar` are the pattern.

**Keep parsing strict and sweep shipped worlds.** Refuse unmapped JSON members by name on
every nested row; only the document root's `Extensions` bag round-trips
reserved-prefix (`$`/`_`) keys. Adding a top-level section refuses at boot
until every shipped world carries it; adding a nested member silently
defaults at parse and (usually) refuses at validation — sweep the shipped
worlds in the same change either way. Precise direction:
[references/documents.md](references/documents.md).

**Adding an authorable feature — the five steps, one change.** The contracts
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

**Doc hygiene, same commit.** [`docs/campaign.md`](../../../docs/campaign.md) is the one document that says what we are collectively building; correct it in the SAME commit as any landing that changes its truth. NEVER write a status column — a status claim duplicates what the code answers better, so record the DECISION and let the code answer "is it done". Component READMEs are developer references (no doctrine
prose); if a change stales one, or stales a comment, fix it in the same
change. A doc that would produce wrong behavior today is hostile, not stale —
delete it.

## Running and verifying

```
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds N --state-dir <tmp> < script.txt > out.log 2> err.log
```

- `--exit-after-seconds 0` (or absent) runs until the window closes. The
  full flag surface is parsed in `Program.cs` (`--backend`, `--width`,
  `--height`, `--exit-after-seconds`, `--present-mode`, `--world`,
  `--recording`, `--storage-uri`, `--user-id`, `--state-dir`, `--headless`,
  `--capture-dir`, `--listen`, `--connect`); host-related flags are nullable
  deployment overrides. Absent host overrides leave the world document's
  `host` section in control. `host.presentation` has three values: windowed,
  `none` (`HeadlessWorldSimulation` — full authority, no GPU), and
  `offscreen` (full authority + GPU composition to images, no window —
  what `puck parity` boots). A world may author a `captures` section:
  tick-scheduled capture rows that arm the `world.screenshot` path at exact
  sim ticks, refuse when the camera is inside geometry
  (`map(cameraPos) <= 0`, `cameraInside=true`), stamp a per-station
  material census and a `world.state.hash`-matching state hash, and write a
  `puck.parity.manifest.v1` into `captures.directory` (overridable by
  `--capture-dir`). The camera program's `select` op dispatches to named
  sub-programs keyed on a live `state.<row>` value — the discrete sibling
  of `blend`.
- `--state-dir <dir>` redirects the on-disk state root (profile catalog,
  replays) — use a temp dir for hermetic verification runs; parallel runs
  each need their own.
- **Capture BOTH streams.** Read-back answers land on stdout; refusals,
  server narration, boot origin lines, and `[world.mutation: …]` echoes
  land on stderr. Reading one stream is reading half the conversation.
- Blank lines and `#` comments in the piped script are skipped — annotate
  your scripts.
- **The drain barrier**: a following `Immediate` verb is held until pending
  `Simulation` traffic applies, so write-then-read pairs need no polling.
  `world.wait <ticks>` is the explicit fence, clocked by completed
  simulation ticks (see [references/console.md](references/console.md)).
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
- **Use the repository's content search.** Run `puck search`, never `grep`;
  the published project tool is the repository's supported search surface.
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
  proofs your change touches. The
  acting-principal/administration and control-application authority contracts
  are proved in `tests/Puck.World.Tests` (`AuthorityAdministrationLawTests`,
  `EngageAuthorityLawTests`, `ControlApplicationLawTests`); a retired battery leaves no record directory
  behind — its history is in git, and its contract is validated by running
  the app until a law or canary owns it. Do NOT create new persisted runner/battery artifacts without asking, and do
  NOT repair a rotted fixture — quarantine it and move on (validation currency
  is run-the-app, owner-in-the-loop). A battery still worth a historical note
  keeps a README recording what it proved and why after its runner is
  deleted; once even that record adds nothing beyond git history, delete
  the directory outright — a runner kept alive only to announce it no
  longer runs is a battery-shaped file that is not a battery. Verify
  thoroughly; ask before committing new permanent verification
  infrastructure.

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
through the same gates, all-or-nothing, but refuses before crossing a market
listing, bid, buyout, cancellation, or settlement finality barrier. Rendering derives from the
delivered definition on revision moves — a mutation's visual effect is a
side effect, never a draw call. The exact `WorldServer.Step` order, the
apply pipeline, the 64-kind catalog with declared ordinals, and the
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
| `body.engage`, control applications (the (target, kit) set a principal holds; capture as own-body membership), the kit pad map, server-internal merged pads, possession's co-drive path, machines | [references/engagement.md](references/engagement.md) |
| Join/leave (local seat and peer), park-with-grace, the `$parked:` reserved rule channel, body-resume's identity match rule | [references/session-lifecycle.md](references/session-lifecycle.md) |
| The replay tape: version-1 format, capture scope, pose hash, verify semantics, receipts | [references/replay.md](references/replay.md) |
| Addon rows, the prepare/commit mount transaction, pump points, channels, fuel, ABI verdicts, `world.row.set addons`/`.remove` | [references/addons.md](references/addons.md) |
| Command modules, routing, the stdin barrier, output contract, verb grammar, screenshots | [references/console.md](references/console.md) |

Adjacent skills: `sdf-world` for the renderer and SDF VM the frame source
feeds; `gaming-bricks` for the emulators behind engaged screens;
`rom-forge` for the SM83 framework and the Tune cart; `maths-usage` for
choosing fixed-point primitives on sim value paths.

## Boundaries worth knowing

- `WorldBodiesLimits.CapacityCeiling` is 4096 (the largest authored
  `population.capacity` the validator admits), and `WorldClient.EntityCapacity`
  is SINGLE-SOURCED from it (`= WorldBodiesLimits.CapacityCeiling`, the F3
  reconciliation 2026-08-06) — so the validator's admitted capacity and the
  client's fixed per-entity view arrays are the SAME number by construction; the
  old gap where a document could author past the client bound, validate, and boot
  into an out-of-bounds throw is closed. The client reserves detailed rigs for
  the first 128 indices and emits later active bodies through the coarse crowd
  representation. Existing shipped worlds may still author 128 with seats 0–3
  local and 124 simulated.
- `SdfProgramBuilder.MaxInstances = 16384` — the per-tile mask width scales
  with DECLARED instances, which is why the frame source emits active
  avatars only and the render envelope is probed at construction
  (`WorldRenderEnvelope.TryFit` is the apply-time capacity gate).
- The per-pixel soft-shadow gather addresses ≤1024 instances; beyond that
  the engine falls back to coarser camera-tile masking.
- `OffscreenRenderBudget.RegisteredViews = 64` (Puck.Abstractions.Presentation; the validator caps `cameras` by the same constant) — never register a rendered view per
  population entry.
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`:
  the document-global CPU/instance-grid ceiling. The separately recorded
  GPU-bound measurement is `0`, but it is not the governing admission term.
- XInput caps at 4 Xbox-family pads locally; HID pads are uncapped.
- The overlay reservation's tightest resource is text (14,433/16,384 words;
  1,951 headroom). The other current totals are clips 24/32, elements
  1,542/2,048, and panels 10/16. A HUD or binding-surface capacity bump fails
  the `Puck.Overlays` build until the leases move with it
  ([references/hud.md](references/hud.md)).
