# Puck.World

Normal builds generate the registered world JSON and shader bytecode from their
tracked parents. See [generated assets](../../build/README.md) for build,
source-control, and packaging rules.

Puck.World is the application that composes a document-defined local
multiplayer world of up to 4096 simulated bodies (four local seats plus
autonomous stand-ins), rendered through the SDF engine and scripted end to end
over its own console. This project is the composition root of a multi-project
engine. This README owns application usage; sibling projects own their local contracts:

| Project | Owns |
|---|---|
| [`Puck.World.Schema`](../Puck.World.Schema/README.md) | The `puck.world.definition.v1` document model |
| [`Puck.World.Protocol`](../Puck.World.Protocol/README.md) | The wire/tape protocol |
| [`Puck.Networking`](../Puck.Networking/README.md) | The world-agnostic wire substrate—the frame grammar and reader/writer pair |
| [`Puck.World.Server`](../Puck.World.Server/README.md) | The authoritative runtime: the tick, entity table, grants, addons, owned worlds, storage, replay |
| [`Puck.World.Client`](../Puck.World.Client/README.md) | The per-machine client half: seats, the entity view, the fly camera application, and the binding-authoring layer |
| `Puck.World` (this project) | The audio director, the frame source, presentation, console command modules, assets, and `Program.cs` |

The reference-game design lives in [the game guide](../../docs/game/README.md);
nothing there is evidence that a capability is built. What each `Puck.*`
project is for is [`docs/project-map.md`](../../docs/project-map.md).

## Operator access

For AI pairing, `world.control start|stop|status` manages an authenticated
Operator attachment without restarting World. CLI serves console exec and
completed PNG tools over local stdio or OAuth-protected remote HTTP; see
[setup and trust](../Puck.Mcp/README.md).

## Usage

For the standalone avatar prototype, see the [Moth flight studio](Assets/worlds/avatars/moth.md).
It pairs a rebuilt SDF avatar with the Moth Pipeline, with inspection cameras,
walking and flight poses, and live document reload.

The [Moth courtyard](Assets/worlds/moth-courtyard.md) imports that model into an
eight-body scene: one isolated inspection subject and seven held pose stations,
with named cameras and independent sky and volumetric-cloud toggles.

For service extensions, use the [configuration guide](../Puck.World.Server/ExtensionConfiguration.md)
and the [Azure example](Assets/hosting/azure.extensions.example.json). The operator
selects the file with `--extensions-config-file`; `world.extensions.catalog`
lists every composed [extension](../../docs/reference/extensions.md) and its
contributions, and `world.extensions` reads back the configured connections and
participants.
World composes both Gaming Brick forges as built-ins and discovers the rest from
`extensions` under the working directory and beside the executable; an
installation it cannot use refuses the boot by name. Imported world content
cannot enable cloud access by itself.

The [granaries district](Assets/worlds/modules/README.md) renders the existing Azure
deployment's storage inventory on the island's own plaza as an importable, authored
storeyard; the same collection tables can serve a text world. Its buildings can preserve gameplay-owned
transforms and facets across inventory refreshes, and `world.reflow.preview` / `world.reflow.status` / `world.reflow.commit` rearrange
declared footprints through a guarded, undoable placement-and-payment batch. The district guide owns the policy
and its current boundaries.

```bash
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6
```

`--exit-after-seconds 0` (or omitting the flag) runs until the window closes.

For repeated launches, publish once for the target desktop and run that artifact:

```powershell
dotnet publish src/Puck.World -c Release -r win-x64 --self-contained false -o artifacts/world
dotnet artifacts/world/Puck.World.dll --world worlds/parlor/chess.puck
```

Use `linux-x64` for a Linux desktop. World publishes with
[ReadyToRun](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
precompilation to reduce first-use JIT work. This makes publishing slower and
assemblies larger; ordinary `dotnet build` and `dotnet run` still use IL.
`-p:PublishReadyToRun=false` opts out for a comparison. Restore prepares both
desktop runtime graphs and the compiler pack, so a later publish can use
`--no-restore`. CI publishes the already-built portable assemblies with
`AppendRuntimeIdentifierToOutputPath=false` and `--no-build`; the project
regenerates the runtime-specific dependency manifest in the publish directory.
Precompilation does not rebuild C# sources or replace the portable build's
manifest. Measure a published artifact with
`puck bench startup --world-artifact artifacts/world/Puck.World.dll`.

`--world` takes a `.world.json` document or a `.puck` source, which boots
without a prior compile. A source that declares several worlds — a
composition such as [Rulepush](../../worlds/rulepush/README.md) — boots the
world it declares `entry world`; `--entry <name>` boots another of its declared
worlds instead, which is how a level of a composition is worked on directly:

```powershell
dotnet run --project src/Puck.World -c Release -- --world worlds/rulepush/rulepush.puck
dotnet run --project src/Puck.World -c Release -- --world worlds/rulepush/rulepush.puck --entry hedges
```

Either way the game stages every declared world under
`<state-dir>/compositions/<source name>`, so each border crossing finds its
neighbour, and prints one `[world] composition:` line naming the staging
directory and the world it booted. A composition with no entry and no
`--entry`, an `--entry` the source does not declare, and an `--entry` beside a
world that is not a composition each refuse the boot by name.

A document reference inside a world (`basis`, `imports`, `references`) names a
document, such as `games/poker`, never a file. It resolves to the `.puck`
source beside the referrer when one exists and to the `.world.json` document
otherwise, so the source tree and the built output (`Assets/worlds` beside the
executable, which carries compiled documents only) boot the same worlds.

A `.puck` source compiles once: the boot, every basis and import it reads, and
`world.reload` compile through one cache, kept per user in `compilations` under
the [per-user Puck directory](../../docs/development/contributing.md#per-user-directory)
and shared with the CLI, so a second boot of
an unchanged source tree compiles nothing. A held compile is used only while
every file it read — the source, its modules, its locks and assets, and every
path it probed — still holds the same bytes, so an edit recompiles exactly the
sources that read the edited file. The boot's own work (documents read and
merged, sources compiled or answered from the cache, parses, validations,
neighbour resolutions and more) is the `world.boot` work source every counters
report reads.

A boot takes its drawn definition from a
[compiled world](../../docs/architecture/worlds.md#compiled-worlds) when one
holds for the document: the `<name>.puckb` beside it, which the build ships for
every world in `Assets/worlds` and `puck compile` writes, or the one a previous
boot wrote into the per-user `compiled-worlds` cache, which every boot shares
whatever its `--state-dir`. A boot that finds none, or finds chunks that no
longer hold, derives them and writes the whole compiled world into that cache,
leaving out the creation bakes: those a
compiled world names fill the bake cache from the build's bake pack
(`Assets/worlds/bakes.puckbake`), and a presentation bakes any that are
missing in the background and keeps them in the per-user `bakes` cache
([creation bakes](../../docs/architecture/worlds.md#creation-bakes)). The
entry point names these three per-user caches and hands them to the boot as
`WorldCacheRoots`, so a host or test that composes World services uses
directories of its own.

Boot prints one line naming the world-definition file it loaded (an explicit
`--world <path>` or the shipped `Assets/worlds/puck.world.json`), one
`[world] compiled world:` line naming the compiled world it read, the chunks it
kept, derived and deferred, and where it wrote one, one naming
the recording document, and one capability-disclosure line per mounted addon. The full CLI
flag surface (backend, size, world, recording, user id, present mode, listen,
connect, federation key) is declared in `Program.cs`; the graphics API is the boot-time
choice `--backend directx|vulkan` (Direct3D 12 is the Windows default),
because changing APIs rebuilds the whole render host. `--debug-layers` creates
the device with its backend's validation layer, printing `[vulkan-debug]` or
`[d3d12-debug]` lines; `puck canary --debug-layers` and `puck qualify` pass it.

`--help` (or `-h`) and `--version` exit before host setup: they do not load a
world, open a window, or initialize storage. Use `--version` alone; the parser
refuses combining it with other arguments. Invalid options exit with a diagnostic.

| Exit code | Meaning |
|---|---|
| 0 | The run ended normally. |
| 1 | The boot was refused by its options, world document, or configuration. |
| 2 | This host cannot run the boot: the selected graphics backend has no usable device, the operating system does not offer it (Direct3D 12 off Windows), or the QUIC listen endpoint cannot be bound (the address is in use, the operating system refused it, or QUIC is not offered here). Stderr carries one `[world.host: unsupported: <resource> unavailable: <reason>]` line, such as `vulkan device unavailable: …` or `quic listener 127.0.0.1:7777 unavailable: …`. |
| other | A host pump or service failed; the exception and its stack trace go to stderr. |

A backend raises `GpuDeviceUnavailableException` from its own device bring-up
when the loader or driver is missing, no adapter meets its requirements, or the
driver refuses the device. Both boot shapes report that as exit 2. The offscreen
shape brings its device up while the host starts. The windowed shape brings it
up in the window pump's presenter activation, before the window is shown. Either
way, the failure is reported rather than surfacing inside the frame loop.
`Program.cs` ends in `LauncherHostRun.RunAsync`, which maps both places to the
exit code. A pump that fails for any other reason fails the run, so the process
never exits 0 after a pump crashed. Teardown after such a failure still runs
every step. A step that fails then is logged and never replaces the original
failure. Each GPU consumer releases only what it allocated, so after a failed
bring-up nothing asks for the device.

Compiled SDF shaders can change within that running host: finish `CompileShaders`,
then issue `world.shaders.reload src/Puck.SdfVm/Assets/Shaders/Sdf` and inspect
`world.shaders.status`. The request swaps changed compute pipelines at a frame
boundary while retaining world and GPU scene state. Failed validation keeps the
previous set. See the [shader reload contract](../Puck.SdfVm/README.md#reload-compiled-shaders)
for scope and binding-layout limits; `world.reload` remains the world-document verb.

**Networking.** `--listen <ip:port>` (or the document's `host.listen`) binds
the QUIC peer endpoint (`WorldPeerHost`) so a remote peer can join the same
ordered domain a local script drives; `--connect <ip:port>` keeps the normal
world/presentation composition while its local seats are authorized and driven
by that remote authority. Listening and connecting may coexist on the shared
`Puck.Networking.Peers.Peer`; neither is enabled by default. The listener binds
while the host starts and narrates `[world.listen: bound <ip:port> …]`; port 0
binds any free port, and that line names the one bound. An endpoint that is not
an `ip:port` pair refuses the boot with exit 1, and one this host cannot bind
exits 2 as described under the exit codes above. The networking
library owns TLS, certificate-bound identity, and bounded message delivery;
there is no TCP fallback. `PeerStream` adapts those messages to World's byte
codecs. After that peer handshake, an interactive connection crosses two
application checks: `WorldHelloDoor` (the version-1 protocol contract) and
`WorldAdmissionDoor`
(`Puck.World.Schema`'s `WorldAdmissionDoor.cs`)—a challenge-response
identity check over `Puck.Attestation`'s signed attestations against the
world document's own `admission` section. A world authoring no `admission`
entries admits no remote peer at all, and no traveller from another authority
either (deny by default—a transfer's own arrival verdict comes from the same
section, through a keyless `federatedAuthority` row naming the source authority
namespace or `*`). Transport identity alone grants no World permissions.
`world.peers` echoes each connection's
verified identity and mapped principal and disclosure tier, plus an `arrivals:`
group naming each transferred body and the authority its verdict was decided
against; `world.admission` echoes the document's own authored entries and each
one's `disclosure`. `world.projection` echoes what this authority would hand a
peer at each tier—the byte size and the section inventory—or at the tier a
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

**Hosted Puck.** Sign in with a ByteTerrace API Users account and use the
generated authentication configuration from the deployed [Puck world release](../../docs/development/ci.md#azure-production-deployment):

```text
dotnet run --project src/Puck.World -c Release -- --connect play.puck.byteterrace.com:7825 --authentication-config-file /path/to/world-authentication.json
```

The installed authentication extension acquires the API token and checks the
server's pinned key before sending it. Do not combine this client configuration
with a federation private key. The primary world admits sixteen network players
and gives disconnected seats sixty seconds of reconnect grace. Connections in
one process share a session namespace; starting a new process creates a fresh
traveller namespace. Cross-process body recovery is a separate travel concern.
The [Azure authentication provider](../Puck.World.Azure/README.md#silo-hosting)
owns token validation and group membership policy; those concepts stay outside
the engine.

**Branding.** `host.title` and `host.icon` are the window's caption and its
icon. Both are durable document fields with no CLI reflection, so a world
shipped by someone else wears its own name and mark rather than the engine's.
`host.icon` resolves against the world document's own directory (an absolute
path is taken as-is), so the `.ico` travels beside the world file:

```json
"host": { "title": "Harbour Nine", "icon": "harbour-nine.ico" }
```

Authoring neither wears the executable's own icon and `Puck: World`. An icon
this OS cannot read falls back the same way rather than refusing the boot.

**Headless.** `--headless` (or the document's `host.presentation: none`) boots
with no window, no GPU device, no swapchain, and no audio device—the
authoritative server, console, and tape only:

```bash
dotnet run --project src/Puck.World -c Release -- --headless --exit-after-seconds 6
```

`--headless` is a developer/CI reflection of `host.presentation`, never a
separate product (the unification contract): the SAME console verb surface
drives both shapes, minus whatever presentation composed. The command
VOCABULARY itself must be identical in every shape—the document validators
check a world's `bindingOverlays`
against whatever this composition registers, so a genuinely presentation-only
verb (`world.fps` and the other graphics options, host and audio levers,
recording) refuses as
UNKNOWN over headless stdin, while `player.mode` (and the fly camera
application it can activate) is CORE-registered (nothing in its dependency
chain is GPU-typed), terminal-owned `console` is
registered beside `quit`, and `world.screenshot`, `player.wheel.*`,
`view.override`/`world.view.*`, and `pipeline.*` are
CORE-registered too but resolve their presentation dependency as OPTIONAL and
refuse BY NAME at use instead of going unregistered—a headless boot that
left a stock wheel sector unregistered would refuse the SAME boot document a
windowed boot admits. `screen.*` is
registered in EVERY shape (the machine host is core
state, not presentation-fed): `screen.insert`/`.eject` apply through the
ordered domain headless exactly as windowed, and `screen.source <index> camera|capture|desktop|probe|view|qr` still
attempts a real device open (or, for `qr`, a real encode) and reports the
honest failure rather than refusing as unknown.
`WorldBootComposition.cs` is the split: `AddWorldAuthoritativeCore` registers
in EVERY shape, `AddWorldOffscreenPresentation` only offscreen, and
`AddWorldPresentation` only when a window is composed. `AddWorldBoot` selects
among them, and `WorldBootCompositionLawTests` resolves what it registers
without a device.

**Offscreen.** The document's `host.presentation: offscreen` boots a real GPU
device and the composed-frame render pipeline (the world render alone—no post-render extension chain,
unified overlay/console-mirror/binding-bar, no audio device, no gamepad or
pointer input) with NO window and NO swap chain ever created, so
`world.screenshot` writes real PNGs of the composed world with nothing on
screen. There is no `--offscreen` CLI flag; author it in the world document:

```json
"host": { "presentation": "offscreen", "width": 640, "height": 480 }
```

Direct3D 12 is genuinely surfaceless (the device activates on its first GPU
call); Vulkan's device bring-up in
this codebase is fused to a real native surface, so this shape stands up a
native window through the SAME path the windowed shape uses but never shows
it and never builds a swap chain against it—see
`WorldOffscreenGpuActivation`'s remarks for the exact obstacle. Diegetic
View-type screens (the jumbotron pool the windowed render-root factory stands
up via `WorldScreenBinder.ConfigureViews`) are a known gap this shape does not
compose. `WorldBootComposition.AddWorldOffscreenPresentation` and
`Puck.Launcher.OffscreenTickHostedService` (which produces one composed frame
per host-loop iteration, paced by the fixed-step pump rather than vsync) are
the seams; the server steps exactly like `host.presentation: none`
(`HeadlessWorldSimulation`).

Because its frames are its only output, the offscreen host holds its clock
for them: it never steps past an armed capture's tick until that capture is
served or refused. Whatever keeps the render chain from serving the capture
(the engine's pipelines still building on a cold driver shader cache, or a
device being rebuilt), the host keeps producing frames and answering the
console but steps no further tick, and the time it waits is spent, not owed,
so serving the capture releases no burst. The hold is bounded, and counts
from readiness: while the engine's pipeline set builds (or rebuilds after a
device loss), the run may hold its clock for 180 seconds in all
(`WorldCaptureScheduler.BuildHoldBudgetSeconds`), and once the engine is ready
for 60 seconds in all (`WorldCaptureScheduler.HoldBudgetSeconds`). Past either,
the capture is refused as `unserved` and the run steps on; a refusal the build
caused names it and its progress, such as "the engine's pipeline set is
building (5 of 14 pipelines created)", and a later capture the chain still
cannot serve is refused at once. `world.counters`
reports the hold under `world.captures`: `world.captures.held` (engine ticks
withheld) and `world.captures.ticks-while-armed` (ticks stepped while a
capture waited, which stays 0 offscreen). A capture still waiting when the run
ends is refused as `unserved` before the render chain is disposed. The windowed
host paces to its display and never holds.

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
local/travel renderers use.

A seat's camera reads its rig from the document of the authority the seat is
routed to, and a rig operand bound to `state.<row>[.<key>]` reads that same
authority's rows: the client's own state mirror while the seat is on the
authority this client observes, and the routed authority's followed mirror,
at that authority's delivered tick, once the seat travels to another. The chase
framing's body scale, the seat's state-backed binding contexts and its radial
wheel's label and icon rows read the routed authority the same way, so a seat
never frames, binds or labels itself with the rows of a world it has left.
The wheel's rings are rebuilt only when one of the label or icon cells they read
changes, not on every state delivery.

A seat's route can change on any thread: the tick thread commits a local
transfer or runs a console verb, and a federated observer reports an onward
handoff from its socket worker. Publishing the claim is safe from either, but
what follows a route change rewrites the seat's bindings and binds its reads to
a state mirror, and neither takes a lock. `WorldSeatAuthorityRouter.RouteChanged`
therefore fires only on the thread that pumps and presents: `WorldHostStep`
delivers the changes published since its last delivery before a step's
intents, after the transfer drain, and after the other instances step. A
subscriber may assume it runs on that thread.

Binding contexts can also select complete control groups from gameplay state.
A context family named `state:<row>` reads that declared world-state row: a
scalar row publishes one value to every seat, while a keyed row reads the
controlled body's entity-index cell. Ordinary rule `setState`/`addState`
effects therefore switch mappings through the same validated, replayed state
pipeline as the rest of gameplay. `player.bindings` reports the published
state, matched group, and precedence winner. A missing row contributes no match,
which keeps portable profile layers usable across worlds. When the winning group
changes, held commands and chord/page latches from the old group are cleared.

## Shader pipelines

A `views.graphs` row (a `graph "name" { … }` block inside `views` in `.puck`)
names an instance of a
[frame graph](../../docs/reference/shaders.md#frame-graphs) and its source: a
`puck.render.graph.v1` graph document, a one-off HLSL shader read as a
one-pass graph, or a shader package directory written by
`puck shaders package`, which loads from its precompiled binaries with its
source tree gone and no compiler on the machine. A document or shader source
loads the package the game's build stored for it in `Assets/worlds/packages`
when one matches, which is every source a shipped world names, and otherwise
compiles in the background, which needs `dxc`; without `dxc` it is refused by
`SHADERPKG_ABSENT` (see
[the build's package store](../../docs/reference/shaders.md#the-builds-package-store)).
Every pass is HLSL; it reads its
time, pointer, camera and config from the generated frame block the
[shader reference](../../docs/reference/shaders.md#the-frame-block)
describes. A relative
source resolves against the CURRENTLY LOADED document's own directory, on
both the server's override gate and the rendering host: a `world.load` of a
document from another directory moves that directory for every row the
loaded document names, the same directory a live `pipeline.commit` binds
against from that point on. A silo-hosted world has no local document file
(its definition arrives from cloud storage), so a relative source there
resolves against the executable's own directory instead. A row may instead
name an engine `package` producer, such as `sdf.world`, in place of a source.
A layout slot shows the instance by naming it with `instance`; it can instead
select a `camera`, but cannot select both. The root graph places each pane
over the world at its slot's rect, renders it at the slot's size, and feeds it
the slot's pointer; a pane the active layout does not show is not rendered,
and a layout change places panes one frame later. The row's optional camera
supplies shader camera inputs, and `timeScale` seeds its presentation clock.
A row also takes a refresh divisor or rate and inputs bound to other rows'
outputs, with `views.graphBudget` as the scheduler's pass-pixel ceiling, and
`world.budget` prices each row by planning its source. `views.shaderToolchain`
optionally selects the directory holding `dxc`. `pipeline.sentinels <name> on`
writes each frame-block word's echo sentinel in place of the frame values and
config, which an [echo pass](../../docs/reference/shaders.md#pass-interfaces)
reads back. The `pipeline.*` verbs address `views.graphs` rows by name.

Start the three-pass feedback example from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/pipeline.world.json --state-dir artifacts/pipeline/state
```

The ink simulation feeds a color pass and a fullscreen finish. Drag the pointer
to draw. From the console:

```text
pipeline.status
pipeline.watch ink on
pipeline.inspect ink
pipeline.set ink simulation {"decay":0.995}
pipeline.set ink visualize {"exposure":1.4}
pipeline.time ink pause
pipeline.step ink
pipeline.output ink simulation
pipeline.capture ink artifacts/pipeline/simulation.png
pipeline.output ink image
pipeline.reset ink
pipeline.time ink resume
```

Edit [the simulation shader](Assets/pipelines/ink-simulation.hlsl) while it runs.
Compilation happens in the background. A typo reports a source diagnostic and
keeps the complete last successful pipeline running. Saving a correction
installs a new candidate at a frame boundary. `pipeline.reload ink` requests
compilation explicitly. A step advances time by 1/60 second and leaves the
instance paused; reset clears feedback and time. `pipeline.inspect` shows the
GPU work of the newest completed submission, then the ordered passes, resource
lifetimes, resolved image sizes and allocation bytes. Its first line ends with
`work submission=S revision=R`, or `work unavailable` when nothing has completed
since the last install, resize or reset. Each pass then has its own line,
`work <pass> executed: dispatches=… draws=… barriers.image=… …`, followed by
`work outside: …` for the finalizing work and `work lifetime: …` for the
instance's counters, including the GPU objects it has created.
`pipeline.status` repeats each instance's `work lifetime` line. The counts are
the calls each pass records, so they are the same on both backends and never a
measure of time. The first line also carries the instance's memory, in bytes:
`owned=` (everything it holds, graphs still retiring included), `steady=` (the
installed graph), `peak=` (what a reload of it would reach) and `budget=`.
`pipeline.budget <name> <bytes>` caps an instance's
[memory budget](../../docs/reference/shaders.md#memory-budget) below the
device's, and `pipeline.budget <name> device` removes the cap. A reload whose
peak does not fit is refused with `SHADERPIPE_BUDGET`, and the installed graph
keeps running.
`gpu.faults arm <kind> [<n>]` makes the device fail the nth creation of a kind
from now, or the next one, before the call reaches the device, so a reload can
be refused partway through its allocation on real hardware. The kinds are
`pipeline`, `buffer`, `image`, `render-pass`, `framebuffer`, `shader-module`,
`command-pool` and `bindings-pool`. The refusal is `GPU_CREATION_FAULT`, naming
the kind and the creation's number; the node releases what the candidate
created and the installed graph keeps running. A fault fires once.
`gpu.faults lose [<n>]` loses the device on the nth frame from now, or the next
one, on a healthy GPU; the host recovers through its
[device-loss policy](../../docs/reference/hosting.md), and a capture armed at
the loss is refused as `[capture] refused <path>: <reason>`.
`gpu.faults disarm` clears every fault, the armed loss and every count, and
`gpu.faults list` prints `armed=<kind>:<n>,…,lose:<n>` (or `armed=none`), one
`<kind>=<count>` field per kind, the creations since the last disarm, and
`frames=<count>`, the frames counted since then. Only the operator's console
runs it; a seat, binding, schedule step or world document cannot.
`pipeline.capture` queues a PNG of the selected output, including while paused.
The completion report says when the file has been written.

`pipeline.set`, `pipeline.output` and `pipeline.time … scale` preview values
for this session only. `pipeline.set` merges field by field: each field it
names replaces that field, and `null` returns the field to the source's
default. `pipeline.overrides <name>` shows every overridden field's committed
value beside its preview, then the time scale and output the same way. It also
shows the row revision the preview is based on and the source identity of the
installed graph. `pipeline.commit <name>` writes the preview into the
instance's `views.graphs` row as `overrides`, `timeScale` and `output`, and
`world.save` then writes the document:

```text
pipeline.set ink visualize {"exposure":0.5}
pipeline.overrides ink
pipeline.commit ink
world.save
```

The commit refuses, naming the reason, when another edit moved the row since
the preview began, or when the source changed after its graph installed (let
the edit install and preview again). Moving the row discards the preview and
shows the committed values. The shared pipeline file keeps its defaults, so
another instance of the same source keeps its own values. Booting a document,
`world.load` and `world.reload` bind every row's committed overrides against
its source too, and refuse a value the source's config schema refuses by name,
so a saved document never carries a value that would only be reported once a
graph installs. The
[shader reference](../../docs/reference/shaders.md#per-instance-overrides)
states the contract.

A row naming a package reads the same way:

```text
puck shaders package src/Puck.World/Assets/pipelines/ink.graph.json --output artifacts/packages/ink
pipeline.load ink ../../../../artifacts/packages/ink
pipeline.wait ink installed
```

A package refusal fails the instance's compilation with its code, such as
`[pipeline: ink [SHADERPKG_FILE_PIN] …]`, and the last good graph keeps running.
See [naming a package from a World](../../docs/reference/shaders.md#naming-a-package-from-a-world).

A script waits for a pipeline the way a person watches `pipeline.status`, but
with a deadline. `pipeline.wait <name> <phase> [seconds]` holds only the issuing
session until the phase is reached or the deadline passes. The deadline is in
presentation time, defaults to 30 seconds, and may be at most 600. It bounds
liveness and is never a measurement. The phases are:

- `compiled`: the latest requested compilation has completed.
- `installed`: that compilation's candidate is the installed, allocated graph.
- `submitted <frames>`: the installed graph has submitted that many frames
  since its last reset.
- `counted <submissions>`: the work counts of that many submissions since the
  last reset (or since boot, if it was never reset) have completed, so
  `pipeline.inspect` shows them.
- `captured`: the latest `pipeline.capture` has been written or has failed.
- `resized <width> <height>`: the host has asked the instance for that output
  extent, because the layout slot showing it changed size, and the instance has
  installed its graph at that extent.

A candidate's shader modules and pipelines are built off the frame thread, and
the instance keeps presenting its installed graph until they are ready; the
candidate installs at the first frame after. A reload, an edit of the row and a
resize replace the graph and are not steps, so a paused instance (paused, or at
time scale zero) builds and installs them too and reaches `installed` and
`resized` without a step. The install renders nothing and leaves `submitted`
where it was. The instance keeps showing its last image, and paused captures
read it, until a step, a resume or a reset renders through the new graph. A step
taken while a replacement still builds waits for it, then renders once through
it. An output selected on a graph installed while paused is published with that
graph's first frame. When two edits arrive before the first has installed, the
first is dropped and the runtime says so on stderr:
`[pipeline: <name> superseded: compilation]` while it was still compiling, or
`superseded: compiled candidate` when its compiled candidate was still queued or
building. An edit that has already installed is simply replaced by the next one,
with no line, so whether a quick second edit reports the first as superseded
depends on compile and build timing, paused or not. After `pipeline.reset` on a paused instance,
exactly one initialization frame renders, so `submitted 1` and `counted 1`
are that frame. That frame never uses up a step: a step issued right after the
reset renders one frame beyond it, so it is submission 2.
Exactly one outcome follows on stderr:
`[pipeline: <name> wait <phase> reached]`, `… failed: <why>`,
`… unsupported: <why>`, or `… timed out after <seconds>s: <state>`. A load
refused because a shader tool is absent reports `[pipeline: <name> unsupported:
…]`. The `pipeline-*` canaries drive these phases
offscreen on both backends; see [`puck canary`](../../docs/reference/cli.md#puck-canaryreal-world-behavioral-proofs).

`pipeline.load <name> <source> [camera]` authors a row through normal world
validation. The rendered host creates it only after the mutation is accepted.
To try a one-off shader in the example's existing slot, run
`pipeline.load ink ../pipelines/moth.hlsl`; it replaces that instance's source
through the same background compilation path. Return with
`pipeline.load ink ../pipelines/ink.graph.json`.

Loading a new row does not change the active layout: name it as a layout
slot's `instance`. The [shader reference](../../docs/reference/shaders.md#shader-pipelines-and-live-development)
owns the graph document and pass contracts.

[The Moth shader](Assets/pipelines/moth.hlsl) remains a one-pass procedural
character example; its header controls poses and framing. Its
[concept pack](../../docs/game/art/moth-concept-pack/README.md) is the visual
reference.
## The console

The console is the control plane: process stdin in, results on stdout,
refusals and server narration on stderr, all mirrored onto the in-game panel
(the terminal's `ConsoleTape` in `Puck.Hosting`, drawn by `Puck.Overlays`'
console-panel writer). Every capability is a verb. **Type `help` for the
live, self-documenting verb list**—it is generated from the registered
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
  `world.wait` are the explicit waits. `world.wait ready <seconds>` waits for
  the rendering engine instead of a tick count: it holds the session until
  the engine's pipeline set is installed and it has produced its first frame,
  or the deadline passes, and reports which on standard error. A script that
  reads rendered work (`world.counters gpu`) waits on it, since a cold driver
  cache can hold the first frame back for many ticks.
- **Timing.** The console drains before every fixed step. A piped script's
  lines up to its first `world.wait` run before the first tick, and the line
  after a `world.wait` that releases at tick R runs before tick R+1. The
  [commands reference](../../docs/reference/commands.md#who-can-dispatch-a-command)
  owns this contract. End a script with `quit` to stop the World when the script
  ends; `--exit-after-seconds` stops it after a wall-clock time instead.
- **Ordering.** Within one stdin batch, submissions apply in FIFO order
  across kinds—a grant before a command is visible to that command. The
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

- `Program.cs`—the composition root: resolves the world, the host settings
  and the command-line options BEFORE any registration, then hands them to
  `WorldBootComposition.AddWorldBoot` as a `WorldBootInputs`, which registers
  them, calls `AddWorldAuthoritativeCore` always, and adds the shape's host;
  `WorldPostBuildWiring.Install` wires the affordance vocabulary, RE-VALIDATES
  the boot document's binding vocabulary now that the registry is real (the
  FIRST validation, at `WorldDefinitionLoader.TryResolve` above, ran before
  the registry existed, so its command half was a documented no-op in EVERY
  boot shape—see `WorldPostBuildWiring.Install`'s remarks), the session-lever
  sink, and the server's echo/cue taps once, after the container builds, in
  EITHER shape. A refused re-validation prints its reason and fails the boot
  (`Install` returns `false`) before `Program.cs` ever calls `IHost.RunAsync`.
- `WorldBootComposition.cs`—the boot's composition (above): everything
  server-safe (profiles, roster, server, grants, addons, replay tape, the
  console's tick barrier, `WorldMachineHost` and `WorldScreenBinder`—the
  machine host is core state that boots and steps in every shape, and the
  binder is CORE too, since `world.faces`/`body.engage` read its bound/
  no-signal state even headless—every server-safe command module including
  `ScreenCommandModule`, and the camera control application (the `player.mode`
  and `player.camera` verbs)—for command-vocabulary parity: a world's binding document commits
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
  calls; `AddWorldBoot`'s headless branch calls
  `Puck.Launcher.AddLauncherHeadlessTerminal` plus, on Windows, a standalone
  `Puck.Platform.Windows.AddWindowsPrecisionWaiter`. The two boot shapes are
  never composed together.
- `WorldSimulation.cs` / `HeadlessWorldSimulation.cs`—the two boot shapes'
  `IFixedStepSimulation`s, each a thin holder of one `WorldHostStep`. Windowed
  contributes a post-step (seat-binding sync and seat-context publish);
  headless contributes none—no `WorldClient`, no screens.
- `WorldHostStep.cs`—the one fixed step every boot shape runs, in one order:
  decide whether boot is due, submit its seats' intents, drain the host's
  pending transfers, step boot through
  `Puck.World.Server.WorldServerStepShell.Step` (not in this project)—
  `WorldServer.Step`, then the replay tape's `NoteTick` and the console wait
  gate's and capture scheduler's `PublishTick`—or, while boot is paused,
  drain an administrative mutation and release a stalled `world.wait`; step
  every other instance; run the shell's post-step; finish the seat intents.
  It counts the host work a `world.wait` is clocked by and is the
  `IWorldSimulationClock` the frame producer reads. `Puck.Launcher.FixedStepPump`
  (not in this project) owns the accumulator both boot shapes' hosted services
  drive it through.
- `WorldInstanceHost.cs` / `WorldInstance.cs`—the process's running world
  instances. The boot world is one entry (name `boot`) beside every instance
  `world.instance.start` adds; each non-boot instance holds its own
  `WorldServer`/`WorldPopulation`/`WorldOwnedWorlds` and an empty
  `WorldMachineHost`, shares no singleton with the boot world, and advances on
  its OWN authored `simulation.rateHz` (never a shared build-wide rate) inside
  the SAME `IFixedStepSimulation.Step` call (never a second pump)—a
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
  (`world.instance.seat.*`—enter/warp/face/run/stop/where/leave, applying
  through that instance's own `ApplySession`/`ApplyCommand`, with
  `world.instance.seats` the occupancy read-back and `ReapIfEmpty` retiring an
  instance whose last seat just left); only the CLIENT, the tape, and the
  socket door still address the boot instance exclusively—that remaining
  asymmetry is wiring, not kind, and `WorldInstanceHost`'s remarks carry what a
  real flattening needs.
- `*CommandModule.cs`—the console verb modules, one per family (player,
  world, population, mutation, grants, bindings, profile, screens, collision,
  looks, placements, network, refusals, audio, HUD, state,
  storage, recording, replay, views, waits, sdf, ui, instances, chat). Modules compose against
  the protocol link, so a scripted line drives the same wire a UI would.
  `WorldPopulationCommandModule.cs` (world.players/.devices/.population),
  `ScreenCommandModule.cs` (`screen.*`—the machine host is core state that
  boots and steps in every shape, so its whole verb surface is registered
  there too: `screen.insert`/`.eject` apply through the ordered domain headless exactly as windowed, and
  `screen.source <index> camera|capture|desktop|probe|view|qr` still attempts a real
  device open (or, for `qr`, a real encode) and reports the honest failure
  rather than refusing as unknown), and
  most others are server-safe (registered in `AddWorldAuthoritativeCore`,
  the fly camera application included—see above);
  `WorldRenderLeverCommandModule.cs` (the render levers: shadows, ambient
  occlusion, far field, cadence, render scale, quality and the rest) is
  composed by both the windowed and the offscreen shapes; `WorldCommandModule.cs`
  (frame rate, FPS target, screens, cameras, shader reload, debug view),
  `WorldHostCommandModule.cs`, `WorldAudioCommandModule.cs`,
  `WorldRecordingCommandModule.cs`, and `WorldSdfCommandModule.cs` are
  genuinely presentation-only (unregistered headless); `WorldUiCommandModule.cs`,
  `WorldWheelCommandModule.cs`, `WorldViewCommandModule.cs`, and
  `WorldPipelineCommandModule.cs` are core-registered but refuse by name at
  use when their presentation dependency is absent. Terminal-owned `console`
  is registered in every boot shape; `player.wheel.*` are the rows a world's
  own wheel-hold page binds.
- `WorldDefinitionLoader.cs`—resolves and validates the boot world
  document; `RecordingDocumentSource.cs` does the same for
  `puck.recording.configuration.v1`.
- [`Puck.World.Client`](../Puck.World.Client/README.md)—the per-machine
  client half: seats and device intents, the snapshot-fed entity view and
  render interpolation, the fly camera application, the
  binding-authoring layer, frame composition (`WorldFramePresenter.cs`), scene
  emission (`WorldSceneEmitter.cs`), and offscreen view composition
  (`WorldViewComposer.cs`)—the three read the root's `WorldAudioDirector`
  only through `IWorldAudioFrameFeed`/`IWorldAudioCueSink`, never the concrete
  type.
- `WorldAudioDirector.cs`—derives the emitter table from the delivered
  definition with stable ids, resolves emitter poses per produced frame, and
  publishes `AudioSnapshot`s to the mixer (see
  [`Audio/README.md`](Audio/README.md)). Stays here rather than in
  `Puck.World.Client` because it imports `Puck.World.Audio` types directly;
  the composition root passes it to `Puck.World.Client` types through the two
  narrow interfaces above.
- [`Audio/`](Audio/README.md)—the deterministic mixer core, synth voices,
  and the WASAPI output device.
- `WorldScreenBinder.cs` and `ScreenCommandModule.cs`—the diegetic screens
  (below).
- `WorldRenderProbe.cs`—the probed capacity envelope live placement is
  validated against.
- `WorldRecordingCommandModule.cs`—the recording-session command surface;
  generic frame capture lives in Hosting and is driven by Launcher.
- `Assets/`—the one shipped world, `worlds/puck.world.json` (the island; the
  boot default), a delta over `worlds/standard.world.json` (the standards as
  state, the safety net under everything at y = -64, and its debug texture);
  its districts under `worlds/modules/` (`modules/README.md`) and the tabletop
  games under `worlds/games/`, each an imported fragment; the corner shards
  under `worlds/shards/`, each a `basis` delta over the island. The island
  owns the reciprocal half of the seam: `references`/`destinations` name each
  shard (`nw`/`ne`/`se`/`sw`), and four `adjacencies` rows (`north`/`east`/
  `south`/`west`) site the invisible ownership boundary at the island's own
  ground edge beyond the dive/kart/jump/studio lobes. A shard's own mutual
  ring to its two neighbours scales its edge/spawn/ground geometry uniformly
  from the ring's authored numbers, keeping every derived-corner diamond
  closed against the island's own (much larger) edge placement; each shard
  also drops the one imported-module navigation domain and camera its
  wholesale `placements` replacement leaves dangling. See
  `src/Puck.World.Schema/README.md`, "Document composition"), the default recording document
  (`recordings/`), two shipped
  WASM addons (`addons/`: `default`, `hudbuilder`; no shipped world mounts
  either), and an example `puck.sdf.v1` document (`sdf/`).

## Cartridge authoring

Players create and edit CGB/AGB ROM source directly in the console with the
`forge.*` commands. Source is a `puck.cartridge.v1` document: tiles, palettes,
maps, variables, sprites and ordered input rules. `forge.save` persists the
source, `forge.export` writes native ROM bytes, and `forge.play` saves canonical
source and submits its path through the normal authoritative insertion. The
machine's content provider preserves the source identity and exported symbols.
See the [cartridge authoring guide](../Puck.GamingBricks.Forge/README.md) for
an executable walkthrough and the explicit first-version limits.

## The world as data

Everything durable is a document field; everything live is a console verb;
there is no `PUCK_*` configuration surface for this game. The document
families, their serialization contract, and the strict-parse rules are
[`Puck.World.Schema`](../Puck.World.Schema/README.md)'s to describe. Live editing
is one mutation vocabulary—validate the whole candidate, apply at the tick
boundary, journal, `world.undo` by replay—owned by
[`Puck.World.Server`](../Puck.World.Server/README.md). `world.save` writes the
authored document, not the effective one: a section the world omits stays
omitted, a section the session left alone is written as authored, and a moved
session lever (render levers, master volume, present target, peer-source
default, magazine selector) folds only into a section the world authors. The
host's named machine declarations are written into `machines`, and every
advancing `state` row/cell settles to its live value with its projected epoch
reset to 0, so a reload resumes exactly where the save observed it instead of
reading frozen. The live document itself is never touched. A world with a
`basis` or `imports` saves as a delta over them. `world.status` reports source,
counts, drift, and the journal length. `world.imports` reads back the whole
basis-and-imports composition graph in merge order, each import with the alias
it composed under (see
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
compact field placement-shape count, the analytic placement-collider
ceiling, and the solid field's distance grid (cell size, corner extent, baked
corners, contact band, bake hash). Each kit independently authors `bodyContact: "Overlap" | "Solid"`
(default `Overlap`). Two dynamic bodies depenetrate only when both choose
`Solid`; collision geometry, observation, targeting, and interactions do not
silently change with that choice. The deterministic sweep-and-prune
broadphase's potential, narrowphase, and resolved pair counts are included in
`world.contacts` so crowd cost and behaviour are observable on the real path.
The fixed collider vocabulary and generic contact geometry live in
`Puck.Physics`; World retains document compilation, authority, pair selection,
grounding/walkability, obstruction reporting, and body-state writes.

A kit carrying a `rigid` facet is a passive physical entity—a billiard ball,
a bowling pin, a domino—advanced by the rigid solver instead of a
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
operand reads. The shipped world's `billiardsTray`/`bowlingLane`/`dominoRun`
placements are the garden's proof fixture—see the
[server reference](../Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs)
for the mechanics and the [schema reference](../Puck.World.Schema/README.md#rigid-dynamics-worldrigidcs)
for the authored facet.

Each tabletop game—poker, dominoes, billiards, bowling, tic-tac-toe,
hex lines, mancala, the solitaires—lives in its own file under `Assets/worlds/games/`, imported bare by
`puck.world.json` (`WorldDefinition.Imports`; see the
[`puck-world` skill's documents reference](../../.claude/skills/puck-world/references/documents-composition.md#document-composition-basis-and-imports)),
and every district lives under `Assets/worlds/modules/`, imported under its alias. The island restates
each game's root placement row onto the market hall's floor (`parent: marketCourt`) and each district's
`<alias>Court` row where the district stands, so a module's own rows never carry island coordinates. The
shared substrate—channels, kits, population capacity, spawn points, the island's own ground, hud/views,
and everything no module's content touches—stays in `puck.world.json` itself; `world.imports` reads the
resolved stack back. A module addresses the world through placements, never
absolute coordinates: dominoes, billiards, and bowling anchor to marker
placements of their own, so a module composes into any host that declares the
placement it names. Hex lines (`hexlines.puck`)
is a radius-4 hexagonal disk of 61 pointy-top tiles on `hexTable` with two stone
trays, its `hexLinesBoard` topology unanchored today (a `board` facet admits only
a Grid topology, so a host restating `hexTable` restates the topology's origin
beside it) and no rules yet; its cells follow one convention: cell `(Q, R)` is a
`HexagonalCoordinate` in the Eisenstein basis, its centre on the board's XZ plane
at the origin plus `cellSize · (Q − R/2, 0, R·√3/2)` (so +X is the direction-0
neighbour and `cellSize` is the centre-to-centre spacing), and cell index `i` is
`HexagonalIndex`'s ring order.

A kit carrying a `carry` facet may pick up another kit's rigid body:
`body.carry <carrier> <target>` begins it (within an authored reach and mass
ceiling, both scaling with the carrier's own live `Scale`), `body.release
[carrier]` ends it; a carried body's own rigid integration is suspended, but
its pose is not an unconditional follow—its own collider sweeps against
static geometry and every other active body every tick, so it pushes and is
blocked rather than passing through, and whatever correction that sweep
applies is handed back to the carrier too, so the carrier itself is stopped
by what it is holding. `body.release` refuses by name when the carried
body's current pose still overlaps geometry or another body. A released body
re-enters the solver with the carrier's own velocity. `body.where` echoes
`carrying=`/`carriedBy=` while the relationship holds. The garden's `walker`
kit (Wren) is the worked example—see the [server reference](../Puck.World.Server/README.md#carry-as-attachment-worldbodycarrycs-worldpopulationcarrycs).

The garden's hound witness rules read body IDs from its own `houndIdentity`
row. Its six cells correspond to the `hound` carrier's keys (90-95), and
`boneHolder` and `reporter` admit the garden's full body-index range. This
identity data belongs to the garden rather than an imported game.

Physical chess is not part of the shipped island: it is the Parlor package's
[`chess.puck`](../../worlds/parlor/chess.puck), whose
[README](../../worlds/parlor/README.md) covers play, its computer opponent, and
verification. Its board uses the tabletop primitive (see the
[schema reference](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)),
and `world.tabletop` reads the board's frame, observation, and bound rows.

The garden also carries a hidden-hand poker table, state only, no card
bodies: heads-up fixed-limit hold'em, authored in
`games/poker.puck`. A `cards` token domain (52 identities) carries
`rank` and `suit` attribute rows, each declaring a `keysOf: cards` domain so a
hidden card's value inherits its owning zone's own visibility (see below) and
an explicit `capacity: 52`, since a `keysOf` row that authors no capacity is
priced at the 4096-cell row ceiling by every transform and sweep that
addresses it. Beside them sit the
`deck`/`hand1`/`hand2`/`community` zone family, drawn through the
`cardStream` streamDraw site, and the scalars, which live in keyed rows rather
than one 128-cell slot row each: `phase` (`street`, `next`), `table` (button,
bettor, pot, acted, raises, winner, hands, dealRequest, evalPending,
boardMask, refused, leak), `house` (the tunables: smallBlind, bigBlind,
maxRaises, autoDeal; a tunable is a document field, never a constant),
`stack` and `streetBet` keyed by seat, `betAction1`/`betAction2` (one `act`
cell each: -1 idle, 0 check or call, 1 bet or raise, 2 fold; two rows only so
each seat's row-scoped `Edit` grant covers its own), a `bets` history ring
logging every accepted action as `street * 100 + seat * 10 + action`, and
`private1`/`private2` (the seat's hole mask and strength word, readable by
that seat and by whoever its `audience` row names).

`phase.street` runs 0 idle, 1 preflop, 2 flop, 3 turn, 4 river, 5 showdown,
6 collect, and exactly ONE rule writes it: `poker-transition`, declared
first, copies `phase.next` whenever the two differ. Every other rule requests
a change by writing `next` and gates on `street`, which is what lets
`world.budget`'s exclusion trie price the deal, the three street deals, and
the three collect transfers as alternatives (one writer of the pinned cell
admits the two costliest street values, never all eight transforms summed) at
the cost of one tick of latency per transition, which a turn-based table never
notices; the row is named `phase` because the trie orders pinned cells by
their `row.key` spelling and the street must lead every rule's pin set.
`poker-deal` (street 0, `table.dealRequest`, both stacks covering the big
blind) is one `transaction`: two random two-card transfers off the deck, the
button passing to the other seat, the blinds moved from the stacks into the
pot, the bettor set to the button (heads-up, the button posts the small blind
and acts first preflop), the masks and strength words zeroed, `evalPending`
raised, `next = 1`; the two audience resets are text writes after it, since a
transaction step carries no text. `poker-check-call`, `poker-raise`, and
`poker-fold` are one rule each for BOTH seats: a `compareValue` gate reads the
acting seat's pending action through the turn cell
(`table[bettor] == 1 ? betAction1[act] : betAction2[act]`) and the bettor's
own `stack`/`streetBet` cell is written through the expression key
`$expr:table[bettor]`, so there is no per-seat rule pair. A raise pays the
call plus one big blind (two on the turn and river) and is refused past
`house.maxRaises`; a call or raise the stack cannot cover is refused too.
`poker-street-complete` (both seats acted, street bets equal) clears the
street and hands the action to the non-button seat; `poker-flop`/`-turn`/
`-river` deal 3/1/1 community cards when the street and the community count
say so, each raising `evalPending`; `poker-showdown` (street 5) widens both
`audience` rows, compares the two strength words, and awards the pot (a tie
splits it, the odd chip to the button); the `poker-collect-*` rules return
every card to the deck one per tick at street 6 and `poker-hand-over` idles
the table, re-raising `dealRequest` when `house.autoDeal` is set.
`poker-discard`, declared last, clears and counts (`table.refused`) whatever
action no handler accepted this tick (out of turn, outside a betting street,
unaffordable, over the raise cap), so a pending action can never wait for a
turn it was not submitted on. `poker-card-conservation` is the authored
"every card is in exactly one zone" invariant (`table.leak`), one
`compareValue` gate summing `$reduce:count:` over the zones against the
domain row's own count.

Hand strength is derived by the rules from one word, not read from patterns:
`poker-see-hand1`/`-hand2`/`-board` (`forEach` over the zones, gated on
`evalPending`) fold each card into a 64-bit mask at bit
`16 * (suit - 1) + (rank - 1)`, four 16-bit suit lanes with ranks 2..14 at
lane bits 1..13 and lane bit 0 reserved for the ace read low, and
`poker-evaluate-1`/`-2` rank `hole | boardMask` in sixteen bindings: the four
lanes (`bitField`), their union (the ranks present), the pairwise and
triple-wise lane intersections (the ranks held twice, three times, four
times), the straight and straight-flush runs (five shifted ANDs over the
wheel-augmented word), the flush lane (`setBitCount >= 5`), then the category
and its tiebreakers. The strength word is
`category << 28 | primary << 14 | secondary`; within one category the
tiebreakers are either rank masks (integer order is lexicographic by highest
rank; the top-k of a mask is
`x ^ parallelBitDeposit((1 << (setBitCount(x) - k)) - 1, x)`) or one
highest-rank index, never mixed, so the plain integer comparison the showdown
makes IS the poker comparison. No sort, no per-rank pattern row, no scratch
copy of the hand, and a live rank on every street for the seat that may see
it. `tests/Puck.World.Tests/PokerHandStrengthLawTests.cs` feeds authored
seven-card hands through the shipped rules on a real server and pins the
exact word for every category, the near-miss controls, the ordering, a full
hand to showdown (chip and card conservation, the reveal, the collection back
to a 52-card deck), and a fold with an out-of-turn discard. From the console:
`world.state.cell.set table dealRequest 1`, then
`world.state.cell.set betAction1 act 0` (or `betAction2`) as the bettor,
`world.state table`/`phase`/`stack`/`bets` to read back, and
`world.observe seat2` (see the [console
reference](../../.claude/skills/puck-world/references/console.md)) to inspect
what one seat may see without submitting as it: before the showdown the
other seat's hand and `private` row are absent and `rank`/`suit` show fifty
placeholders. Hidden cards are placeholders through `rank`/`suit`'s own
public, `Hidden: Placeholder` visibility: each cell resolves through its
OWNING zone's own visibility (`WorldStateDisclosure.Observer.CanRead`'s
nested zones-by-domain lookup, which is *why* the `keysOf` domain cannot be
dropped from either row), `deck` is authority-only, and `hand1`/`hand2` are
each their own seat's direct, whole-row read of its own two cards (the
row's `StateVisibility` names that seat in `readers` and widens through its
`readersFrom` audience row at the showdown; see the [Schema
reference](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)),
which is why a hand never authors its own `Placeholder` policy.

The garden also carries a 4x4x4 tic-tac-toe cube, state only—the
`Box`-topology worked example the schema's own discrete-boards section names.
`tttCube` is a `Box` `state.lattices` topology (width/depth/layers all 4,
64 cells); `tttBoard` is a plain `cellsOf: tttCube` int row (0 empty, 1/2 the
two marks). A move is two console writes plus a request bump—
`world.state.cell.set tttMoveCell $value <0..63>` then
`world.state.cell.set tttMoveRequest $value 1 add`—resolved by
`ttt-place-mark` (gated on the cell naming an empty board cell and no winner
yet; `$cell:tttMoveCell:$value` is the dynamic-key indirection that turns the
authored cell index into `tttBoard`'s own write address), which places the
mark, advances `tttMoveCount`/`tttBoardVersion`, and toggles `tttActive`.
`ttt-reject-out-of-range`/`ttt-reject-illegal` resync `tttMoveApplied` without
touching the board when the request names an out-of-bounds or already-occupied
cell, so a bad request never sticks. `ttt-check-win` (gated on
`tttBoardVersion` having advanced) folds all 76 four-in-a-row lines—each a
literal 64-bit cell mask, `(occupancyMask & lineMask) == lineMask`—through
eight-line chunks into `tttWinner` (0 none, 1 X, 2 O, 3 draw at 64
moves). `world.state tttBoard`/`tttWinner` is the read-back; there is no
dedicated verb, since the generic one already answers it.

The garden's [Solitaire collection](Assets/worlds/games/README.md) adds Klondike
draw-one/draw-three, Spider with one/two/four suits, and FreeCell through authored
state and rules. Its guide covers table selection, card identities, pile IDs,
and the console request protocol.

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
inhabited, or attached placement carries it through the replay stamp pool—the
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
glyph-decal tier off the same packed font atlas—a fixed monospace cell grid
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
document), a **source** (the signal it carries—a `WorldScreenSource`: none, a
registered image producer, a booted machine's named output, a jumbotron view of
this same world through a placeable `WorldCamera`, a probe's output, a
remote-session projection, or decal-rendered reading text), and a **route**
(whether a player may engage it, and within what radius).

### Image producers

A `producer` source names a registered image producer by id and hands it a
settings object: `{ "$type": "producer", "id": "qr", "settings": { "payload":
"…" } }`. The document model knows no producer by kind, so adding an emulator
picture unit, a capture API or a video decoder is a registration, not a schema
change. A producer registers twice, once per half:

- its document shape, a `WorldImageProducerShape` in
  `WorldImageProducerVocabulary` (`Puck.World.Schema`), which states its content
  class and transport and checks its settings wherever a document validates.
  The validator refuses an id no shape is registered under, and a settings
  member the shape does not declare, by name.
- its runtime, an `IWorldImageProducer` in `WorldImageProducers`
  (`Puck.World.Client`), which opens an `IWorldImageFeed` for each screen that
  names it. A registration is refused unless its id, content class and
  transport match the shape's. The binder registers the shipped four; a host
  adds its own as `IWorldImageProducer` services.

The engine ships four producers, each with its settings record in
`WorldImageProducerSettings`:

| Id | Settings | Transport | Content class |
|---|---|---|---|
| `testPattern` | `width`, `height` | uploaded | deterministic |
| `qr` | `payload`, `ecLevel` (`M`), `quietZoneModules` (4) | uploaded | deterministic |
| `camera` | `sensor` (`Color`), `seat`, `profile`, `controls` | imported | external |
| `capture` | `windowTitle` or `monitorIndex`, `profile` | imported (Direct3D 12) or uploaded (Vulkan) | external |

Every feed carries an `ImageSourceDescriptor` (`Puck.Abstractions.Sources`),
the one contract for an image entering rendering from outside a pass: its
producer, transport, extent, pixel format, color encoding, cadence, stamp and
content class. A feed whose content is external is the slot's live feed, which
`screen.eject` clears. Any other feed is its declared feed, which survives an
eject. A deterministic feed states the exact image it shows
(`IImageSourceReference`), which the exact verdict `ImageSourceVerdict`
compares a read-back against.

**External content never reaches a capture.** Every external image, whether a
camera, a desktop capture or a probe's output, resolves through
`WorldCaptureGate`. While the gate fills, the image resolves to its declared
capture fill (`ImageSourceDescriptor.CaptureFill`, opaque `#202020` by
default), a 1×1 upload, and the producer's frame is never acquired. The gate
covers screen slots, jumbotron renders of those screens, and HUD `Frame`
elements. An offscreen host, which serves scheduled captures and `puck parity`,
fills every frame. A windowed host fills from the frame a `world.screenshot`
is armed for until two frames after it. On the first frame it fills with an
external source bound, the jumbotron views render again, so no view shows an
image it rendered from that source before; a view beyond that frame's
offscreen budget (`OffscreenRenderBudget`) waits for its round-robin turn and
may still show its older image. Simulation never
reads the gate, and no external pixel reaches simulation state, a replay or the
state hash.

The machine, view, probe and session arms stay typed, because each names a row
of the document (a machine instance, a camera, a probe, a destination). A new
emulator joins as a machine engine in `WorldMachineCatalog`, again with no
schema change. Text is a decal, not an image.

A route's `input` names where a pointer hit on the screen's source goes:
`Presentation` (the default, hover and highlight) or `Simulation`, where a
pointer ray arriving as `source.pointer.origin`/`source.pointer.direction`
command values is mapped in fixed point from the row alone
(`WorldScreenMappings.Of`, the whole source inset by the glass bezel). The
validator refuses `Passthrough` by name, because only a source the local user
opened may send input to a host window. `world.screens` echoes each screen's
destination as `input:<destination>`. The mapping and its laws are described in
[the rendering plan's P13](../../docs/plans/rendering.md#p13--hit-to-source-mapping-and-input-destinations);
nothing reads the ray from a live pointer yet.

**A physical camera is an input device, seated like a gamepad, never named by
hardware.** Each enumerated device gets a reconnect-stable `InputDeviceId`
(`InputDeviceId.FromKey` over the platform's device id) and a roster token
(`camera<N>` by first-seen order, beside `keyboard1`/`gamepad<N>`)—
`world.devices` lists every device and the seat it drives; `player.assign
camera<N> <slot>` moves one between occupied seats, but refuses an empty target
because a passive sensor cannot create player presence. A newly seen camera
attaches to the lowest occupied, camera-less slot by default (seat 1 first); it
never creates a seat. A `camera` frame source (a `screens` row, a
probe socket, a HUD `Frame` element) names a **seat**, never a device: `{
"$type": "producer", "id": "camera", "settings": { "sensor": "Color", "seat": 2
} }`; `seat` absent means the
enclosing seat scope (an identity's own HUD panel, a seat-scoped probe socket)
or seat 1 at world scope. `WorldScreenBinder` resolves `(seat, sensor)` to a
live feed every frame (`roster.TryGetSeatDevice` → the device → its sensor's
feed). Physical-device discovery runs on a worker with at most one scan in
flight; the renderer adopts completed snapshots and retires vanished devices
without waiting for discovery. Another scan becomes due two seconds after the
previous result was consumed. A seat with no camera, or whose camera lacks the
requested sensor, reports that fault through `screen.state`/`screen.camera` rather than
refusing the bind—reassigning a camera moves every consumer to the new
device with no reopen, on the next produced frame. A scan that fails (the
platform enumeration call throws) is never read as "every camera unplugged"—
the device table is left untouched and removals resume only once a scan
completes again, and the failure narrates once per failure episode on stderr
rather than on every retry.

Camera demand—which (seat, sensor) pairs need an open feed, at what profile
—is a live set, fully recomputed every produced frame from the actual
consumers: camera-bound screen slots (a slot's camera feed names its seat and
sensor), retained probe sockets, and retained HUD `Frame`
elements naming a camera; the richest requested profile wins when more than
one consumer names the same pair. Nothing is declared once and remembered—
`screen.source <i> camera …` moving away from a camera, `screen.eject`, a HUD
panel losing its `Frame` element, and `player.assign` reseating a device all
resolve within the same one-publish seam: the next produced frame's demand
recompute drops what is no longer wanted and picks up what changed, closing a
device's graph once none of its feeds are demanded any more (lazily reopened
on the next demand, exactly like the first open).

**A booted MACHINE is authoritative server state, not presentation-fed.** `Puck.World.Server.WorldMachineHost` owns
boot, exact-tick advancement, links, and hardware access for named machines in
every boot shape, including headless. Screens and speakers consume their named
outputs. `machine.operation` carries expected generation and named-machine Control
authority; `screen.insert` and `forge.play` use that executor for named producers,
while `screen.eject` detaches the display. Legacy screen operations remain in the
protocol. Generic provider operations are refused during recording until the tape
can capture their execution. `WorldScreenBinder.cs` reads a machine output's
framebuffer handle and light and calls
`IMachineVideoOutput.PublishFrame` each produced frame—the one GPU call this
project still makes on a machine's behalf. The machines outlive the render
device, so the binder that published an output's upload also retires it: on
device loss and when the render chain is torn down, it calls
`IMachineVideoOutput.NotifyDeviceLost` for every output it has published on
that device (`PublishedMachineOutputs`, which records an output before it
publishes, so a publish that loses the device still retires its upload).
Device loss also retires every probe's shared ring, keeping its request so the
next publish provisions a fresh one, and the Vulkan host's headless camera
device is disposed only after the last image made on it is released
(`DisposeAfterDependents`), however late a submitted frame's lease releases
it. It recreates its own slot for a
screen index removed and later restored by `world.reset`/`.load` exactly as
`WorldMachineHost` does (bounded to indices the render engine's boot-frozen
provider key set already names). A recreate re-points a `ScreenSourceCell`'s
`Slot` field rather than writing a fresh delegate into the engine's
provider maps—`SdfEngineNode` copies those maps' delegates ONCE, at
construction, and never re-reads this binder's own dictionaries again, so
only the cell indirection (never a brand-new delegate) is visible to an
already-running renderer after a remove+reset. It still OWNS the genuinely presentation
sources—every producer feed and every jumbotron view—bound through `screen.source <index> <kind>`
(`camera`, `capture`, `desktop`, `probe`, `view`, `qr`; it ejects a
present machine first, through the ordered domain) and `screen.eject` (which
routes to whichever half—machine or
local producer—actually holds the slot). A camera source row picks its
`sensor` (`color` default, or `infrared`—its own shared feed; two-sensor
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
given SEAT wins regardless of sensor—two seats' cameras carry independent
states; the standard
UVC pan/tilt/zoom/exposure/focus/color surface plus the vendor-extension
`fieldOfView` in degrees and raw `vendor` selector/value rows,
`WorldCameraControls`): the values land on the physical device once its
stream is live (vendor-extension writes are firmware-ignored on an idle
filter), an `UpsertScreen` mutation (`world.row.set screens …`) moves the
device live through the ordered domain, and `screen.camera` lists every known
device (token, name, sensors, tier, and the seat it drives) with each live
sensor's own section—negotiated extent, native transport subtype/rate,
coordinated capture mode, device range, mode, current value, the resolved
seat's authored value, and raw vendor read-backs—over the pipe (the device
stays authoritative: values clamp to its reported envelope, members never authored
leave driver defaults untouched, and removing an applied member restores its
default). A QR is the one source with no
per-frame cost at all: `QrEncoder` resolves the module grid and `WorldQrFeed`
rasterizes it once at author time, then re-uploads the unchanged buffer only
after a device loss. `world.identify <screenIndex>
[ecLevel]` (`WorldIdentifyCommandModule`) is a composition over that same live
path rather than a source of its own: it mints the RUNNING world's identity—
`puck:world/<documentId>?schema=<schema>&hash=sha256-64/<hex>`—and hands it
to `WorldScreenBinder.TryQr`, so a phone pointed at a live session carries the
world's identity away. The hash is recomputed from the LIVE definition's
canonical bytes on every invocation (mutations included), never read from the
boot-time load pin, so the code never claims an identity the running world no
longer has; the echo says `hash-covers=live-definition` and carries the payload
in full. It is deterministic in the definition alone—same document, same
payload, every run. An
unbound slot gets the engine's no-signal card, and a missing device is loud
data in `world.screens`/`screen.state`, never a crash. A machine screen is
engine-neutral (`Puck.Abstractions.Machines`): `WorldBootComposition`
registers the SM83 family (`gaming-brick`) and the ARM7TDMI machine
(`advanced-gaming-brick`) onto `WorldMachineHost`, and `body.engage`
composes a control application onto the machine—the same `PlayerIntent`
currency, translated once into a neutral pad image through the named kit's
`pad` map, folded server-side (`WorldEngagement.FoldTick`) and read directly by
`WorldMachineHost.Advance` inside `WorldServer.Step`; the authority to compose
rides the grant table's `Control` capability. See [`Puck.World.Server`](../Puck.World.Server/README.md) for the full contract.

A THIRD registered engine, `tune-instrument`
(`Puck.HumbleGamingBrick.Forge.Tune.TuneInstrumentEngine`), is a diegetic, player-operated
instrument: its content is a `puck.tune.v1` document rather than a
cartridge ROM, compiled to the same jukebox cart `Audio/TuneMachineSource.cs`
plays passively and booted on a real `Puck.HumbleGamingBrick.MachineHost`, so
`body.engage` reaches it exactly like any other screen machine. While a seat
holds the application, `WorldServer.InstrumentClockBoundary` folds the
instrument's own authored tempo into the world's `MusicClock` boundary each
tick—holding the application is the whole gate, and there is deliberately no
session lever beside it (`WorldSessionLever`'s own remarks: a knob the
simulation reads is a mutation, not a lever). `instrument.state` reads which screen (if any) the routed seat is engaged
with, whether it carries the capability, and its tempo. See
[`Audio/README.md`](Audio/README.md) for the instrument host itself.

A placeable camera's offscreen view can also be EXPORTED—read as a GPU
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
Export carries exactly ONE physical image (the engine's own persistent
output, re-rendered in place every refresh) with no second buffer to rotate
into. A shared lease admits concurrent readers of the completed image and
defers the next writer until all of them retire; export submission drains the
producer queue before publishing that image as readable.

## HUD frame elements

A HUD `Frame` element (`WorldHudElementKind.Frame`) shows a live frame inside
the banded overlay—the same `WorldFrameSource` vocabulary a screen samples (a
`producer`, a `view`, a `probe`), never a pipeline of its own; the camera and
capture producers are the ones it shows.
`WorldScreenBinder.DeclareFrameSource`/`TryAcquireFrame` take an explicit
enclosing SEAT (the owning identity panel's slot for a player-scope panel,
or seat 1 for a world-scope one) alongside the source, since a bare `camera`
source (no authored `seat`) means "this panel's own seat"—the seat argument
is what resolves that, not a value baked into the source record. They are the
registry every non-screen consumer of a `WorldFrameSource` shares: the former
opens a non-camera producer's underlying feed the first time anything asks
for it (idempotent—a view renders every `ViewStack` refresh with no wired
screen narrowing its round-robin turn, a probe reads whatever its own kernel
publishes, a capture opens through the same ladder a declared screen's capture
source uses); a `camera` source declares nothing here at all—it instead
rides `RetainFrameSource`/`ReleaseFrameSource`'s reference-counted table,
whose membership is one of the inputs `ReconcileCameraDemand`
(`WorldScreenBinder.FrameSources.cs`) recomputes camera demand from every
produced frame, alongside every camera-bound screen slot and retained probe
socket—see the seated-camera section above; the latter (`TryAcquireFrame`)
reads the current frame, render-thread-side, once per produced frame. `WorldOverlayFrameSources` (`Puck.Overlays.IOverlayFrameSources`)
is the integer-keyed adapter the compositor addresses: `WorldHudFeed.BuildPanel`
resolves each of a `Frame` element's ranked source candidates to a key on the structure rebuild (world
panels at seat 1, a joined seat's own panel at its own seat), and the
compositor calls `TryAcquire` each produced frame to bind the element's
overlay slot. Two elements naming an identical (source, seat) pair share one
key, one feed, and one slot—keying on the pair, not the source alone, is
what keeps two seats' otherwise-identical bare camera panels (both authoring
no `seat`) from collapsing onto the same feed. A `portrait` panel—a mirrored,
rounded picture-in-picture of the color camera—is the shape such an element
takes.

## Native capture

The world records itself to WebM/Matroska through the recording graph in
`Puck.Recording` (`puck.recording.configuration.v1`—resolved at boot from `--recording`
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
declare a registered `puck.probe.manifest.v1` kind and plug one `WorldFrameSource` into
each of the kind's typed sockets (`inputs`, by socket name)—or, in place of
every socket at once, a recorded `puck.probe.track.v1` track—and carry the
bindings that route one of its channels to a command axis (a `probe.<name>`
source, an ordinary bindable stick-like input any binding overlay may map), a
presentation float (a `render.extensions` config field, or another probe's
kind config field—patched live into its running kernel), or the existing
camera control surface. A socket's class is `frame` (any one frame source) or
`strobePair` (a strobing infrared sensor's lit frame and the unlit frame kept
before it—bound only to a `camera` source with sensor Infrared); an
`optional` socket may be left unbound (a null input to the kernel). A socket
source is the same `WorldFrameSource` vocabulary a screen samples: the
`camera` producer (a declared sensor and seat—`seat` absent means the enclosing
instance's own seat, the same convention a `screens` row and a HUD `Frame`
element follow; every camera socket in one probe must resolve to the same seat,
because one kernel run has one host graph; `profile` is honored while source
`controls` are refused in favor of probe control bindings or camera-screen
authoring), `view` (a named `cameras[]` row's offscreen render, exported as a
kernel-readable lease that holds the last complete image while a kernel reads),
`probe` (another declared probe's
own texture output, read back as a ring). Any other producer is part of the
shared frame-source vocabulary but is refused on a probe socket until a kernel
input host exists for it. The kind's `trigger`
socket must bind a `camera` producer source: kernels run on that sensor's own camera
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
trigger sensor's open graph whenever any socket's generation—or the output
ring's—changes; a socket whose source is not ready yet (an unpublished ring,
an unopened camera) idles the whole probe with that fault and retries every
frame until it resolves. A camera socket retains its own (seat, sensor,
profile) demand while its instance lives, then releases it on retirement, the
same ownership shape a visible HUD `Frame` element uses—no `screens` row
need ever name the sensor for a probe to read it. A probe is not a device and
never occupies a seat: an axis binding addresses its own instance's seat's
lane directly (`InputSignal.Slot`), its `probe:<seat>` device id is only the
router's held-state key, it never counts as player activity, and it loses its
carried sample whenever the terminal takes focus, exactly as a gamepad does.

A row is seat-relative when at least one of its camera sockets carries no
`seat` of its own—it is then instanced once per occupied local seat, each
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
at document load—it always takes its instance's); one declared on a
single-instance row still authors `seat` as before (absent defaults to seat
1). A `probe`-target parameter binding or a `probe` socket naming a
seat-relative probe resolves to the enclosing instance's own seat's target
instance, or the single instance when the target is not seat-relative.
Shipped kinds are
the lit-frame blob centroid `ir-blob` (bright-mass centroid/coverage/mean
luminance of the above-threshold pixels over the FaceAuth infrared stream)
and `faerie` (relights the color frame from a light orbiting an authored
anchor, with the infrared strobe pair's lit-minus-unlit response as the
height field; see `src/Puck.Shaders/README.md`)—GPU-tier only today.

`probe.status` echoes every live instance's run state (or fault), tier, rate,
cycles/drops, latest capture age, channel values and confidence, every
binding's conditioned value and write count, and (for a camera socket) the
resolved device token (`camera<N>`, or `seat<N>-unassigned` when the socket's
seat carries no camera)—a query, always echoing even under `wire.ack quiet`.
A seat-relative row's instance is listed as `<id>$<seat>`, a generated name no probe id may spell; a single-instance
row's is listed by its bare `<id>`. `probe.record <probe>[$<seat>] <path>
<seconds>` arms a live recording of one instance's fresh readings to a
`puck.probe.track.v1` document, sampled once per host frame (an instance
faster than the host frame rate records its latest reading per frame); each
sample carries its own capture time, and playback follows those times, so
completion narrates on stderr with the sample count and the recorded cadence
replays as recorded. The `$<seat>` suffix is required to name one instance of
a seat-relative row (omitting it is refused as ambiguous, naming the live
instances) and optional on a single-instance row. The recorded document plugs
into a track-input probe in place of a live device—the hardware-free proof
leg every probe admits. `probe.set <probe>[$<seat>] <field> <value>` patches
one float config field of a declared probe's kind live—the same constants
write a `probe`-target parameter binding performs, bound only by the field's
own declared range; a parameter binding targeting the same field overwrites a
`probe.set` write on its own next changed reading. With no `$<seat>` suffix a
single-instance row's one instance is written and a seat-relative row's every
live instance is written; with a suffix, only that one instance.

No shipped world declares a probe. The checked-in track
`Assets/probes/tracks/brio-head.probe-track.json` and the verbs above are the
probe surface. A district that needs a camera reading authors its `probes` rows
on the one world and proves them with `probe.status`, `body.channels`, and
`wire.errors`; the kernel's own numbers are pinned hardware-free by
`tests/Puck.Platform.Windows.Tests/ProbeKernelTests.cs`.

## Graphics options

All render levers are live verbs with no-arg echoes of the current value:
`world.quality`, `world.shadows`, `world.ao`, `world.render-scale`,
`world.upscale-sharpness`, `world.target`, `world.shadow-mask`,
`world.shadow-march`, `world.ao-quality`, `world.view-refresh`,
`world.debug-view`, `world.fps`. Render scale applies
to both seat views and named cameras, multiplied by any layout-transition scale.
Named tiers are
facades over continuous values. Do not assume a lower render scale is
monotonic for a large instance field—read both `world.counters gpu` and
`world.fps` at the intended population and view layout. `world.budget` is the DERIVED cost
sheet, not a lever: the live render program's packed words/instances against
their frozen envelopes, the Lipschitz step scale and march multiplier, the far
distance with its reach multiplier, horizon-ray step tax, and far-plane fog
remnant, the field lattice program's node/cadence counts and exact
full-cell/body-slot pass costs, and the state row count—
how an authored choice's price becomes legible instead of a silent frame tax.
Navigation adds its compiled cell count, fixed A*/shared-tree workspace bytes,
authored search caps, live follower count, and this tick's expansions to that
sheet, plus the simultaneous-replan ceiling across current followers. Shared
domains contribute their aggregate per-tick budget once, not once per follower.
`world.navigation` lists each surface/volume/medium domain and
`body.targets <body>` includes the selected route's status and waypoint. Both
`world.navigation` and `world.budget` are server-safe under `--headless`;
the latter names the absent renderer while retaining all authoritative costs.

`world.counters [<source-or-prefix>] [--json]` is the one work-counter
readout, registered in every host shape. It discovers every
`IWorkCounterSource` registered in the World's container and prints one section
per source, sorted by name: the source's dotted name, then a `<kind> <value>`
line per kind it counts. The boot server registers its own sources:
`state.arena` and `state.search`, whose totals carry across a definition
rebuild (`world.reload`, `world.load`, `world.reset`) that replaces the arena
and search behind them, so a reading never goes down, and `state.rules`. A
rendering shape adds the shader compiler's `shaders.compiler` (requests, cache
hits and each native tool's runs) and the process's shader loads,
`shaders.sdf-kernels` and `shaders.set-manifest`
(loads and the bytecode bytes they read); each backend adds its
`pipeline-cache.<backend>` and `memory.<backend>` (device-local bytes
allocated and released at their allocation sizes, and the peak held; swapchain
images are never counted), and Vulkan adds `procedures.vulkan`. A rendering
shape also registers `sdf.bakes`: the creation bakes its cache held, scheduled,
baked and refused, and the field evaluations the bakes spent. The
client registers `presentation.mirror`, the cells its state mirror read. A
presented host registers `sdf.transforms`: the dynamic-transform rows packed,
the bytes compared and the rows owed, summed over the main frame source and the
frame source each session view composes for itself. A released session view's
totals stay in the sum. The
`allocation` section names the GC mode and the fewest managed bytes one read of
every count allocated (`world.counters.read`, measured with
`AllocationWindow.Measure`); only zero or not zero is meaningful. A render host
adds the `gpu` section. Its header names
the device as its backend reported it at creation — `backend`, `adapter`, PCI
`vendor` and `device`, the driver version as displayed (`driver`) and as
reported (`driver.raw`), `api`, and on Vulkan `driver.name`, `driver.id`,
`conformance` and `pipeline-cache.uuid` — or says `device unavailable` before the device is brought up.
The identity is recorded, never branched on. Once the device is up, a
`capabilities` line (and a `capabilities` object under `--json`) records what
it can bind: `descriptor-sets` or `root-signature-words`,
`push-constant-bytes`, the `stage.*` descriptor limits, and on Direct3D 12
`binding-tier`, `root-signature`, `shader-model`, `heap.views`,
`heap.samplers` and `heap.samplers-static`; it is recorded the same way. Then come each render node
(`world` for the SDF engine, then every render-graph instance by its instance
name, such as the root `main` and each `views.graphs` pane, then
`view:<name>` for each offscreen view) with its newest
completed submission's per-pass counts and its created objects, or
`work unavailable` until a submission completes. A filter selects whole dotted
segments (`world.counters gpu`, `world.counters state`); a filter that selects
nothing is refused and lists the sources. `--json` prints one line:
`{"sources":[{"name":…,"counts":{"<kind>":<value>}}],"gpu":{"device":{…},"nodes":[…]},"allocation":{"gcMode":…,"windows":{…}},"kinds":{"<kind>":{"unit":…,"class":…}}}`.
The `kinds` legend gives every reported kind's unit and class
(`deterministic`, `per-backend-deterministic` or `pacing`), which
`puck counters` reads to tag each count.
A headless host has no `gpu` section. Counts only go up; read twice and
subtract for a window.

`world.sdf.dump <path>` exports the live packed GPU program as little-endian
32-bit words, replacing the destination file. Use it to inspect the instructions,
materials and acceleration tables actually uploaded, rather than the reserved
capacity. It requires an initialized renderer and does not change the world.
Dynamic transforms and the per-frame grid are separate buffers and are excluded.
The dump follows the current `SdfProgram` layout; it is a diagnostic snapshot,
not a loadable or durable asset format.

`render.farDistance` is the depth every camera march ends at (default 40 when
unauthored; 1..8192), re-read on every definition revision like the lighting
below—geometry beyond it is never marched, so an infinite ground plane shows
a horizon curve there unless the `render.sky` fog layer absorbs it first.

Two document sections author the scene's lighting instead of a verb, re-read
on every definition revision (a live edit lands on the next frame).
`render.lighting.lights[]` is a typed list, at most eight, each `$type`
`directional` (`direction`, `color`, `weight`, `angularRadius`, `shadows`—the
one shadowing light drives the soft-shadow march, whose penumbra is the tangent
of its angular radius; the rest are scaled by ambient occlusion), `hemisphere`
(`color`, `base`, `gradient`) or `rim` (`color`, `weight`, `power`, added after
the material shade); absent, the pinned sun and hemisphere render.
`render.lighting.curvature` adds cavity darkening, ridge light and an ink outline
read through the `inkLow`/`inkHigh` curvature band (1 / fillet radius).
`render.sky.layers[]` is a stack of `$type` `gradient` (two to four `stops` of
`elevation`/`color`), `fog`, `sunDisc` (bound to a light slot), `stars` (each
star hash-dealt its own blackbody colour and apparent luminosity;
`twinkle { share, depth, rate }` scintillates a share of them on the tick clock)
and `clouds` (`coverage, softness, scale, seed, color, drift, spin, curl, shear`
—a hashed, warped noise layer over everything above it, all on the tick clock),
composited in that order whatever order they are authored in. Every field is
optional individually. `world.lighting` echoes both sections. `render.cycle`
keys both over a state row: `{ "state": "timeOfDay", "keys": [ { "at": 0.25,
"lighting": {…}, "sky": {…} }, … ] }`—the row's live value (its fractional
part, so an advancing row wraps once per unit) picks the two bracketing keys
and every lighting/sky lane interpolates between them (directions along the
arc; counts, kinds, seeds and flags held); a key states only the fields it
moves, addresses a light by slot and a stop by index with the kinds the statics
author, and the rest hold from the previous key. The clock is simulation
state (an advancing `state` row—deterministic, replayed, settable with
`world.row.set state`); the interpolation is presentation.

## Engine boundaries worth knowing

- `SdfProgramBuilder.MaxInstances = 65536`: per-tile mask width scales with
  DECLARED instances, which is why this project emits active avatars only and
  probes capacity floors at construction.
- The exact soft-shadow gather scans the live program's instance range, up to
  the 65536-instance ceiling. Empty stamp capacity emits no live instances.
  Camera-tile masking is a separate approximation selected by the quality
  policy or `world.shadow-mask camera-tile`.
- `OffscreenRenderBudget.RegisteredViews = 64`: do not register a rendered view per
  population entry.
- XInput caps at 4 Xbox-family pads locally; HID pads are uncapped.

## Verifying

`Puck.World` is greenfield (`CLAUDE.md` rule 3): verify by RUNNING the game
and driving stdin verbs—no gate stages, no `--validate` flags, and no
golden corpus. Byte-identity observations (the canonical save round-trip,
`git diff` on shipped worlds) are useful evidence but never acceptance
criteria for feature work (if a shipped world's
JSON moves as a side effect of a landing, note it and move on; goldens become
worth building when the data settles).

A typical assertion session over a pipe:

```bash
printf 'world.status\nbody.where 0\nworld.grants console\n' |
  dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6
```

`world.screenshot <path.png>` is the cheap pixel assertion, but it REQUESTS a
capture of a following composed frame. A tick wait lets rendering progress;
confirm the capture completion before reading the file. Its stdout echo says `pending <path>`
precisely because no file exists yet; the resolved path arrives on **stderr**
when the frame lands, named by whichever node served it: `[capture] main ->
<path>` from the node of the render graph's root, which draws the
`render.extensions` passes and the overlay over the world, or `[debug] captured
frame N -> <path>` from the engine node when the world is the root because
nothing is drawn over it. The root reads the frame the display shows; an
overlay that draws nothing this frame publishes the world's image in its place,
and the capture reads that. Arming a second capture while one is still
pending is REFUSED by name—the earlier path would never be written—and a
request still outstanding when the run ends prints a `WARNING` naming it. A
scripted caller can therefore distinguish a reported write from an unserved
request. In-process callers receive a `FrameCaptureRequest` from
the render root (`RenderGraphRuntimeNode.RequestCapture`) and await its `Completion` for success or
failure. See [the render contract](../Puck.SdfVm/README.md#capture-completion).

Committed, re-runnable proofs cover most load-bearing seams as `puck canary`
manifests under `tests/Puck.World.Canaries/`—`sdf-decode-sign-refusal`
(all twelve builder-mirrored sign fields) and `world-seat-binding-recompose`
(a forced seat recompose against a registered command, cleanly, with no
binding-narration) among them—see the
[`puck canary` reference](../../docs/reference/cli.md#puck-canaryreal-world-behavioral-proofs)
for the `stream` override that lets a `world.grant` claim bind its
stderr-narrated confirmation. Strict-parse and mutation-all-or-nothing are
proved in-process by
`tests/Puck.World.Tests/{StrictParseLawTests,MutationAllOrNothingLawTests}.cs`,
and cited repository paths are checked by `puck docs links`.
`four-corners-sharded` is the stronger five-authority federation proof: four
ground worlds plus the floating island, each its own real process on its own
dynamic loopback endpoint, with one human-driven body ringing all four
ground authorities purely through the router that follows a body wherever it
now lives. Ordered-domain submission order, the headless boot, the HUD
document, and engagement dissolution have no committed battery at all—validate
them by running the app. Principal/grant enforcement and engage/disengage authority
are proved by `AuthorityAdministrationLawTests`, `EngageAuthorityLawTests`, and
`ControlApplicationLawTests` in `tests/Puck.World.Tests`.

The [discrete state contract](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
covers tabletop/card rules and turn-based tactics. `world.state.transform`
submits a closed atomic operation; `world.state.act <phase-row> <sequence>`
adds its phase guard. `world.topologies` reads topology declarations,
`world.state` reads authority state, and `world.state.observe` requests the
calling principal's explicitly disclosed literal observations.
`world.state.similar <row> <key> <table> [top]` ranks a vector table's cells
against a query vector by cosine similarity and dot product, reading through
the caller's visibility and writing nothing. The headless
[tabletop fixture](../../tests/Puck.World.Canaries/tabletop-state/fixture.world.json)
includes legal/blocked moves, ray flips, ordered card transfer, and replay.

[Decision policies](../Puck.World.Schema/README.md#decision-policies) let a world
rule select eligible actions by highest score or weighted chance, with authored
reconsideration, commitment, and interrupts. Inspect them with `world.decisions`;
`world.rules` identifies policy-bearing rules and `world.budget` includes their
worst-case work (`world.budget.rules` breaks the total down by rule). To
debug one rule, `world.rule.trace <rule>` captures its next evaluations—
bindings, each gate conjunct's values and verdict, each effect's value and
outcome—and prints them after a `world.wait`. The Schema reference includes a
complete rule example.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
