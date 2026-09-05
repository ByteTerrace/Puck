# Puck.World — the world game host

`Puck.World` is the live game: a document-driven, network-shaped local
multiplayer world of up to 4096 simulated bodies (four local seats plus
autonomous stand-ins), rendered through the SDF engine and scripted end to end
over its own console. This project is the composition root of a multi-project
split, and this README is the entry point — each sibling owns its own depth:

| Project | Owns |
|---|---|
| [`Puck.World.Schema`](../Puck.World.Schema/README.md) | What a world IS — the `puck.world.def.v1` document model |
| [`Puck.World.Protocol`](../Puck.World.Protocol/README.md) | What a world SAYS — the wire/tape protocol |
| [`Puck.Networking`](../Puck.Networking/README.md) | The world-agnostic wire substrate — the frame grammar and reader/writer pair |
| [`Puck.World.Server`](../Puck.World.Server/README.md) | The authoritative runtime: the tick, entity table, grants, addons, owned worlds, storage, replay |
| [`Puck.World.Client`](../Puck.World.Client/README.md) | The per-machine client half: seats, the entity view, the fly camera application, and the binding-authoring layer |
| `Puck.World` (this project) | The audio director, the frame source, presentation, console command modules, assets, and `Program.cs` |

The product intent (the overworld, the reveal ladder) lives in
[`CLAUDE.md`](../../CLAUDE.md) and [`docs/vision.md`](../../docs/vision.md);
nothing there is evidence that a capability is built. What each `Puck.*`
project is for is [`docs/project-map.md`](../../docs/project-map.md).

## Run it

```
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6
```

`--exit-after-seconds 0` (or omitting the flag) runs until the window closes.
Boot prints one line naming the world-definition file it loaded (an explicit
`--world <path>` or the shipped `Assets/worlds/puck.world.json`), one naming
the recording document, and one capability-disclosure line per mounted addon. The full CLI
flag surface (backend, size, world, recording, user id, present mode, listen,
connect, federation key) is declared in `Program.cs`; the graphics API is the boot-time
choice `--backend directx|vulkan` (Direct3D 12 is the Windows default),
because changing APIs rebuilds the whole render host.

**Networking.** `--listen <ip:port>` (or the document's `host.listen`) binds
the QUIC peer endpoint (`WorldPeerHost`) so a remote peer can join the same
ordered domain a local script drives; `--connect <ip:port>` keeps the normal
world/presentation composition while its local seats are authorized and driven
by that remote authority. Listening and connecting may coexist on the shared
`Puck.Networking.Peers.Peer`; neither is enabled by default. The networking
library owns TLS, certificate-bound identity, and bounded message delivery;
there is no TCP fallback. `PeerStream` adapts those messages to World's byte
codecs. After that peer handshake, an interactive connection crosses two
application checks: `WorldHelloDoor` (the version-1 protocol contract) and
`WorldAdmissionDoor`
(`Puck.World.Schema`'s `WorldAdmissionDoor.cs`) — a challenge-response
identity check over `Puck.Attestation`'s signed attestations against the
world document's own `admission` section. A world authoring no `admission`
entries admits no remote peer at all, and no traveller from another authority
either (deny by default — a transfer's own arrival verdict comes from the same
section, through a keyless `federatedAuthority` row naming the source authority
namespace or `*`). Transport identity alone grants no World permissions.
`world.peers` echoes each connection's
verified identity and mapped principal and disclosure tier, plus an `arrivals:`
group naming each transferred body and the authority its verdict was decided
against; `world.admission` echoes the document's own authored entries and each
one's `disclosure`. `world.projection` echoes what this authority would hand a
peer at each tier — the byte size and the section inventory — or at the tier a
named federation namespace resolves to.

Authority-to-authority projection and transfer additionally require
`--federation-key-file <path>` on both processes. The file is a DER-encoded
PKCS#8 P-256 private key with nothing after it; a key on any other curve, or
one followed by trailing bytes, is refused at boot by name. A fresh challenge authenticates
and binds every connection to its claimed source-authority namespace before any
observe, reserve, commit, status, intent, or submission operation is accepted;
omitting the key disables federation by name while leaving ordinary admitted
peer listening available. `puck canary` creates a run-scoped key for every
runner-owned authority pair.

**Headless.** `--headless` (or the document's `host.presentation: none`) boots
with no window, no GPU device, no swapchain, and no audio device — the
authoritative server, console, and tape only:

```
dotnet run --project src/Puck.World -c Release -- --headless --exit-after-seconds 6
```

`--headless` is a developer/CI reflection of `host.presentation`, never a
separate product (the unification contract): the SAME console verb surface
drives both shapes, minus whatever presentation composed. The command
VOCABULARY itself must be identical in every shape — the document validators
check a world's `bindingOverlays`
against whatever this composition registers, so a genuinely presentation-only
verb (`world.fps`/`.gpu`/`render*`/`view*`, audio, recording) refuses as
UNKNOWN over headless stdin, while `player.mode` (and the fly camera
application it can activate) is CORE-registered (nothing in its dependency
chain is GPU-typed), terminal-owned `console` is
registered beside `quit`, and `world.screenshot`/`player.wheel.*` are
CORE-registered too but resolve their presentation dependency as OPTIONAL and
refuse BY NAME at use instead of going unregistered — a headless boot that
left a stock wheel sector unregistered would refuse the SAME boot document a
windowed boot admits. `screen.*` is
registered in EVERY shape (owner ruling, 2026-08-03: the machine host is core
state, not presentation-fed): `screen.insert`/`.eject`/`.select`/`.options`/
`.link`/`.unlink` apply through the ordered domain headless exactly as
windowed, and `screen.source <index> camera|capture|desktop|view|qr` still
attempts a real device open (or, for `qr`, a real encode) and reports the
honest failure rather than refusing as unknown.
`WorldBootComposition.cs` is the split: `AddWorldAuthoritativeCore` registers
in EVERY shape, `AddWorldPresentation` only when a window is composed.

**Offscreen.** The document's `host.presentation: offscreen` boots a real GPU
device and the composed-frame render pipeline (the world render alone — no
unified overlay/console-mirror/binding-bar, no audio device, no gamepad or
pointer input) with NO window and NO swap chain ever created, so
`world.screenshot` writes real PNGs of the composed world with nothing on
screen. There is no `--offscreen` CLI flag; author it in the world document:

```json
"host": { "presentation": "offscreen", "width": 640, "height": 480 }
```

Direct3D 12 is genuinely surfaceless (the device activates on its first GPU
call, exactly like `Puck.Post`'s retired `PostDirectXDevice` — see
`experimental/Puck.Post/PostDirectXDevice.cs`); Vulkan's device bring-up in
this codebase is fused to a real native surface, so this shape stands up a
native window through the SAME path the windowed shape uses but never shows
it and never builds a swap chain against it — see
`WorldOffscreenGpuActivation`'s remarks for the exact obstacle. Diegetic
View-type screens (the jumbotron pool the windowed render-root factory stands
up via `WorldScreenBinder.ConfigureViews`) are a known gap this shape does not
compose. `WorldBootComposition.AddWorldOffscreenPresentation` and
`Puck.Launcher.OffscreenTickHostedService` (which produces one composed frame
per host-loop iteration, paced by the fixed-step pump rather than vsync) are
the seams; the server steps exactly like `host.presentation: none`
(`HeadlessWorldSimulation`).

## Seat controls and camera authoring

Nexus seats use standard third-person action semantics: left stick moves in the
live logical camera plane while preserving heading (lateral input strafes, and
holding forward while turning bends the trajectory with the view); right
stick yaw turns the upright character through `FaceX`/`FaceZ`, while both axes
orbit/look and never write `Turn`. Authors can pair `player.move` with
`player.look` for movement-facing/free-orbit alternatives, or use
`player.move.strafe` with `player.look.steer` for the standard action scheme.
Pressing the left stick toggles the `run` channel; West and Left Shift retain
hold-to-run behavior. Holding LT and pressing the left stick toggles autorun
through the `forward` channel; the chord consumes that press, so it does not
also flip the bare-stick run toggle.
Holding LT + RB temporarily makes the standard right stick camera-only free
look; left-stick movement remains relative to character heading while held.
`views.seatRig` authors framing, `views.seatControl` authors the
world's `World|Body` yaw reference and pitch envelope, and
`seatDefaults.seatCameraFeel` authors portable sensitivity/inversion/arming/rate.
`world.view.camera [player]` reads the same seat-owned state movement and both
local/travel renderers use. The old mixed seat-look shape is not accepted.

Binding contexts can also select complete control groups from gameplay state.
A context family named `state:<row>` reads that declared world-state row: a
scalar row publishes one value to every seat, while a keyed row reads the
controlled body's entity-index cell. Ordinary rule `setState`/`addState`
effects therefore switch mappings through the same validated, replayed state
pipeline as the rest of gameplay. `player.bindings` reports the published
state, matched group, and precedence winner. A missing row contributes no match,
which keeps portable profile layers usable across worlds. When the winning group
changes, held commands and chord/page latches from the old group are cleared.

## The console

The console is the control plane: process stdin in, results on stdout,
refusals and server narration on stderr, all mirrored onto the in-game panel
(the terminal's `ConsoleTape` in `Puck.Hosting`, drawn by `Puck.Overlays`'
console-panel writer). Every capability is a verb. **Type `help` for the
live, self-documenting verb list** — it is generated from the registered
commands, so this README does not catalog verbs.

Each local seat has its own text session, editor, history, tape, and allowed
command surface. Backtick is a terminal-owned, always-active binding rather
than a world-page row. Seated `console [on|off]` invocations affect their own
seat; stdin uses `console [on|off] <player>` and must name the target. Until
there is a separate operator panel, stdin exchanges and deferred edit echoes
live on their own operator tape and are mirrored onto the displayed seat-one
tape.

Facts a script needs:

- **Routing.** A verb is either `Immediate` (answered from current state,
  never entering the simulation) or `Simulation`-routed (applied on the fixed
  tick). The stdin drain barrier holds a following `Immediate` read until the
  pending simulation traffic applies, so a scripted write-then-read pair
  (`world.row.set` then `world.status`, `player.bind` then
  `player.bindings`) needs no polling. `WorldConsoleWaitGate.cs` and
  `world.wait` are the explicit waits.
- **Ordering.** Within one stdin batch, submissions apply in FIFO order
  across kinds — a grant before a command is visible to that command. The
  contract and its battery are in
  [`Puck.World.Server`'s README](../Puck.World.Server/README.md).
- **Acks.** `wire.ack quiet` drops success acknowledgements of the
  side-effecting wire verbs (flood-friendly); errors and query answers always
  echo.
- **Refusals are loud and named.** A denied write prints a
  `[world.grant denied: …]`-shaped line and drops; `world.why` and
  `world.refusals` are the read-backs.
- Row-valued mutation verbs take one inline-JSON argument in the exact wire
  shape of the document section; a malformed row echoes its error and mutates
  nothing. These verbs are Simulation-routed, so the line is not parsed at
  submit: the row's own error echoes when its tick applies, and a line whose
  SHAPE the parser refuses is a deferred refusal, counted into `wire.errors` at
  that tick rather than answered by the submitting call.

## What lives here

- `Program.cs` — the composition root: resolves the boot shape BEFORE any
  registration, then calls `WorldBootComposition.AddWorldAuthoritativeCore`
  always and `AddWorldPresentation` only when windowed;
  `WorldPostBuildWiring.Install` wires the affordance vocabulary, RE-VALIDATES
  the boot document's binding vocabulary now that the registry is real (the
  FIRST validation, at `WorldDefinitionLoader.TryResolve` above, ran before
  the registry existed, so its command half was a documented no-op in EVERY
  boot shape — see `WorldPostBuildWiring.Install`'s remarks), the session-lever
  sink, and the server's echo/cue taps once, after the container builds, in
  EITHER shape. A refused re-validation prints its reason and fails the boot
  (`Install` returns `false`) before `Program.cs` ever calls `IHost.RunAsync`.
- `WorldBootComposition.cs` — the two composition methods (above): everything
  server-safe (profiles, roster, server, grants, addons, replay tape, the
  console's tick barrier, `WorldMachineHost` and `WorldScreenBinder` — the
  machine host is core state that boots and steps in every shape, and the
  binder is CORE too, since `world.faces`/`body.engage` read its bound/
  no-signal state even headless — every server-safe command module including
  `ScreenCommandModule`, and the camera control application (the `player.mode`
  and `player.camera` verbs) — for command-vocabulary parity: a world's binding document commits
  that vocabulary in every boot shape, and the validator checks it against what
  the shape registers) vs. everything genuinely presentation-only (the GPU host,
  render root, overlays, the audio device, gamepads). `WorldUiCommandModule`
  and `WorldWheelCommandModule` are CORE-registered too but resolve their one
  presentation dependency as OPTIONAL and refuse BY NAME at use headless.
- The windowed and headless host blocks are OS-branched calls into the
  launcher/platform family rather than a single consolidated host file:
  `WorldBootComposition.AddWorldPresentation` calls
  `Puck.Launcher.Windows.AddWindowsHostedPresentation`/
  `Puck.Launcher.Linux.AddLinuxHostedPresentation` (windowing, allocator, the
  selected backend) around its own `AddLauncherTerminal`/`AddBackendSwitcher`
  calls; `Program.cs`'s headless branch calls
  `Puck.Launcher.AddLauncherHeadlessTerminal` plus, on Windows, a standalone
  `Puck.Platform.Windows.AddWindowsPrecisionWaiter`. The two boot shapes are
  never composed together.
- `WorldSimulation.cs` / `HeadlessWorldSimulation.cs` — the two boot shapes'
  `IFixedStepSimulation`s. Windowed: per exact tick, the client submits seat
  intents, the shared server-step shell runs, then the client post-step
  (screens). Headless: the shared server-step
  shell alone — no `WorldClient`, no screens.
  `Puck.World.Server.WorldServerStepShell.Step` (not in this project) is the
  shared step both wrap: `WorldServer.Step`, then the replay tape's `NoteTick`
  and a caller-supplied `Action<ulong>` — here, the console wait gate's
  `PublishTick` — one shared step so a boot-shape swap can never fork tape/
  wait-gate semantics. `Puck.Launcher.FixedStepPump` (not in this project)
  owns the accumulator both boot shapes' hosted services drive it through.
- `WorldInstanceHost.cs` / `WorldInstance.cs` — the process's running world
  instances. The boot world is one entry (name `boot`) beside every instance
  `world.instance.start` adds; each non-boot instance holds its own
  `WorldServer`/`WorldPopulation`/`WorldOwnedWorlds` and an empty
  `WorldMachineHost`, shares no singleton with the boot world, and advances on
  its OWN authored `simulation.rateHz` (never a shared build-wide rate) inside
  the SAME `IFixedStepSimulation.Step` call (never a second pump) — a
  per-instance accumulator banks the host's master timeline (the boot world's
  own rate-derived cadence) and steps once per crossing of that instance's own
  step width. A live `world.rate pause`/`resume` lever holds/releases the
  accumulator without touching the authored rate; a rate of 0 is the durable
  stop (never divided by, the instance stays resident and readable, and a
  buffered document mutation still applies through `WorldServer.DrainAdministrative`
  rather than self-locking). The machine host being empty is why the start echo counts a
  document's machine-sourced screens: they start dark, and that has to be read
  back rather than inferred. An instance name is also the DIRECTORY SEGMENT its
  owned worlds live in, so `TryStart` refuses a name that is not one safe
  segment and independently refuses any name resolving its store outside the
  instances root. A non-boot instance now has its own local-seat table too
  (`world.instance.seat.*` — enter/warp/face/run/stop/where/leave, applying
  through that instance's own `ApplySession`/`ApplyCommand`, with
  `world.instance.seats` the occupancy read-back and `ReapIfEmpty` retiring an
  instance whose last seat just left); only the CLIENT, the tape, and the
  socket door still address the boot instance exclusively — that remaining
  asymmetry is wiring, not kind, and `WorldInstanceHost`'s remarks carry what a
  real flattening needs.
- `*CommandModule.cs` — the console verb modules, one per family (player,
  world, population, mutation, grants, bindings, profile, screens, collision,
  looks, placements, network, refusals, audio, HUD, state,
  storage, recording, replay, views, waits, sdf, ui, instances, chat). Modules compose against
  the protocol link, so a scripted line drives the same wire a UI would.
  `WorldPopulationCommandModule.cs` (world.players/.devices/.population),
  `ScreenCommandModule.cs` (`screen.*` — the machine host is core state that
  boots and steps in every shape, so its whole verb surface is registered
  there too: `screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink`
  apply through the ordered domain headless exactly as windowed, and
  `screen.source <index> camera|capture|desktop|view|qr` still attempts a real
  device open (or, for `qr`, a real encode) and reports the honest failure
  rather than refusing as unknown), and
  most others are server-safe (registered in `AddWorldAuthoritativeCore`,
  the fly camera application included — see above); `WorldCommandModule.cs`
  (the graphics/GPU levers), `WorldHostCommandModule.cs`,
  `WorldViewCommandModule.cs`, `WorldAudioCommandModule.cs`,
  `WorldRecordingCommandModule.cs`, and `WorldSdfCommandModule.cs` are
  genuinely presentation-only (unregistered headless); `WorldUiCommandModule.cs`
  and `WorldWheelCommandModule.cs` are core-registered but refuse by name at
  use when their presentation dependency is absent. Terminal-owned `console`
  is registered in every boot shape; `player.wheel.*` are the rows a world's
  own wheel-hold page binds.
- `WorldDefinitionLoader.cs` — resolves and validates the boot world
  document; `RecordingDocumentSource.cs` does the same for
  `puck.recording.v1`.
- [`Puck.World.Client`](../Puck.World.Client/README.md) — the per-machine
  client half: seats and device intents, the snapshot-fed entity view and
  render interpolation, the fly camera application, the
  binding-authoring layer, frame composition (`WorldFramePresenter.cs`), scene
  emission (`WorldSceneEmitter.cs`), and offscreen view composition
  (`WorldViewComposer.cs`) — the three read the root's `WorldAudioDirector`
  only through `IWorldAudioFrameFeed`/`IWorldAudioCueSink`, never the concrete
  type.
- `WorldAudioDirector.cs` — derives the emitter table from the delivered
  definition with stable ids, resolves emitter poses per produced frame, and
  publishes `AudioSnapshot`s to the mixer (see
  [`Audio/README.md`](Audio/README.md)). Stays here rather than in
  `Puck.World.Client` because it imports `Puck.World.Audio` types directly;
  the composition root passes it to `Puck.World.Client` types through the two
  narrow interfaces above.
- [`Audio/`](Audio/README.md) — the deterministic mixer core, synth voices,
  and the WASAPI output device.
- `WorldScreenBinder.cs` and `ScreenCommandModule.cs` — the diegetic screens
  (below).
- `WorldRenderProbe.cs` — the probed capacity envelope live placement is
  validated against.
- `WorldRecordingCommandModule.cs` — the recording-session command surface;
  generic frame capture lives in Hosting and is driven by Launcher.
- `Assets/` — the checked-in worlds (`worlds/*.world.json`, count them there
  rather than here: `default` — a partial `basis` template carrying the one
  shipped binding document (movement and roster groups, and
  the `contexts` rows that map roster states to them); the engine
  itself ships no bindings, so a world names this basis, authors its own, or
  has none — `play` — the hub and boot default — `dive`, `kart`, `jump`
  — the four-world charter's whole game roster, 2026-08-06 — plus `studio`, a
  non-game dev canvas for character work reached only with `--world`: neutral
  floor, no scenery or crowd, four anchored camera eyes and a `sheet` layout
  composing front/three-quarter/side/back at once; `null` — the standard
  control scheme's proving ground — a `basis` delta over `standard`, the
  partial template carrying the shared `channels`, `bodyMotionPrograms`,
  `kits`, and `bindingOverlays` any world may layer over; and the `quilt-*` documents,
  test content for adjacency and corner-crossing work, outside the
  charter roster and not game worlds — each a `basis` delta over the
  `quilt-base` template, the worked example of document composition; see
  `src/Puck.World.Schema/README.md`, "Document composition"), the default recording document
  (`recordings/`), two shipped
  WASM addons (`addons/`: `default`, `hudbuilder`; mounted by no shipped world
  today — the `arcade` addon was ported to a world `rules` section and its
  compiled guest deleted before the addons themselves went unmounted), a
  hand-authored SM83 cartridge ROM (`roms/`: `arcade-quest.gbc`, also unhosted
  today — see `src/Puck.HumbleGamingBrick.Forge/Games/README.md`), and an example `puck.sdf.v1`
  document (`sdf/`).

## The world as data

Everything durable is a document field; everything live is a console verb;
there is no `PUCK_*` configuration surface for this game. The document
families, their serialization contract, and the strict-parse rules are
[`Puck.World.Schema`](../Puck.World.Schema/README.md)'s to describe. Live editing
is one mutation vocabulary — validate the whole candidate, apply at the tick
boundary, journal, `world.undo` by replay — owned by
[`Puck.World.Server`](../Puck.World.Server/README.md). `world.save` writes a
canonical session snapshot (live render levers, census, and runtime screen
inserts fold into their document homes; every advancing `state` row/cell
settles to its live value with its projected epoch reset to 0, so a reload
resumes exactly where the save observed it instead of reading frozen — the
live document itself is never touched), and `world.status` reports source,
counts, drift, and the journal length. `world.imports` reads back the whole
basis-and-imports composition graph in merge order (see
[`Puck.World.Schema`](../Puck.World.Schema/README.md)'s document-composition
section).

The root `state` section is the one authoring inventory for every ownership
mode: `world` rows are document cells, `body` rows are ephemeral per-body
counters/timers, and `identity` rows use the durable identity seam. Body and
identity declarations compile into fixed ordinal arrays per body; actions only
reference those names and carry no nested state declarations.

Solid creation placements use the renderer's canonical shape emission under
both contact providers. `world.contacts` reports the analytic collider census
and its placement-derived share; `world.collision.status` also reports the
compact field placement-shape count and the analytic placement-collider
ceiling. Each kit independently authors `bodyContact: "Overlap" | "Solid"`
(default `Overlap`). Two dynamic bodies depenetrate only when both choose
`Solid`; collision geometry, observation, targeting, and interactions do not
silently change with that choice. The deterministic sweep-and-prune
broadphase's potential, narrowphase, and resolved pair counts are included in
`world.contacts` so crowd cost and behaviour are observable on the real path.
The fixed collider vocabulary and generic contact geometry live in
`Puck.Physics`; World retains document compilation, authority, pair selection,
grounding/walkability, obstruction reporting, and body-state writes.

A kit carrying a `rigid` facet is a passive physical entity — a billiard ball,
a bowling pin, a domino — advanced by the rigid solver instead of a
locomotion program: gravity, restitution/friction/rolling against the world,
real angular velocity, and momentum transfer against another rigid body or a
kinematic character (which contributes its own velocity but is never itself
pushed). Every contact anchor is the struck shape's own true witness point
(never a point on the conservative bounding sphere), and a grounded box or
capsule resolves over its own support manifold rather than one witness
point, so an upright piece stands on physical coefficients alone; a struck
rigid pair's own impulse can cross more than one pair-hop within the same
tick, so a rack break or a falling line of dominoes spreads immediately
rather than one body-hop per tick. `body.impulse <x> <y> <z> [body]` applies
an instantaneous world-space impulse
and wakes a resting body; `world.rigid` reads the live census (mass,
velocity, resting) and the same `quiescent` flag the `$physics:quiescent` rule
operand reads. The shipped world's `billiardsTray`/`bowlingLane`/`dominoes`
placements are the garden's proof fixture — see the
[server reference](../Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs)
for the mechanics and the [schema reference](../Puck.World.Schema/README.md#rigid-dynamics-worldrigidcs)
for the authored facet.

A kit carrying a `carry` facet may pick up another kit's rigid body:
`body.carry <carrier> <target>` begins it (within an authored reach and mass
ceiling, both scaling with the carrier's own live `Scale`), `body.release
[carrier]` ends it; a carried body's own rigid integration is suspended, but
its pose is not an unconditional follow — its own collider sweeps against
static geometry and every other active body every tick, so it pushes and is
blocked rather than passing through, and whatever correction that sweep
applies is handed back to the carrier too, so the carrier itself is stopped
by what it is holding. `body.release` refuses by name when the carried
body's current pose still overlaps geometry or another body. A released body
re-enters the solver with the carrier's own velocity. `body.where` echoes
`carrying=`/`carriedBy=` while the relationship holds. The garden's `walker`
kit (Wren) is the worked example — see the [server reference](../Puck.World.Server/README.md#carry-as-attachment-worldbodycarrycs-worldpopulationcarrycs).

The `tabletop` placement carries a `board` facet (the tabletop primitive —
see the [schema reference](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns))
anchoring an 8x8 `chessBoard` Grid topology, and 32 `piece`-kit rigid bodies
(the garden's chess set, shrunk-Wren scale beside the `drinkMe`/`eatMe`
regions, now sited clear of the table's own footprint so the shrink and the
approach never jostle a resting piece) prove it: two `pieceCode`-forEach
rules (upright/tilted, gated on the `$upright:each` reserved channel so a
knocked-over piece reads as displaced) derive each piece's live cell, then
one PER-PIECE `board`-write rule (never a single rule spanning every piece)
commits that piece's own code at its own cell — a piece whose body has left
the frame (captured, knocked clear) refuses only its own write; it never
costs its neighbours theirs, since a rule's own contiguous run of state
effects preflights and applies as one atomic candidate. A `body.pose`
teleport clears the piece's rest latch (`WorldBody.Pose`), so a bare pose at
the piece's resting height already un-rests it and re-settles it, crossing
`$physics:quiescent`'s Edge on the settle with no impulse at all; a pose
that drops the piece onto or above its support does the same through a real
fall. Pair the pose with a `body.impulse` wake only when the proof wants the
unsettled window to be impulse-shaped. Wake a piece along Y: the `piece` kit's own high
rolling/Coulomb friction couples a horizontal wake into spin (the ball's
known rolling-friction overshoot, here on a lighter body), so the unsettled
window can run long enough to drift the piece across a cell boundary before
it rests; a small vertical impulse re-settles in place instead.
`$physics:quiescent` is POPULATION-WIDE — any other rigid body still
settling (a just-released `carry` target tumbling, say) holds every board's
own Edge-gated rules at bay too, so a board proof run alongside unrelated
rigid activity can see two moves land on the same settle.

Legality is authorable, and the shipped garden's default is everything short
of adjudication: movement geometry for all six piece kinds, captures, check,
castling, en passant, and promotion. A settle's mover is found by reading
each side's occupancy as a `$board:mask` bitboard rather than comparing any
one piece's own cell: `popCount`/`lowestSetBit` size and locate the XOR of
two settles, and the shape of that delta (one square vacated and occupied, a
second side's square vacated too, two-and-two on one side) sorts it into a
quiet move, a capture, an en passant, a castle, or a perturbation — recorded
into `move` (`from`, `to`, `mover`, `captured`, `kind`) once per settle. A
capture whose defending vacate lands anywhere but the destination reads as
en passant only when that square is also adjacent to the destination behind
the mover's approach and the landed piece carries the pawn code; either test
failing reads as a perturbation, not a forged capture — an unrelated
other-side piece leaving the board in the same settle as an ordinary quiet
move must never mint a bogus en passant record. A pawn's own diagonal-move
legality then requires the settle's classified `kind` to match which capture
it claims (2 for an ordinary capture, 3 for en passant) rather than trusting
the target-square geometry alone — a diagonal hop onto the en passant target
with the passed pawn left standing classifies as a quiet move (kind 1, since
nothing was actually removed) and is refused. A settle whose own side only
occupies or only vacates clamps its empty half to `-1` rather than writing
the mask's own bit width into a row that refuses it. Movement legality and
check read outward from the move's own squares: a slider's reach is "walking
`$match:<emptyRun>:…:cell` from the destination back toward the origin lands on the
origin" (no coordinate arithmetic), a leaper's is `$board:offset` matching
the destination over its fixed jumps, and a king's square is attacked
exactly the same way, probed for an enemy piece. A
king-shaped (two-and-two) settle is judged by the castle legality check
alone, never the single-step king check beside it — the classifier's own
`from` is always the king's own home cell, read directly off the pre-move
board's king mask rather than sorted from the two vacated cells, so it can
never coincide with a single king step on either side of the board. Castling
rights are a one-way bitfield set the instant a king or rook home cell is
vacated by any settle, legal or not — a capacity-bounded history ring
answering "has this piece ever moved" can forget a departure once it scrolls
out, reviving a right a long game never regains; a castle event vacates the
king's own home cell, so it burns that king's bit on both sides at once. The
legality check itself re-reads the physical squares: the king/rook homes
held the right pieces immediately before this settle and hold neither
immediately after, the transit squares were empty, and the rook's own
landing square now holds it. Two checks close the remaining gap that leaves
open: the king must not already be in check on the position before this
settle — read from a one-tick-old snapshot of the check verdict, since the
board a rule reads mid-settle already reflects the physical move — and the
square the king crosses must not be attacked either, via a
`$board:attacks:<row>:<min>:<max>:<directions>` query that walks a short
authored ray list from a fixed cell and reports whether the first occupied
cell on any of them falls in an authored value range (a slider's reach at
one square, one query call instead of one rule per direction); king and
knight adjacency stay the cheap `$board:neighbour`/`$board:offset`
composition, since neither depends on which color is attacking. A piece that resolves to no cell,
before or after (captured, lifted off, knocked clear), never itself
qualifies as the mover, so its disappearance never registers a verdict, a
turn change, or a `lastLegal` write under its own color. Illegal moves are
recorded — `illegalCount` counts them, `verdict` names the last ruling — and
never rejected, undone, or repositioned. Promotion is a same-cell piece
swap the mask classifier cannot see as a move at all (an occupied cell whose
value changes stays out of both sides' vacated/occupied masks): reaching the
last rank on an ordinary settle marks `promotionPending`, and it stays
pending across the pawn being lifted off — an empty cell settles nothing,
since it is not a promoted piece either — until a later settle where that
cell holds an actual piece of the mover's own color other than a pawn,
which clears it directly. A hand promotion is ordinarily two settles (lift,
then place); neither one needs to itself be a legal move for the clearing to
land correctly.
`world.tabletop` reads the frame, live occupancy, and the bound convenience
rows back. `boardSquareLight`/`boardSquareDark` placements (paired one to a
cell, colors from the `boardColors` text row) render the board itself; the
`plan` row is echoed and console-writable but unrendered — reserved for an
addon to paint move highlights, per the lane's own scope. One known limit
carries from the per-body scale primitive's own contract: two pieces landing
on one cell in the same settle (an ordinary capture, an en passant) need the
captured piece physically removed in that SAME settle — the world never
depicts two bodies resting on one cell, so a capture that leaves the
defender's body sitting on the destination square reads as the defender's
own code winning the write, not the capturing piece's.

The garden also carries a hidden-hand poker table, state only — no card
bodies — beside the chess set: a `cards` token domain (52 identities, `rank`
and `suit` attribute rows, each declaring a `keysOf: cards` domain so a hidden
card's value inherits its owning zone's own visibility — see below) with a
`deck`/`hand1`/`hand2`/`community` zone family, a `cardStream` streamDraw
site, a plain int `pokerTurn` (0 = deal, 1 = bet — not the `phase` trait; a
guarded row costs the same per-tick budget as any other transform-touched
row, and one flag needs none of it),
a `bettor` turn-alternation row over `seat1`/`seat2`, a `bets` history ring,
and a `pot`. `poker-deal` draws each card at random off the deck (`Transfer`'s
own `Random` selector, three calls off the one streamDraw site) and sets
`pokerTurn = 1`, gated on `pokerTurn == 0` — a gate that, once closed, never
reopens, because nothing in the document ever sets `pokerTurn` back to 0: the
table plays exactly one hand per boot, deliberately (see the budget remarks
below), and a second `dealRequest` is refused outright rather than partially
applying (each of its three transfers would individually refuse against an
already-full destination zone, which is why the gate is the one guard, not a
per-transfer retry). `poker-derive-from-hand1`/`-hand2`/`-community`
(`forEach`) copy each dealt card's rank into `combinedByRank1`/`2`; `poker-sort`
then sorts each into ascending order (needed because the shipped
`hasTripAny`/`hasQuadAny`/`straightAny` patterns are adjacency-based and read
wrong off an unsorted deal), and `poker-strength1`/`poker-strength2` — declared
*after* the sort but *before* the reset that would otherwise close their own
`derivePending` gate first — fold the sorted words through the shipped
`pairAny` pattern into `strength1`/`strength2`, a genuine rule-derived value,
not a console fixture. `pairAtRank2..14`, `hasTripAny`, `hasQuadAny`,
`straightAny`, and `suitAtLeast5_0..3` all remain shipped, compiled, and
correct against a sorted or order-independent word respectively, reachable via
`world.match`, but only `pairAny` feeds `strength1`/`strength2` live:
`WorldRuleWorkBudget.TransformCost` prices every `transformState`
effect — a `sortKeyed`, a `transfer`, a `setRay` alike — against the WHOLE
document's declared cell storage (`suit` and `rank`'s privacy-required
`keysOf` domain declares no capacity of its own, so each still adds a full
4096-cell share to that storage),
so the deal's three transfers plus the two sorts are a real, non-trivial cost
alongside chess's and the rigid facets' own rules — consult `world.budget`
for the live per-tick tally rather than a fraction quoted here. Trip/quad/
straight/flush reads, a second per-seat suit union, and a full house/two-pair
tally would each add their own full-document-priced transform on top of the
sort, so they stay proven correct as authored patterns instead. `poker-bet-action-seat1`/`-seat2` fold a
console-set `betAction1`/
`betAction2` (0 = check, 1 = raise) into `bets`/`pot`, each gated on
`pokerTurn == 1` and on `bettor` naming its own seat, flipping `bettor` to the
other seat on success — a real turn order, not a free-for-all. Hidden cards
are placeholders through `rank`/`suit`'s own public, `Hidden: Placeholder`
visibility: each cell resolves through its OWNING zone's own visibility
(`WorldStateDisclosure.Observer.CanRead`'s nested zones-by-domain lookup,
which is *why* the `keysOf` domain cannot be dropped to save budget on either row) —
`deck` is authority-only and `hand1`/`hand2` are each their own seat until a
rule (`poker-showdown-reveal`) writes the other seat's token into
`audience1`/`audience2`, the same `readersFrom` widening the tabletop's own
`plan` row reserves for an addon. `hand1`/`hand2` themselves stay each seat's
own direct, whole-row read of its own two cards (`WorldStateVisibility` is
all-or-nothing at ROW scope, never partial — see the [Schema
reference](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)),
which is why a hand never authors its own `Placeholder` policy. `world.observe
<principal>` (see the [console
reference](../../.claude/skills/puck-world/references/console.md)) is the
read-back that lets one session inspect both seats' disclosures without
submitting as either.

World-space creation text uses the document's optional `text` catalog. Every
font row has a stable name, a path relative to the world document, a
`sha256-64/...` content pin, explicit Unicode scalar ranges, and an optional
zero-based `faceIndex` for TTC/OTC collections. `Puck.Text` loads OpenType bytes
carrying TrueType quadratic or CFF/CFF2 cubic outlines and generates the SDF atlas
in process; all
declared fonts are packed into the renderer's single glyph texture. A
`textRuns[]` row selects a font by name with `font`, or uses `defaultFont` when
it omits one. There is no absolute-path or ambient system-font fallback.
`world.text [font]` reads the active catalog. Catalog changes are definition
topology, so use `world.load`/`world.reload`; both preflight the asset path,
content pin, rasterization, packing, and every creation run's glyph coverage
before submitting the rebuild.

Creation text emits as real emboss/engrave SDF geometry on every placement
shape: a static placement stamps it into the static program, and an animated,
inhabited, or attached placement carries it through the replay stamp pool — the
run rides the registration's root transform, so lettering follows the body or
the placed root while timeline frames move the shapes. A `textRuns[]` row may
also author `maxWidth` (greedy glyph-level wrapping), `align`
(`left`/`center`/`right` against the block's widest line), `tracking` (em), and
`lineSpacing` (a line-height multiplier). Layout is Unicode-scalar based;
complex shaping and bidirectional script handling are not yet part of the World
contract.

Dense reading text authors as a screen instead: either a `screens[]` row or a
placement's creation-face override may use `{ "$type": "text", "lines": [...],
"font": ..., "columns": ..., "rows": ..., "foreground": "#RRGGBB",
"background": "#RRGGBB" }` (either color may instead bind to a text state cell,
`state.<row>.<key>`). It renders through the engine's per-cell
glyph-decal tier off the same packed font atlas — a fixed monospace cell grid
(capped by the engine's per-screen decal cell budget), sampled at shade time
with no per-glyph geometry cost, bypassing the CRT image pipeline. Signs,
plaques, and monitors belong on this tier; short sculptural lettering stays a
text run.

Federated adjacency and remote-session projection currently deliver a neighbour's
document but not its pinned font asset bytes. Those projections omit the remote
creation text until federation gains asset transport and multi-world atlas merging;
the locally loaded world's text remains fully rendered.

## The diegetic screens

Three primitives, split cleanly: a **surface** (a `WorldScreen` slab in the
document), a **source** (the signal it carries — a closed `WorldScreenSource`
hierarchy: none, test pattern, booted machine, webcam, compositor capture, a
jumbotron view of this same world through a placeable `WorldCamera`, the
diegetic console, an authored QR code, a remote-session projection, or
decal-rendered reading text), and a **route** (whether a player may engage it,
and within what radius).

**A physical camera is an input device, seated like a gamepad, never named by
hardware.** Each enumerated device gets a reconnect-stable `InputDeviceId`
(`InputDeviceId.FromKey` over the platform's device id) and a roster token
(`camera<N>` by first-seen order, beside `keyboard1`/`gamepad<N>`) —
`world.devices` lists every device and the seat it drives; `player.assign
camera<N> <slot>` moves one between occupied seats, but refuses an empty target
because a passive sensor cannot create player presence. A newly seen camera
attaches to the lowest occupied, camera-less slot by default (seat 1 first); it
never creates a seat. A `camera` frame source (a `screens` row, a
probe socket, a HUD `Frame` element) names a **seat**, never a device: `{
"$type": "camera", "sensor": "Color", "seat": 2 }`; `seat` absent means the
enclosing seat scope (an identity's own HUD panel, a seat-scoped probe socket)
or seat 1 at world scope. `WorldScreenBinder` resolves `(seat, sensor)` to a
live feed every frame (`roster.TryGetSeatDevice` → the device → its sensor's
feed). Physical-device discovery runs on a worker with at most one scan in
flight; the renderer adopts completed snapshots and retires vanished devices
without waiting for discovery. Another scan becomes due two seconds after the
previous result was consumed. A seat with no camera, or whose camera lacks the
requested sensor, reports that fault through `screen.state`/`screen.camera` rather than
refusing the bind — reassigning a camera moves every consumer to the new
device with no reopen, on the next produced frame. A scan that fails (the
platform enumeration call throws) is never read as "every camera unplugged" —
the device table is left untouched and removals resume only once a scan
completes again, and the failure narrates once per failure episode on stderr
rather than on every retry.

Camera demand — which (seat, sensor) pairs need an open feed, at what profile
— is a live set, fully recomputed every produced frame from the actual
consumers: camera-bound screen slots (`ScreenSlot.CameraSeat`/
`CameraSensorKind`), retained probe sockets, and retained HUD `Frame`
elements naming a camera; the richest requested profile wins when more than
one consumer names the same pair. Nothing is declared once and remembered —
`screen.source <i> camera …` moving away from a camera, `screen.eject`, a HUD
panel losing its `Frame` element, and `player.assign` reseating a device all
resolve within the same one-publish seam: the next produced frame's demand
recompute drops what is no longer wanted and picks up what changed, closing a
device's graph once none of its feeds are demanded any more (lazily reopened
on the next demand, exactly like the first open).

**A booted MACHINE is authoritative server state, not presentation-fed**
(owner ruling, 2026-08-03). `Puck.World.Server.WorldMachineHost` owns
boot/step/cable-link/reconfigure/memory-peek for every declared screen's
machine, in every boot shape (headless included); `screen.insert`/`.eject`/
`.select`/`.options`/`.link`/`.unlink` submit a `WorldScreenOp` through the
ordered submission domain (`IServerLink.SubmitScreenOp`, CAS-pinned and
tape-covered for BOTH `Insert` and a Machine-magazine `Select` — a failed
boot is a failed op, never a disguised success) rather than calling a
presentation binder directly. `WorldScreenBinder.cs` is a PURE READER for a
machine-owning index (its framebuffer handle/light,
`IScreenMachine.PublishFrame` each produced frame — the one GPU call this
project still makes on a machine's behalf), recreates its own slot for a
screen index removed and later restored by `world.reset`/`.load` exactly as
`WorldMachineHost` does (bounded to indices the render engine's boot-frozen
provider key set already names). A recreate re-points a `ScreenSourceCell`'s
`Slot` field rather than writing a fresh delegate into the engine's
provider maps — `SdfEngineNode` copies those maps' delegates ONCE, at
construction, and never re-reads this binder's own dictionaries again, so
only the cell indirection (never a brand-new delegate) is visible to an
already-running renderer after a remove+reset. It still OWNS the genuinely presentation
sources — a test pattern, an authored QR code, the webcam, compositor
capture, or a jumbotron view — bound through `screen.source <index> <kind>`
(`camera`, `capture`, `desktop`, `view`, `qr`; it ejects a
present machine first, through the ordered domain) and `screen.eject` (which
routes to whichever half — machine or
local producer — actually holds the slot). A camera source row picks its
`sensor` (`color` default, or `infrared` — its own shared feed; two-sensor
worlds prefer the device's Windows Face Authentication Profile V2 and its
driver-declared simultaneous native format pair. On Windows, Puck first asks
the frame server for both native GPU surfaces: YUY2 (BRIO) or NV12 (Surface)
color and L8 IR are converted by D3D11 compute with the native format's declared
matrix, range, and chroma siting into private RGBA textures, copied into two
shared three-slot rings, and sampled directly by either renderer without host
pixels.
Each sampled slot stays acquired until the SDF frame-ring fence proves that GPU
submission retired, so camera and renderer cadence cannot race an overwrite;
closing a graph likewise defers the ring's destruction across those frames.
The pair is admitted only after both GPU surfaces prove live; synchronous target
provisioning failures and worker-side startup failures before every stream's
first frame both reopen the same sensor set once on the CPU-pixel graph instead
of retrying the failed GPU tier forever, and every open runs off the render
thread. A legacy face-auth provider available only
to the Windows biometric broker does not constitute a public dual-camera graph.
Alternating IR strobes the illuminator across the declared transport rate, so
half the frames arrive ambient and only the illuminated half ever publishes
(a Surface declares 60 fps IR, so 30 lit frames reach the feed); a device
that cannot stream the public pair keeps the first-bound sensor and faults the
other by name, and an absent IR source faults that feed loudly)
and may author `controls` (one physical camera device has one state across its
color and IR streams, so the first controls-bearing camera row authored for a
given SEAT wins regardless of sensor — two seats' cameras carry independent
states; the standard
UVC pan/tilt/zoom/exposure/focus/color surface plus the vendor-extension
`fieldOfView` in degrees and raw `vendor` selector/value rows,
`WorldCameraControls`): the values land on the physical device once its
stream is live (vendor-extension writes are firmware-ignored on an idle
filter), an `UpsertScreen` mutation (`world.row.set screens …`) moves the
device live through the ordered domain, and `screen.camera` lists every known
device (token, name, sensors, tier, and the seat it drives) with each live
sensor's own section — negotiated extent, native transport subtype/rate,
coordinated capture mode, device range, mode, current value, the resolved
seat's authored value, and raw vendor read-backs — over the pipe (the device
stays authoritative: values clamp to its reported envelope, members never authored
leave driver defaults untouched, and removing an applied member restores its
default). A QR is the one source with no
per-frame cost at all: `QrEncoder` (in `Puck.World.Schema`) resolves the module
grid and the binder rasterizes it ONCE at author time, then re-uploads the
unchanged buffer only after a device loss. `world.identify <screenIndex>
[ecLevel]` (`WorldIdentifyCommandModule`) is a composition over that same live
path rather than a source of its own: it mints the RUNNING world's identity —
`puck:world/<documentId>?schema=<schema>&hash=sha256-64/<hex>` — and hands it
to `WorldScreenBinder.TryQr`, so a phone pointed at a live session carries the
world's identity away. The hash is recomputed from the LIVE definition's
canonical bytes on every invocation (mutations included), never read from the
boot-time load pin, so the code never claims an identity the running world no
longer has; the echo says `hash-covers=live-definition` and carries the payload
in full. It is deterministic in the definition alone — same document, same
payload, every run. An
unbound slot gets the engine's no-signal card, and a missing device is loud
data in `world.screens`/`screen.state`, never a crash. A machine screen is
engine-neutral (`Puck.Abstractions.Machines`): `WorldBootComposition`
registers the SM83 family (`gaming-brick`) and the ARM7TDMI machine
(`advanced-gaming-brick`) onto `WorldMachineHost`, and `body.engage`
composes a control application onto the machine — the same `PlayerIntent`
currency, translated once into a neutral pad image through the named kit's
`pad` map, folded server-side (`WorldEngagement.FoldTick`) and read directly by
`WorldMachineHost.Advance` inside `WorldServer.Step`; the authority to compose
rides the grant table's `Control` capability. See [`Puck.World.Server`](../Puck.World.Server/README.md) for the full contract.

A THIRD registered engine, `tune-instrument`
(`Puck.HumbleGamingBrick.Forge.Tune.TuneInstrumentEngine`), is a diegetic, player-operated
instrument: its content is a `puck.audio.v1` document rather than a
cartridge ROM, compiled to the same jukebox cart `Audio/TuneMachineSource.cs`
plays passively and booted on a real `Puck.HumbleGamingBrick.MachineHost`, so
`body.engage` reaches it exactly like any other screen machine. While a seat
holds the application, `WorldServer.InstrumentClockBoundary` folds the
instrument's own authored tempo into the world's `MusicClock` boundary each
tick — holding the application is the whole gate; the `world.instrument-clock`
session lever (`WorldSessionLevers.InstrumentClock`) is a presentation-only
echo of that fact for a future HUD cue, never a second gate (`WorldSessionLever`'s
own remarks: a knob the simulation reads is a mutation, not a lever).
`instrument.state` reads which screen (if any) the routed seat is engaged
with, whether it carries the capability, and its tempo. See
[`Audio/README.md`](Audio/README.md) for the instrument host itself.

A placeable camera's offscreen view can also be EXPORTED — read as a GPU
texture by a consumer outside the render engine (a probe kernel, see
`## Probes` below) rather than only sampled by a jumbotron screen.
`WorldScreenBinder.TryGetViewExport`/`ReleaseViewExport` register/withdraw a
named camera's `SdfCameraView` for export, sharing the SAME persistent view a
`screen.source <index> view` binding uses (so a camera already filmed by a
jumbotron gains export at no extra render cost) and keeping an export-only
camera rendering every `ViewStack` refresh even with no screen wired to it.
Export needs the Direct3D 12 host: the exported image is opened by a Direct3D
11 `OpenSharedResource1` elsewhere in the process, which cannot open a Vulkan
host's opaque Vulkan-to-Vulkan export handle, so the Vulkan host refuses
export outright rather than producing a handle nothing downstream can read.
Day one exports exactly ONE physical image (the engine's own persistent
output, re-rendered in place every refresh) with no second buffer to rotate
into. A shared lease admits concurrent readers of the completed image and
defers the next writer until all of them retire; export submission drains the
producer queue before publishing that image as readable.

## HUD frame elements

A HUD `Frame` element (`WorldHudElementKind.Frame`) shows a live frame inside
the banded overlay — the same four-arm `WorldFrameSource` vocabulary a screen
samples (`camera`, `view`, `probe`, `capture`), never a pipeline of its own.
`WorldScreenBinder.DeclareFrameSource`/`TryAcquireFrame` take an explicit
enclosing SEAT (the owning identity panel's slot for a player-scope panel,
or seat 1 for a world-scope one) alongside the source, since a bare `camera`
source (no authored `seat`) means "this panel's own seat" — the seat argument
is what resolves that, not a value baked into the source record. They are the
registry every non-screen consumer of a `WorldFrameSource` shares: the former
opens a non-camera producer's underlying feed the first time anything asks
for it (idempotent — a view renders every `ViewStack` refresh with no wired
screen narrowing its round-robin turn, a probe reads whatever its own kernel
publishes, a capture opens through the same ladder a declared screen's capture
source uses); a `camera` source declares nothing here at all — it instead
rides `RetainFrameSource`/`ReleaseFrameSource`'s reference-counted table,
whose membership is one of the inputs `ReconcileCameraDemand`
(`WorldScreenBinder.FrameSources.cs`) recomputes camera demand from every
produced frame, alongside every camera-bound screen slot and retained probe
socket — see the seated-camera section above; the latter (`TryAcquireFrame`)
reads the current frame, render-thread-side, once per produced frame. `WorldOverlayFrameSources` (`Puck.Overlays.IOverlayFrameSources`)
is the integer-keyed adapter the compositor addresses: `WorldHudFeed.BuildElements`
resolves a `Frame` element's `Source` to a key on the structure rebuild (world
panels at seat 1, a joined seat's own panel at its own seat), and the
compositor calls `TryAcquire` each produced frame to bind the element's
overlay slot. Two elements naming an identical (source, seat) pair share one
key, one feed, and one slot — keying on the pair, not the source alone, is
what keeps two seats' otherwise-identical bare camera panels (both authoring
no `seat`) from collapsing onto the same feed. `puck.basis.frozen.json`'s
`hud.panels` ships one such panel (`portrait`) — a mirrored, rounded
picture-in-picture of the color camera — inherited by the frozen world naming
it as its `basis`.

## Native capture

The world records itself to WebM/Matroska through the recording graph in
`Puck.Recording` (`puck.recording.v1` — resolved at boot from `--recording`
or `Assets/recordings/default.recording.json`). `capture.start` arms Hosting's
generic frame-capture controller with a `RecordingSession`; Launcher supplies
the exact final root surface immediately before presentation, and the active
presenter reads GPU surfaces back to CPU pixels. The session composites
capture-only overlays that never appear in the game window. While idle the
controller performs no readback or sink work.

Playback time is WALL-CLOCK time, not engine time: the shipped document sets
`clock: "Wall"`, so blocks are stamped from QPC when the frame reaches the
sink. `Sim` stamps from the engine tick clock instead but forbids audio rows.
The two diverge under capture. GPU readback is synchronous per captured frame
and can reduce live throughput; `world.fps` exposes that impact while
`capture.status` reports frames the recording queue dropped separately.
A `Timecode` overlay reads its own clock and is not rebased, while the
container timeline is, so the burnt-in number leads playback position by the
arm-to-first-packet latency.

## Probes

A document's optional `probes` rows (`WorldProbe`, boot-authored only) each
declare a registered `puck.probe.v1` kind and plug one `WorldFrameSource` into
each of the kind's typed sockets (`inputs`, by socket name) — or, in place of
every socket at once, a recorded `puck.probe-track.v1` track — and carry the
bindings that route one of its channels to a command axis (a `probe.<name>`
source, an ordinary bindable stick-like input any binding overlay may map), a
presentation float (a `render.extensions` config field, or another probe's
kind config field — patched live into its running kernel), or the existing
camera control surface. A socket's class is `frame` (any one frame source) or
`strobePair` (a strobing infrared sensor's lit frame and the unlit frame kept
before it — bound only to a `camera` source with sensor Infrared); an
`optional` socket may be left unbound (a null input to the kernel). A socket
source is the same four-arm `WorldFrameSource` vocabulary a screen samples:
`camera` (a declared sensor and seat — `seat` absent means the enclosing
instance's own seat, the same convention a `screens` row and a HUD `Frame`
element follow; every camera socket in one probe must resolve to the same seat,
because one kernel run has one host graph; `profile` is honored while source
`controls` are refused in favor of probe control bindings or camera-screen
authoring), `view` (a named `cameras[]` row's offscreen render, exported as a
kernel-readable lease that holds the last complete image while a kernel reads),
`probe` (another declared probe's
own texture output, read back as a ring). `capture` remains part of the shared
frame-source vocabulary but is refused on a probe socket until a kernel input
host exists for it. The kind's `trigger`
socket must bind a `camera` source: kernels run on that sensor's own camera
graph, so it decides which `ICameraKernelHost` a run attaches to. A kind that
declares an `output` writes a texture each cycle, at the extent its own
`output.of` socket's source renders at; a screen shows it as a `probe` source
(`screen.source <index> probe <id>`), and another probe's `probe` socket may
read it back as an input in turn.

`WorldProbes` services every declared row from the host loop's per-frame
capture in both boot shapes (headless, every camera/view/probe socket faults
by name for want of a live feed and a parameter binding finds no composed
pass; a track-input probe and every axis binding run in full), resolving each
socket against the binder's live state and (re)attaching the kernel to the
trigger sensor's open graph whenever any socket's generation — or the output
ring's — changes; a socket whose source is not ready yet (an unpublished ring,
an unopened camera) idles the whole probe with that fault and retries every
frame until it resolves. A camera socket retains its own (seat, sensor,
profile) demand while its instance lives, then releases it on retirement, the
same ownership shape a visible HUD `Frame` element uses — no `screens` row
need ever name the sensor for a probe to read it. A probe is not a device and
never occupies a seat: an axis binding addresses its own instance's seat's
lane directly (`InputSignal.Slot`), its `probe:<seat>` device id is only the
router's held-state key, it never counts as player activity, and it loses its
carried sample whenever the terminal takes focus, exactly as a gamepad does.

A row is seat-relative when at least one of its camera sockets carries no
`seat` of its own — it is then instanced once per occupied local seat, each
instance carrying its own reading ring, kernel run, packed constants, and
bindings, and resolving its seat-less sockets against its own seat, exactly
the way the identity HUD panel is already instanced per seat. A row whose
camera sockets every one name a seat (or that has no camera sockets, or plays
back a recorded track) stays a single instance for the whole boot, exactly as
before seat-relative instancing existed. Instances follow the roster's
occupancy: a seat joining creates its row's instances on the next serviced
frame, a seat leaving retires them (ending the run, releasing the output ring,
releasing retained view exports, and releasing every binding's held router
state) with no reboot. An axis
binding declared on a seat-relative row may not author its own `seat` (refused
at document load — it always takes its instance's); one declared on a
single-instance row still authors `seat` as before (absent defaults to seat
1). A `probe`-target parameter binding or a `probe` socket naming a
seat-relative probe resolves to the enclosing instance's own seat's target
instance, or the single instance when the target is not seat-relative.
Shipped kinds are
the lit-frame blob centroid `ir-blob` (bright-mass centroid/coverage/mean
luminance of the above-threshold pixels over the FaceAuth infrared stream)
and `faerie` (relights the color frame from a light orbiting an authored
anchor, with the infrared strobe pair's lit-minus-unlit response as the
height field; see `src/Puck.Shaders/README.md`) — GPU-tier only today.

`probe.status` echoes every live instance's run state (or fault), tier, rate,
cycles/drops, latest capture age, channel values and confidence, every
binding's conditioned value and write count, and (for a camera socket) the
resolved device token (`camera<N>`, or `seat<N>-unassigned` when the socket's
seat carries no camera) — a query, always echoing even under `wire.ack quiet`.
A seat-relative row's instance is listed as `<id>@<seat>`; a single-instance
row's is listed by its bare `<id>`. `probe.record <probe>[@<seat>] <path>
<seconds>` arms a live recording of one instance's fresh readings to a
`puck.probe-track.v1` document, sampled once per host frame (an instance
faster than the host frame rate records its latest reading per frame); each
sample carries its own capture time, and playback follows those times, so
completion narrates on stderr with the sample count and the recorded cadence
replays as recorded. The `@<seat>` suffix is required to name one instance of
a seat-relative row (omitting it is refused as ambiguous, naming the live
instances) and optional on a single-instance row. The recorded document plugs
into a track-input probe in place of a live device — the hardware-free proof
leg every probe admits. `probe.set <probe>[@<seat>] <field> <value>` patches
one float config field of a declared probe's kind live — the same constants
write a `probe`-target parameter binding performs, bound only by the field's
own declared range; a parameter binding targeting the same field overwrites a
`probe.set` write on its own next changed reading. With no `@<seat>` suffix a
single-instance row's one instance is written and a seat-relative row's every
live instance is written; with a suffix, only that one instance.

`Assets/worlds/brio-probe.world.json` (basis `brio-dual.world.json`) is the
end-to-end vertical: the `ir-blob` probe over the BRIO's infrared stream,
bound to the seat's `turn` channel through an ordinary binding overlay
(`probe.head-x`), `sdf-film-grain.intensity` (a presentation parameter), and
`brightness` (a camera control). Run it windowed, in front of the camera:

```
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-probe.world.json --exit-after-seconds 16
```

`Assets/worlds/brio-faerie.world.json` (basis `brio-dual.world.json`) is the
full vertical, all three shipped kinds over one BRIO stream: the `ir-marker`
probe frames a strip of retroreflective tape on the wall as a painting quad
(eight `probe`-target parameter bindings into the `faerie` probe's
`paintingX0..paintingY3` config), the `ir-blob` probe steers the `faerie`
probe's anchor from the tracked head (two more `probe`-target bindings), and
the `faerie` probe relights the color frame and, through its optional
`painting` socket — a `view` export of the world's own `gallery` camera —
shows that camera's own feed as the framed canvas. An `axis` binding publishes
the `faerie` probe's own `portal` channel as `probe.faerie-portal`; a binding
overlay routes it onto the world's `portal` command channel, and a
`compareState($channel:1:portal, GreaterOrEqual, 1)` world rule upserts a
`faerie-glow` placement (kit `faerieKit`, a steering producer that approaches
a sensed target) once
`probe.set faerie journey 1` carries the light into the painting far enough to
cross the kind's own `portalThreshold`. Screen 0 shows the faerie probe's
output beside the raw infrared and color feeds:

```
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-faerie.world.json --exit-after-seconds 16
```

`Assets/worlds/brio-faerie-authored.world.json` (basis
`brio-faerie.world.json`) is the no-tape leg, for an author with nothing
retroreflective on the wall: it drops the `ir-marker` row (and with it, the
eight painting-corner bindings that lived on it) and pins the `faerie`
probe's `paintingX0..paintingY3` config to a hand-authored rectangle instead —
the same corners `ir-marker` would otherwise track live. The head-anchored
faerie and the `journey`/`portal` spawn are unchanged:

```
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-faerie-authored.world.json --exit-after-seconds 16
```

`Assets/worlds/brio-probe-track.world.json` (basis `brio-probe.world.json`)
swaps the `head` probe's input for the checked-in
`Assets/probes/tracks/brio-head.probe-track.json` recording — the
hardware-free leg, runnable headless on a machine with no camera:

```
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-probe-track.world.json --headless --exit-after-seconds 6
```

`Assets/worlds/brio-seats.world.json` (basis `brio-probe.world.json`) declares
two local seats to exercise seat-relative instancing directly: run it
windowed, join a second, console-only seat (`player.join 1`), and
`probe.status` shows two instances, `head@1` (the machine's own BRIO,
default-seated at boot) running and `head@2` idle with a no-camera-assigned
fault — a second local seat with no camera of its own, never a crash or a
silently-shared reading:

```
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/brio-seats.world.json --exit-after-seconds 16
```

## Graphics options

All render levers are live verbs with no-arg echoes of the current value:
`world.quality`, `world.shadows`, `world.ao`, `world.render-scale`,
`world.upscale-sharpness`, `world.target`, `world.shadow-mask`,
`world.shadow-march`, `world.ao-quality`, `world.view-refresh`,
`world.debug-view`, `world.timing`, `world.gpu`, `world.fps`. Named tiers are
facades over continuous values. Do not assume a lower render scale is
monotonic for a large instance field — read both `world.gpu` and `world.fps`
at the intended population and view layout. `world.budget` is the DERIVED cost
sheet, not a lever: the live render program's packed words/instances against
their frozen envelopes, the Lipschitz step scale and march multiplier, the far
distance with its reach multiplier, horizon-ray step tax, and far-plane fog
remnant, the field lattice program's node/cadence counts and exact
full-cell/body-slot pass costs, and the state row count —
how an authored choice's price becomes legible instead of a silent frame tax.
Navigation adds its compiled cell count, fixed A*/shared-tree workspace bytes,
authored search caps, live follower count, and this tick's expansions to that
sheet, plus the simultaneous-replan ceiling across current followers. Shared
domains contribute their aggregate per-tick budget once, not once per follower.
`world.navigation` lists each surface/volume/medium domain and
`body.targets <body>` includes the selected route's status and waypoint. Both
`world.navigation` and `world.budget` are server-safe under `--headless`;
the latter names the absent renderer while retaining all authoritative costs.

`render.farDistance` is the depth every camera march ends at (default 40 when
unauthored; 1..8192), re-read on every definition revision like the lighting
below — geometry beyond it is never marched, so an infinite ground plane shows
a horizon curve there unless `render.sky.fogDensity` absorbs it first.

Two document sections author the scene's lighting instead of a verb, re-read
on every definition revision (a live edit lands on the next frame):
`render.lighting` sets the directional sun and ambient term; `render.sky` sets
the procedural sky gradient, sun disc, star field (each star hash-dealt its own
blackbody colour and apparent luminosity; `stars.twinkle { share, depth, rate }`
scintillates a share of them on the tick clock), cloud layer (`clouds
{ coverage, softness, scale, seed, color, drift, spin, curl, shear }` — a
hashed, warped noise layer over everything above it; drift translates it,
spin turns it about the zenith, curl winds it Coriolis-fashion, shear slides
the shaping field so clouds re-form as they travel — all on the tick clock),
and distance fog.
Both are
optional, and every field within them is optional individually — absent
renders the pinned defaults unchanged. `render.cycle`
keys both over a state row: `{ "state": "timeOfDay", "keys": [ { "at": 0.25,
"lighting": {…}, "sky": {…} }, … ] }` — the row's live value (its fractional
part, so an advancing row wraps once per unit) picks the two bracketing keys
and every lighting/sky field interpolates between them; a key states only the
fields it moves, the rest hold from the previous key. The clock is simulation
state (an advancing `state` row — deterministic, replayed, settable with
`world.row.set state`); the interpolation is presentation.

## Engine boundaries worth knowing

- `SdfProgramBuilder.MaxInstances = 16384`: per-tile mask width scales with
  DECLARED instances, which is why this project emits active avatars only and
  probes capacity floors at construction.
- The per-pixel soft-shadow gather addresses ≤1024 instances; beyond that the
  engine falls back to coarser camera-tile masking.
- `OffscreenRenderBudget.RegisteredViews = 64`: do not register a rendered view per
  population entry.
- XInput caps at 4 Xbox-family pads locally; HID pads are uncapped.

## Verifying

`Puck.World` is greenfield (`CLAUDE.md` rule 3): verify by RUNNING the game
and driving stdin verbs — no gate stages, no `--validate` flags, and no
golden corpus. Byte-identity observations (the canonical save round-trip,
`git diff` on shipped worlds) are useful evidence but never acceptance
criteria for feature work (owner ruling, 2026-07-20: if a shipped world's
JSON moves as a side effect of a landing, note it and move on; goldens become
worth building when the data settles).

A typical assertion session over a pipe:

```
printf 'world.status\nbody.where 0\nworld.grants console\n' |
  dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6
```

`world.screenshot <path.png>` is the cheap pixel assertion, but it REQUESTS a
capture of the next composed frame rather than taking one — fence a frame
(`world.wait`) before reading the file. Its stdout echo says `pending <path>`
precisely because no file exists yet; the resolved path arrives on **stderr**
when the frame lands, named by whichever node in the render chain served it:
`[capture] unified overlay -> <path>`, `[capture] <shader-set id> -> <path>`
from a composed `render.extensions` pass, or `[debug] captured frame N ->
<path>` from the engine node at the bottom. A node that draws nothing this
frame forwards the request inward, so the readback always lands on the node
that actually produced the shown frame. Arming a second capture while one is still
pending is REFUSED by name — the earlier path would never be written — and a
request still outstanding when the run ends prints a `WARNING` naming it. A
scripted caller can therefore always tell "written" from "never happened".

Committed, re-runnable proofs cover most load-bearing seams as `puck canary`
manifests under `tests/Puck.World.Canaries/` — `sdf-decode-sign-refusal`
(all twelve builder-mirrored sign fields), `world-seat-binding-recompose`
(a forced seat recompose against a registered command, cleanly, with no
binding-narration), and `addon-mutation-seam` (the grant-door outcome matrix
plus a real compiled WASM guest's chained mutate and boot-anchored replay
arming — see `src/Puck.Cli/README.md`'s `puck canary` section for the
`stream` override that lets a `world.grant` claim bind its stderr-narrated
confirmation) among them. Strict-parse and mutation-all-or-nothing are
proved in-process by
`tests/Puck.World.Tests/{StrictParseLawTests,MutationAllOrNothingLawTests}.cs`,
and cited repository paths are checked by `puck doc-links`.
`four-corners-sharded` is the stronger five-authority federation proof: four
ground worlds plus the floating island, each its own real process on its own
dynamic loopback endpoint, with one human-driven body ringing all four
ground authorities purely through the router that follows a body wherever it
now lives. `ordered-domain`,
`headless-boot`, `lane-present-deletion`, `hud-document`, and
`engagement-dissolution` have no committed battery at all — validate them by
running the app. Principal/grant enforcement and engage/disengage authority
are proved by `AuthorityAdministrationLawTests`, `EngageAuthorityLawTests`, and
`ControlApplicationLawTests` in `tests/Puck.World.Tests`.

The former World proof suite (proof.cs and its standalone harnesses) was quarantined
out of the build on 2026-08-02 with the rest of `experimental/`; nothing has
taken over its coverage, and its subcommand inventory lives in git history,
not here. Do not cite it or re-derive a gate from it — the current
verification story is the paragraph above.

The [discrete state contract](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
covers tabletop/card rules and turn-based tactics. `world.state.transform`
submits a closed atomic operation; `world.state.act <phase-row> <sequence>`
adds its phase guard. `world.topologies` reads topology declarations,
`world.state` reads authority state, and `world.state.observe` requests the
calling principal's explicitly disclosed literal observations. The headless
[tabletop fixture](../../tests/Puck.World.Canaries/tabletop-state/fixture.world.json)
includes legal/blocked moves, ray flips, ordered card transfer, and replay.

[Decision policies](../Puck.World.Schema/README.md#decision-policies) let a world
rule select eligible actions by highest score or weighted chance, with authored
reconsideration, commitment, and interrupts. Inspect them with `world.decisions`;
`world.rules` identifies policy-bearing rules and `world.budget` includes their
worst-case work. The Schema reference includes a complete rule example.
