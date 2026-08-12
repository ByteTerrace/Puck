# Puck.World — the world game host

`Puck.World` is the live game: a document-driven, network-shaped local
multiplayer world of up to 128 simulated players (four local seats plus
autonomous stand-ins), rendered through the SDF engine and scripted end to end
over its own console. This project is the composition root of a three-project
split, and this README is the entry point — each sibling owns its own depth:

| Project | Owns |
|---|---|
| [`Puck.World.Data`](../Puck.World.Data/README.md) | The `puck.world.def.v1` document model and wire protocol (`Protocol/`) |
| [`Puck.World.Server`](../Puck.World.Server/README.md) | The authoritative runtime: the tick, entity table, grants, addons, owned worlds, storage, replay |
| `Puck.World` (this project) | The client, presentation, console command modules, assets, and `Program.cs` |

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
`--world <path>` or the shipped `Assets/worlds/play.world.json`), one naming
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
(`Puck.World.Data`'s `Protocol/WorldAdmissionDoor.cs`) — a challenge-response
identity check over `Puck.Carriage`'s signed-carriage envelopes against the
world document's own `admission` section. A world authoring no `admission`
entries admits no remote peer at all (deny by default); `--connect-identity-dir <dir>`
supplies the connecting client's own identity, and omitting it signs with a
freshly minted, unregistered key so the door's refusal path is exercisable
without a pre-arranged identity. `world.peers` echoes each connection's
verified identity and mapped principal; `world.admission` echoes the
document's own authored entries.

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
check a world's `bindingOverlays` (and the engine-default document's own
wheels/editor/sculpt pages, which every world compiles in unconditionally)
against whatever this composition registers, so a genuinely presentation-only
verb (`world.fps`/`.gpu`/`render*`/`view*`, audio, recording) refuses as
UNKNOWN over headless stdin, while `editor.*`/`sculpt.*` are CORE-registered
(nothing in their dependency chain is GPU-typed) and `world.console`/
`world.screenshot`/`player.wheel.*` are CORE-registered too but resolve their
presentation dependency as OPTIONAL and refuse BY NAME at use instead of going
unregistered — a headless boot that left a stock wheel sector unregistered
would refuse the SAME boot document a windowed boot admits. `screen.*` is
registered in EVERY shape (owner ruling, 2026-08-03: the machine host is core
state, not presentation-fed): `screen.insert`/`.eject`/`.select`/`.options`/
`.link`/`.unlink` apply through the ordered domain headless exactly as
windowed, and `screen.source <index> camera|capture|desktop|view|qr` still
attempts a real device open (or, for `qr`, a real encode) and reports the
honest failure rather than refusing as unknown.
`WorldBootComposition.cs` is the split: `AddWorldAuthoritativeCore` registers
in EVERY shape, `AddWorldPresentation` only when a window is composed.

## Seat controls and camera authoring

Play seats use standard dual-stick semantics: left stick moves in the logical
camera plane and right stick changes yaw/pitch; right stick never doubles as
body turn. `views.seatRig` authors framing, `views.seatControl` authors the
world's `World|Body` yaw reference and pitch envelope, and
`playerDefaults.seatLook` authors portable sensitivity/inversion/arming/rate.
`world.view.camera [player]` reads the same seat-owned state movement and both
local/travel renderers use. The old mixed seat-look shape is not accepted.

## The console

The console is the control plane: process stdin in, results on stdout,
refusals and server narration on stderr, all mirrored onto the in-game panel
(`WorldConsoleMirror.cs`). Every capability is a verb. **Type `help` for the
live, self-documenting verb list** — it is generated from the registered
commands, so this README does not catalog verbs.

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
  `ScreenCommandModule`, and the WHOLE editor/sculpt verb surface — session,
  drag, workbench, picker, targeting, and every `editor.*`/`sculpt.*` command
  module — for command-vocabulary parity: the engine-default binding document
  commits that vocabulary in every boot shape regardless of what any world
  document authors) vs. everything genuinely presentation-only (the GPU host,
  render root, overlays, the audio device, gamepads). `WorldUiCommandModule`
  and `WorldWheelCommandModule` are CORE-registered too but resolve their one
  presentation dependency as OPTIONAL and refuse BY NAME at use headless.
- `WorldHost.cs` — `AddWorldGpuHost` (windowing, allocator, the selected
  backend) and its headless twin `AddWorldHeadlessHost` (the launcher's
  headless terminal + an optional standalone precision waiter); the two are
  never called together.
- `WorldSimulation.cs` / `HeadlessWorldSimulation.cs` — the two boot shapes'
  `IFixedStepSimulation`s. Windowed: per exact tick, the client submits seat
  intents, the shared server-step shell runs, then the client post-step
  (screens, the editor's per-tick latch). Headless: the shared server-step
  shell alone — no `WorldClient`, no screens, no editor.
  `WorldServerStepShell.cs` is the shared step BOTH wrap: `WorldServer.Step`,
  then the replay tape's `NoteTick` and the console wait gate's `PublishTick`
  — one place tape/wait-gate semantics live, so a boot-shape swap can never
  fork them. `Puck.Launcher.FixedStepPump` (not in this project) owns the
  accumulator both boot shapes' hosted services drive it through.
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
  looks, placements, network, refusals, editor, sculpt, audio, HUD, state,
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
  the editor/sculpt modules included — see above); `WorldCommandModule.cs`
  (the graphics/GPU levers), `WorldHostCommandModule.cs`,
  `WorldViewCommandModule.cs`, `WorldAudioCommandModule.cs`,
  `WorldRecordingCommandModule.cs`, and `WorldSdfCommandModule.cs` are
  genuinely presentation-only (unregistered headless); `WorldUiCommandModule.cs`
  and `WorldWheelCommandModule.cs` are core-registered but refuse by name at
  use when their one presentation dependency is absent (command-vocabulary
  parity — `world.console`/`player.wheel.*` are stock wheel-hold-page rows the
  engine-default document commits in every boot shape).
- `WorldDefinitionLoader.cs` — resolves and validates the boot world
  document; `RecordingDocumentSource.cs` does the same for
  `puck.recording.v1`.
- [`Client/`](Client/README.md) — the per-machine client half: seats and
  device intents, the snapshot-fed entity view and render interpolation, the
  frame source, the in-session editor and sculpt workbench, the audio
  director.
- [`Audio/`](Audio/README.md) — the deterministic mixer core, synth voices,
  and the WASAPI output device.
- `WorldScreenBinder.cs` and `ScreenCommandModule.cs` — the diegetic screens
  (below).
- `WorldRenderSettings.cs`, `WorldRenderProbe.cs`,
  `WorldDynamicGeometryCeilings.cs` — the live render levers and the probed
  capacity envelope live placement is validated against.
- `WorldAvatarCatalog.cs`, `WorldCameraRigCompiler.cs`, `WorldAnchorGeometry.cs` —
  the deterministic avatar catalog (a distinct animated humanoid rig per
  population slot, authored without RNG state) and rig compilation.
- `RecordingTap.cs` — the render-tap capture path (below).
- `Assets/` — the checked-in worlds (`worlds/*.world.json`, count them there
  rather than here: `play` — the hub and boot default — `dive`, `kart`, `jump`
  — the four-world charter's whole game roster, 2026-08-06 — plus `studio`, a
  non-game dev canvas for character work reached only with `--world`: neutral
  floor, no scenery or crowd, four anchored camera eyes and a `sheet` layout
  composing front/three-quarter/side/back at once; and the `quilt-*` documents,
  test content for adjacency and corner-crossing work, outside the
  charter roster and not game worlds), the default recording document
  (`recordings/`), two shipped
  WASM addons (`addons/`: `default`, `hudbuilder`; mounted by no shipped world
  today — the `arcade` addon was ported to a world `rules` section and its
  compiled guest deleted before the addons themselves went unmounted), a
  hand-authored SM83 cartridge ROM (`roms/`: `arcade-quest.gbc`, also unhosted
  today — see `src/Puck.Forge/Games/README.md`), and an example `puck.sdf.v1`
  document (`sdf/`).

## The world as data

Everything durable is a document field; everything live is a console verb;
there is no `PUCK_*` configuration surface for this game. The document
families, their serialization contract, and the strict-parse rules are
[`Puck.World.Data`](../Puck.World.Data/README.md)'s to describe. Live editing
is one mutation vocabulary — validate the whole candidate, apply at the tick
boundary, journal, `world.undo` by replay — owned by
[`Puck.World.Server`](../Puck.World.Server/README.md). `world.save` writes a
canonical session snapshot (live render levers, census, and runtime screen
inserts fold into their document homes; every advancing `state` row/cell
settles to its live value with its projected epoch reset to 0, so a reload
resumes exactly where the save observed it instead of reading frozen — the
live document itself is never touched), and `world.status` reports source,
counts, drift, and the journal length.

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

## The diegetic screens

Three primitives, split cleanly: a **surface** (a `WorldScreen` slab in the
document), a **source** (the signal it carries — a closed `WorldScreenSource`
hierarchy: test pattern, booted machine, webcam, compositor capture, or a
jumbotron view of this same world through a placeable `WorldCamera`), and a
**route** (whether a player may engage it, and within what radius).

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
local producer — actually holds the slot). A QR is the one source with no
per-frame cost at all: `QrEncoder` (in `Puck.World.Data`) resolves the module
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
diverts a player's intent wire onto the machine — the same `PlayerIntent`
currency, translated once into a neutral pad image, folded server-side
(`WorldEngagement.FoldTick`) and read directly by `WorldMachineHost.Advance`
inside `WorldServer.Step`; engagement authority rides the grant table's
`Control` capability. See [`Puck.World.Server`](../Puck.World.Server/README.md) for the full contract.

## Native capture

The world records itself to WebM/Matroska through the recording graph in
`Puck.Recording` (`puck.recording.v1` — resolved at boot from `--recording`
or `Assets/recordings/default.recording.json`). The render root is wrapped
once in a capturing node; arming a session (`capture.start`/`capture.stop`/
`capture.status`) reads captured frames back to CPU pixels and composites
capture-only overlays that never appear in the game window. The tap is free
when idle.

Playback time is WALL-CLOCK time, not engine time: the shipped document sets
`clock: "Wall"`, so blocks are stamped from QPC when the frame reaches the
sink. `Sim` stamps from the engine tick clock instead but forbids audio rows.
The two diverge under capture — the readback is synchronous per captured
frame, so the frame rate roughly halves while recording (frames are never
produced, not dropped; `capture.status` reports the drop count separately).
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

## Engine boundaries worth knowing

- `SdfProgramBuilder.MaxInstances = 16384`: per-tile mask width scales with
  DECLARED instances, which is why this project emits active avatars only and
  probes capacity floors at construction.
- The per-pixel soft-shadow gather addresses ≤1024 instances; beyond that the
  engine falls back to coarser camera-tile masking.
- `ViewStack.MaxRegisteredViews = 64`: do not register a rendered view per
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
when the frame lands (`[capture] unified overlay -> <path>`, or `[debug]
captured frame N -> <path>` when the overlay drew nothing and forwarded the
request down to the engine node). Arming a second capture while one is still
pending is REFUSED by name — the earlier path would never be written — and a
request still outstanding when the run ends prints a `WARNING` naming it. A
scripted caller can therefore always tell "written" from "never happened",
which the pre-2026-08-05 echo (a bare path, printed at arming time) could
not.

Committed, re-runnable batteries cover specific load-bearing seams, under
`docs/verification/`: `undo-all-or-nothing`, `strict-definition-parse`,
`sdf-decode-sign-refusal`, `doc-links`, `addon-mutation-seam`, and
`four-world-boot-smoke`. `four-corners-sharded` is the stronger five-authority
adjacency/federation stress run: four ground worlds plus the floating island,
with human and autonomous handoffs, retained controls and camera routing,
cross-host contact, derived corner peers, and routed read-backs. Each
builds what it needs and exits nonzero on a miss. `ordered-domain`,
`headless-boot`, `lane-present-deletion`, `hud-document`,
`engagement-dissolution`, and (at the repository root)
`verification/authority` are QUARANTINED (`authority`/
`engagement-dissolution` 2026-08-06 — `authority`'s cases 04-06 assumed the
retired `default` world's `screen:0` and mounted addon, and
`engagement-dissolution`'s every phase from (b) on assumed the same
`screen:0` to `screen.insert`/`player.engage` against — no shipped world
authors a `screens` or `addons` row today; the other four by the earlier
2026-08-06 owner ruling) — each is a stub that prints a pointer and exits 3;
see each stub's own header for what it used to prove and the live
run-the-app recipe (or, for `authority`, the successor:
`tests/Puck.World.Tests`'s `AuthorityAdministrationLawTests`, not yet in
`Puck.slnx`, expected to absorb `engagement-dissolution`'s engage/disengage
phases too) that replaced it.

The former World proof suite (proof.cs and its standalone harnesses) was quarantined
out of the build on 2026-08-02 with the rest of `experimental/`; nothing has
taken over its coverage, and its subcommand inventory lives in git history,
not here. Do not cite it or re-derive a gate from it — the current
verification story is the paragraph above.
