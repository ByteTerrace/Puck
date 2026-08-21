# Puck.World — the world game host

`Puck.World` is the live game: a document-driven, network-shaped local
multiplayer world of up to 128 simulated players (four local seats plus
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
`--world <path>` or the shipped `Assets/worlds/nexus.world.json`), one naming
the recording document, and one capability-disclosure line per mounted addon. The full CLI
flag surface (backend, size, world, recording, user id, present mode, listen,
connect, federation key) is declared in `Program.cs`; the graphics API is the boot-time
choice `--backend directx|vulkan` (Direct3D 12 is the Windows default),
because changing APIs rebuilds the whole render host.

**Networking.** `--listen <ip:port>` (or the document's `host.listen`) binds
the P7 socket door (`Server/WorldTcpHost`) so a remote peer can join the same
ordered domain a local script drives; `--connect <ip:port>` keeps the normal
world/presentation composition while its local seats are authorized and driven
by that remote authority. Listening and connecting remain separate boot modes.
Both are zero by default (no socket ever opens). A connection
crosses TWO doors before either side sees a submission: `WorldHelloDoor`
(protocol-version compatibility) and, once that passes, `WorldAdmissionDoor`
(`Puck.World.Schema`'s `WorldAdmissionDoor.cs`) — a challenge-response
identity check over `Puck.Attestation`'s signed attestations against the
world document's own `admission` section. A world authoring no `admission`
entries admits no remote peer at all, and no traveller from another authority
either (deny by default — a transfer's own arrival verdict comes from the same
section, through a keyless `federatedAuthority` row naming the source authority
namespace or `*`); `--connect-identity-dir <dir>`
supplies the connecting client's own identity, and omitting it signs with a
freshly minted, unregistered key so the door's refusal path is exercisable
without a pre-arranged identity. `world.peers` echoes each connection's
verified identity and mapped principal and disclosure tier, plus an `arrivals:`
group naming each transferred body and the authority its verdict was decided
against; `world.admission` echoes the document's own authored entries and each
one's `disclosure`. `world.projection` echoes what this authority would hand a
peer at each tier — the byte size and the section inventory — or at the tier a
named federation namespace resolves to.

Authority-to-authority projection and transfer additionally require
`--federation-key-file <path>` on both processes. The file contains exactly 32
raw secret bytes or 64 hexadecimal characters. A fresh challenge authenticates
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
`playerDefaults.seatLook` authors portable sensitivity/inversion/arming/rate.
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
  shape of the document section; a parse error echoes inline and submits
  nothing.

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
  binder is CORE too, since `world.faces`/`player.engage` read its bound/
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
  binding-authoring layer, frame composition (`WorldFrameSource.cs`), scene
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
  today — see `src/Puck.World.Forge/Games/README.md`), and an example `puck.sdf.v1`
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
counts, drift, and the journal length.

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
the frame server for both native GPU surfaces: BRIO's YUY2 color and L8 IR are
converted by D3D11 compute into private RGBA textures, copied into two shared
three-slot rings, and sampled directly by either renderer without host pixels.
The pair is admitted only after both GPU surfaces prove live; an adapter,
surface, shader, target, import, or copy refusal tears both facades down and
atomically restores the established CPU FaceAuth graph. A legacy face-auth provider available only
to the Windows biometric broker does not constitute a public dual-camera graph.
Alternating IR strobes the illuminator across the declared transport rate, so
half the frames arrive ambient and only the illuminated half ever publishes
(a Surface declares 60 fps IR, so 30 lit frames reach the feed); a device
that cannot stream the public pair faults both feeds by name, and an absent IR
source faults that feed loudly)
and may author `controls` (one physical camera has one state across its color
and IR streams, so the first controls-bearing camera row wins regardless of
sensor; the standard
UVC pan/tilt/zoom/exposure/focus/color surface plus the vendor-extension
`fieldOfView` in degrees and raw `vendor` selector/value rows,
`WorldCameraControls`): the values land on the physical device once its
stream is live (vendor-extension writes are firmware-ignored on an idle
filter), an `UpsertScreen` mutation (`world.row.set screens …`) moves the
device live through the ordered domain, and `screen.camera` reads each
sensor's section — negotiated extent, native transport subtype/rate,
coordinated capture mode, device range, mode, current value, the shared
authored value, and raw vendor read-backs — over the pipe (the device stays
authoritative: values clamp to its reported envelope, members never authored
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
(`advanced-gaming-brick`) onto `WorldMachineHost`, and `player.engage`
composes a control application onto the machine — the same `PlayerIntent`
currency, translated once into a neutral pad image through the named kit's
`pad` map, folded server-side (`WorldEngagement.FoldTick`) and read directly by
`WorldMachineHost.Advance` inside `WorldServer.Step`; the authority to compose
rides the grant table's `Control` capability. See [`Puck.World.Server`](../Puck.World.Server/README.md) for the full contract.

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

## Graphics options

All render levers are live verbs with no-arg echoes of the current value:
`world.quality`, `world.shadows`, `world.ao`, `world.render-scale`,
`world.upscale-sharpness`, `world.target`, `world.shadow-mask`,
`world.shadow-march`, `world.ao-quality`, `world.view-refresh`,
`world.debug-view`, `world.timing`, `world.gpu`, `world.fps`. Named tiers are
facades over continuous values. Do not assume a lower render scale is
monotonic for a large instance field — read both `world.gpu` and `world.fps`
at the intended population and view layout.

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
printf 'world.status\nplayer.where 1\nworld.grants console\n' |
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
