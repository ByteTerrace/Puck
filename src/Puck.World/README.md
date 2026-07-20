# Puck.World — the game, and the engine's crucible

Puck.World is the definitive game, and deliberately more than that: **it is the
crucible that defines what the engine must support.** When a requirement here
exceeds an engine capability, the engine grows (flagged, minimal, Post-gated) —
never the other way around. `Puck.Demo` remains the prototyping ground this
project supersedes; nothing here depends on it.

## The vision contract

| Requirement | Value | Status |
|---|---|---|
| Player-controllable instances per world | **up to 128** — tiers 16 (low) / 64 (medium) / 128 (high) | Built: `WorldPopulation.MaxPopulation = 128` |
| Locally controlled players | **up to 4** (quad viewports) | Built: `PlayerRoster.MaxSlots = 4` |
| Built-in population | **4 local humans + 124 network-human stand-ins** from boot | Quad split screen plus the full 128-avatar population is the default scene |
| Non-local drivers | network, AI, demo (replay), or any other system | The built-in stand-ins autonomously stage the definition's five kit rows (flyer, swimmer, jumper, runner, kart) |
| Total SDF instances per scene | **up to 16384** | Engine cap (`SdfProgramBuilder.MaxInstances`) |
| Resolution / refresh targets | desktop 2560×1440 @ 120 Hz; portable 1280×800 @ 60 Hz; VRR first-class on both | `world.target <hz>|display` (the host caps authored targets at the active physical-signal ceiling) |
| Reference scenarios | 4×4 = 16 (four Steam-Machine-class boxes, the ideal minimum) up to 1×128 / 2×64 / 4×32 | 4×4 tier holds full quality at the portable target today |
| Performance contract | **60 FPS steady-state floor** in the World live proof; 120 FPS remains the reference desktop target under VRR | `proof.cs run --min-fps 60` asserts the last rolling `world.fps` average and worst frame |

## Architecture in one breath

A **profile** (persisted identity + settings; local store, cloud-ready) seats at
a **local seat** (viewport + camera rig + device set — keyboard and pads are
peers; every seat is just a zero-latency networked player) whose authoritative
body lives in the server's **entity table** (up to 128 entries, each an avatar
the renderer draws identically regardless of driver). Input flows as one
currency — `PlayerIntent`
— produced by the doom-replay tape, held keys, analog sticks, network drivers,
or AI drivers. The console (stdin/stdout) is a
network-shaped control plane: every capability is a verb, so a piped script can
build, drive, measure, and assert a whole 128-player session with no hardware.
The built-in world boots the complete posture: four local-human seats in quad
split screen, playable from connected controllers, plus 124 autonomous stand-ins
representing network humans. The stand-ins are deterministically divided among
the definition's kit rows (flyer, swimmer, jumper, runner, kart), so the default scene carries
the mixed movement workload without first running a console corpus.

The population is no longer a repeated placeholder box. `WorldAvatarCatalog` deterministically authors a distinct
humanoid rig for every one of the 128 stable population slots: 12–20 independently culled animated leaves, or
60–100 VM instructions per avatar (`Reset + TransformDynamic + Translate + Rotate + Shape`). Puck.Maths R1/R2
low-discrepancy sequences distribute leaf counts, primitive types, dimensions, offsets, and rotations without RNG
state, modulo bands, or clone clumps. The full catalog is 2,041 dynamic leaf instances / 10,205 authored avatar
instructions. Movement distance advances each avatar's gait (idle holds its pose); arms counter-swing the opposite
leg. `world.population` reports the exact active leaf/instruction load so a capture names the workload it measured.
Each leaf owns a cull instance, so a tile touching one hand does not admit the rest of that humanoid—or its neighbors—
into every VM evaluation.

## The client/server boundary (the loopback)

The process is composed as ONE authoritative server plus ONE per-machine client
bound by an in-process loopback — the shape a socket transport slots into
without moving authority.

**Server** (`Puck.World.Server`, `Server/`): owns the `WorldDefinition`, the
128-body entity table (`WorldPopulation` of `WorldBody` — integration, pose,
tape, motion model, jump kit, warp/face/pose), the profile catalog + routed
store, the server-side intent producers (wander/idle for unclaimed peers), and
inbound validation. Per tick it consumes per-entity submitted intents and
authority commands, and produces a `WorldSnapshot` (per active entry: sim pose,
body color, kit row index, an `EntityContinuity` hint) delivered to the client sink.

**Client** (`Puck.World.Client`, `Client/`): owns seat metadata (`PlayerRoster`
— device set, profile selection, pending picker), the per-seat device-intent
producers (`SeatController` — held keys, sticks, live-held action lanes, the
possession-latch copy), the snapshot-fed entity view (`WorldClient` —
double-buffered tick poses, render interpolation, per-entity correction easers,
the `ISdfAnchorSource`), the frame source, screen binder + machines, engagement,
render settings, and the FPS witness. Poses flow IN via snapshots only; intents,
commands, and session requests flow OUT over the link.

**Protocol** (`Puck.World.Protocol`, `Protocol/`): the wire vocabulary.
`PlayerIntent`/`ActionLanes`/`MotionModel`/`IntentSource`
are the shared currency. Every write submission carries its acting
`WorldPrincipal` (a seat, the console/script surface, a WASM addon, or a
network/population peer — see **Principals and grants** below) checked
against the server's ONE capability table before it applies. `IntentSubmission`
(tick, entity, intent, principal, plus the device held-lane image) rides
`IServerLink.SubmitIntent`; the `WorldCommand` closed hierarchy (Teleport/
Face/EnqueueSegment/PressLane/SetMotion/SetControl/Reconcile/Stop), each
carrying its principal, rides `SubmitCommand`; `SessionRequest`
(Join/Leave/SetProfile/SetPopulation/SetPeerSource/SetPlayerSection) →
`SessionReply` rides `SubmitSession`; `WorldQuery`
(PlayerWhere/WorldPlayers/ScreenState/PlayerDocument) →
`QueryAnswer` rides `Query` (the client prints answers verbatim, so `player.where`
is byte-identical to the server's composition). `SetPlayerSection` is the
durable half of a profile edit (identity/motion/bindings/preferences) — see
**The player document + bindings** below. `IClientSink` carries
`DeliverSnapshot`/`DeliverAnswer`/`DeliverDefinition`. `SubmitDefinition`
(whole-document swap) and `SubmitWorldMutation` (one principal-carrying
`WorldMutation`) BUFFER on the server and drain at the tick boundary before
intents — the definition/editor/plugin wire is real; see **Moldable state**
below for the vocabulary, the journal/undo, and `world.load`, and
**Principals and grants** for `SubmitGrant`/`SubmitRevoke` (the two
capability-table writes that apply synchronously, like a command).
`SessionRequest.Join` carries a `ProtocolVersion` checked against
`WorldProtocol.Version`; a mismatch is rejected in the `SessionReply`
(`Accepted: false`, a distinct `Reason`) rather than silently admitted.

**The intent-source axis (`IntentSource`).** Every entity carries ONE
`IntentSource` — what fills its intent gaps between tape segments: `live` (the
submitted stream — a seat's device image or a remote client; the seat/boot
default), `idle` (nothing — gaps hold still), or `wander` (the deterministic
index-seeded producer; a future producer is a NEW ENUM MEMBER plus its
implementation, never a parallel flag; a network peer is just `live`). The
per-tick merge rule: **tape > submitted (admitted unless `idle`) > producer
(iff the source names one) > zero**, with the wire `player.press` lane always
overlaid and the device held-lane image admitted only under `live`. The client
gates its seat's device edges and held-intent submission on `live`.
`player.control <live|idle|wander> [player]` reads/writes any entity's source —
`player.control wander 2` makes seat 2 join the crowd while unattended (the same
producer path a peer runs, slot-seeded). `WorldPopulation.DefaultPeerSource`
(boot `wander`) is the stored peer TEMPLATE — newly activated peers take it,
which is why it stays observable at zero peers. An explicit
`world.population idle|wander` sets that default AND sweeps ALL peers (4..127)
to it — last-writer-wins: a per-entity `player.control` does not survive the
global flip. Seats are never touched by population operations.

**Loopback semantics** (`LoopbackTransport`): per-tick intents buffer and drain
at the server step; commands, session requests, and queries apply
**synchronously at submit**. The synchronous command apply is deliberate and
tick-equivalent: the host guarantees submissions arrive inside the command-apply
window immediately preceding the tick's step, so every mutation lands before
that tick's advance in stdin FIFO order — exactly the historical direct-mutation
boundary — and a policy read following a command in the same batch observes its
effect (the engage-after-warp ordering the screens proof asserts). A byte
transport buffers to the same boundary.

**Verb ownership.**

| Class | Verbs | Path |
|---|---|---|
| Client-local | `world.shadows/.ao/.render-scale/.target/.quality/.upscale-sharpness/.shadow-*/.ao-quality/.view-refresh/.debug-view/.timing/.gpu/.fps/.devices`; `player.move/look/sticks/confirm/cycle/claim/south/jump/assign`; `player.engage/disengage`; all `screen.*` | act on client state directly (engagement records its route on the body — a loopback seam, named in `WorldEngagement`) |
| Server commands | `player.run/fly/warp/face/pose/reconcile/press/motion/control/stop` (1..128) | handler validates → `WorldCommand` → `IServerLink` |
| Server session/identity | `player.join/leave/profile`; `world.population [count] [idle\|wander]` | `SessionRequest` → server allocates/validates → `SessionReply` |
| Server queries | `player.where` | `IServerLink.Query` → server-composed string, printed verbatim |
| Server mutations | `world.kit.*/screen.*/camera.*/scene.set/spawns.set/motion.set/wander.set/population.defaults/render.defaults/addon.*/bindings.*/load/undo` (Simulation-routed, buffered); `world.status` (Immediate read: definition + journal + a session-drift hint), `world.save` (Immediate: writes a session SNAPSHOT — definition + folded session state — and compacts) | `WorldMutation`/whole-document swap → `SubmitWorldMutation`/`SubmitDefinition`, buffered like intents; see **Moldable state** and **Session write-back** |
| Server grants | `world.grant/revoke` (Simulation-routed, but the grant table itself applies SYNCHRONOUSLY at submit); `world.grants` (Immediate read) | `WorldGrant` → `SubmitGrant`/`SubmitRevoke`; see **Principals and grants** |
| Player document | `player.bind <seat> <source> <command>` / `profile.save [seat]` (Simulation-routed); `player.bindings [seat]` / `profile.doc` (Immediate reads) | `SetPlayerSection` → `SubmitSession`; `PlayerDocument` → `Query`; see **The player document + bindings** |
| Storage status | `storage.status` (Immediate) | a pure local read of the routed store's identity/endpoint/revision/version-token state — no protocol round-trip; see **Storage** |

`player.stop`/`player.control` on a seat also touch the seat's client half (held
keys/lanes, the latch copy) in the same command. `world.players` stays
client-composed (device tokens and pending state are client metadata); its pose
fragments read the server bodies through a named loopback seam.

**Loopback-only seams** (each named in code, with what a socket transport
replaces it with): engagement's body writes/reads (`WorldEngagement`), the
roster's pose reads and catalog reference (`PlayerRoster`), handler validation
reads (`PlayerCommandModule.ResolveTarget`), the engage radius read
(`PlayerCommandModule.EngageHandler`), and the population diagnostics reads
(`WorldCommandModule`).

**Extension points this boundary preserves**: `IScreenMachineEngine` (the
DI-collected machine registry), the closed `WorldScreenSource` kinds,
`WorldBody.SubmitIntent` via `IServerLink.SubmitIntent` (any driver — network,
AI, replay — feeds any entity without touching internals), the `WorldDefinition`
provider seam (plus the live definition/mutation messages — see **Moldable
state**), and `ICommandModule` (modules compose against the link, so a plugin
module drives the same wire).

## The world as data (the server north star)

The guiding rule for what describes a world is **"the definition of a world is
pulled from the server."** So the hardcoded facts of *this* world are gathered
into one aggregate, `WorldDefinition`, with a `Default` that is today's world
verbatim — every value byte-equal to what shipped. It bundles the static
`WorldScene` (ground albedos + polymorphic `WorldSceneRow` placements —
`boulder-N` spheres and `slab-N` material-carrying boxes, upserted/removed by
stable id), the seat
`SpawnPoints` (each a stable `seat-N` id + position), the `WanderTuning` (the
stand-ins' drift/weave/inward-steer), the `WorldMotionDefaults` (the ground
plane plus the profileless move/turn speeds — the whole jump feel kit lives on
each kit's `MotionTuning`, where every body reads it, retuned by one number,
`ActionScale`), and the `WorldRenderDefaults` (the boot render levers + the
`world.quality` preset table). Every consumer — the roster, the population, the
frame source, the render settings, the `world.quality` verb — takes it by
construction/DI and reads the fields; nothing bakes the constants inline anymore.

The definition is a versioned document (`puck.world.def.v1`) that (de)serializes
through `WorldJsonContext` — source-gen, camelCase, enums by name, a `Vector3`
converter (`[x, y, z]`, never `IncludeFields`), and `$type`-discriminated
polymorphic screen sources / cameras / action predicates / effects. Boot loads
`--world <path>` (or `Assets/worlds/default.world.json` beside the executable)
through `WorldDefinitionLoader`: file → parse → schema check →
`WorldDefinitionValidator` (the one thick gate, now covering tunings, ids,
actions, the assignment policy, and addons), with a LOUD baked-default fallback
on any failure and one `[world] definition:` boot line. `world.save [path]`
writes the active definition back canonically (stable member order, invariant
numbers, LF, one trailing newline), so a load→save reproduces the file
byte-for-byte — the ouroboros round-trip (a property worth knowing, **not a
landing gate**; see the proof-suite note below).

This matches the **player-document stack** exactly (`WorldProfile` /
`WorldPlayerDocument` / `BuildDefault`): a serialization-friendly record shape
whose in-code `BuildDefault` is the hand-authored stand-in for what a document
(or a server stream) will fill later — see **The player document + bindings**
below. `WorldDefinition` is the **seam** cut for that pull, and
`Assets/worlds/default.world.json` (checked in, generated by
`world.save` against `Default` itself) is that data today — the loader fills
the same fields from the file and nothing downstream changes; deleting it (or
booting `--world` at a bad path) falls back to `Default` with the same loud
line. Both the ouroboros round-trip and the checked-in-vs-baked-default parity
are proven by `proof.cs worlddoc`.

**Two more checked-in worlds keep the loader honest** (a one-document loader
rots into a deserializer for its only input; a second and third keep it a
*format*). `Assets/worlds/kart-remap.world.json` exercises the per-world binding
overlay (see **The player document + bindings**). `Assets/worlds/expo.world.json`
is the standing genre-neutrality proof artifact — booting `--world
expo.world.json` yields a **visibly different game with zero code**: a new `glider`
kit row (6 kits, not 5), a `table` kit-assignment policy, retuned locomotion, a
warmer four-pillar scene, staggered spawns, and three asset-free screens (5→3),
plus folded session render/census defaults (medium shadows, AO on, ¾ render
scale, 32 stand-ins). It is authored **the honest way** — never hand-edited — by
a scripted stdin session (`scripts/expo-world.txt`: mutations + live session
levers + `world.save`) reproducible with `proof.cs expo-author`, and proven
(loud boot line + a distinguishing `world.status` fact + the write-back slice) by
`proof.cs expodoc`. All three round-trip byte-for-byte under the `worlddoc`
ouroboros. (One deliberate divergence noted in code: the
scene's rows are the minimal World-local kind-tagged `WorldSceneRow` records
(`boulder` | `slab`), not `Puck.Scene.SceneObject`s, because that vocabulary
speaks material *indices* and JSON vector arrays through an op chain — a
contortion for a handful of inline-colored shapes; the convergence onto the
`puck.run.v1` scene document is named there for when the scene grows.)

## Moldable state (the mutation vocabulary)

Every world-doc section is molded through ONE mechanism: a kind-tagged
`WorldMutation` record, submitted over `IServerLink.SubmitWorldMutation`,
buffered and applied at the tick boundary (compose a candidate → revalidate
the WHOLE document through `WorldDefinitionValidator` → on failure reject
loudly, definition unchanged → on success swap, journal, rebuild the changed
section's derived state, deliver). `WorldMutationCommandModule` is the console
reflection of that wire — the SAME messages an editor or a plugin submits
tomorrow, driven over stdin today. Every mutation verb routes
`CommandRouting.Simulation`, so a following `Immediate` read (`world.status`)
is held by the stdin drain barrier until the buffered edit applies — a
scripted `mutate-then-read` pair needs no polling.

| Verb | Effect |
|---|---|
| `world.kit.set <kit-json>` / `world.kit.remove <name>` | upsert/remove a `WorldKit` row (whole-row, keyed by name) |
| `world.kit.default <name>` | sets `DefaultSeatKit` |
| `world.kit.assign hash \| table <kit>…` | sets the kit→entity `WorldRowAssignment` policy |
| `world.kit.tune <name> <field> <value>` | console sugar: read-modify-write one `MotionTuning` field into a whole-row `UpsertKit` |
| `world.screen.set <screen-json>` / `world.screen.remove <index>` | upsert/remove a `WorldScreen` row (keyed by index) |
| `world.camera.set <camera-json>` / `world.camera.remove <name>` | upsert/remove a `WorldCamera` row (keyed by name); applies LIVE — a pose/aim/FOV edit rewrites the running offscreen view's rig in place, a dimension/kind change recreates it, a removal releases it |
| `world.scene.set <scene-json>` | replaces the whole static scene (albedos + rows); the per-row editor grain is `UpsertSceneRow`/`RemoveSceneRow` (the `editor.*` verbs) |
| `world.spawns.set <spawns-json-array>` | replaces the seat spawn-point list |
| `world.motion.set <json>` | replaces the `WorldMotionDefaults` (`moveSpeed`, `turnSpeed`, `groundY`); any other field is rejected by name — jump feel is `world.kit.tune` |
| `world.wander.set <json>` | replaces `WanderTuning` |
| `world.population.defaults <local> <network>` | sets the census defaults (document-only; preserves the peer-source default, which the live `world.population idle\|wander` verb owns and `world.save` folds) |
| `world.render.defaults <json>` | replaces `WorldRenderDefaults` |
| `world.addon.set <json>` / `world.addon.remove <name>` | upsert/remove a `WorldAddonRow` descriptor |
| `world.bindings.set <overlay-json>` / `world.bindings.remove <id>` | upsert/remove a `WorldBindingOverlay` row (whole-row, keyed by id); recomposes every seat's mapping on apply — see **The player document + bindings** |
| `world.load <path>` | whole-document swap via `SubmitDefinition`: validate → swap → derived rebuild → **journal RESET** |
| `world.undo [n]` | undoes the last `n` (default 1) applied mutations |
| `world.status` | reports source/schema/counts + a `session-drift` hint + `dirty`/`undoable` (Immediate) |
| `world.save [path]` | writes a **session SNAPSHOT** canonically (live definition + folded session state) + **compacts the journal** (Immediate) |

Row-valued verbs (`.set`) take ONE inline-JSON argument in the exact wire
shape of the document section — the console tokenizer's raw-line
reconstruction lets quotes survive, so no second grammar exists alongside
`WorldJsonContext`. A parse error echoes inline and submits nothing.

**The journal is the undo engine.** The server appends every applied mutation,
tick-stamped, to a session journal — `dirty` in `world.status` IS the journal
length, so "has this session been edited since it was loaded/saved" is never a
separate flag. `world.undo [n]` restores the loaded base definition and
deterministically replays the journal minus its tail through the SAME apply
path — no per-mutation inverse is ever written, because replay IS the undo.
`world.save [path]` writes a **session snapshot** back canonically (the
ouroboros round-trip) and then compacts: the saved definition becomes the new base and
the journal clears, so `dirty` drops to 0. `world.load <path>` swaps the whole
document and also resets the journal (a freshly loaded file starts clean).

**Session write-back (`WorldSessionCapture`).** A running world holds live
SESSION state that is not part of the loaded definition and never journaled: the
render levers the graphics verbs move (`WorldRenderSettings` — shadows, AO,
render scale, crowd radius, upscale), the live census the population verb moves
(`WorldPopulation.SimulatedCount` and the `DefaultPeerSource` template), and the
machines a runtime `screen.insert` booted onto declared screens (`WorldScreenBinder`).
`world.save` folds all three into their document homes — render levers into
`Render` (the continuous shadow reach / render scale quantize back to their
tiered boot defaults), the census + peer source into `Population` (the peer
source has a durable home: `WorldPopulationDefaults.DefaultPeerSource`, honored
at boot), and each live machine insert into that screen row's `Machine` source —
so a save is a faithful snapshot of what is playing, and re-booting the saved
file reproduces it. The fold is **saved-bytes-only**: it composes the snapshot
the writer serializes and never mutates the in-memory definition or the journal
(a save is a snapshot, not a mutation). It is exactly IDEMPOTENT on a freshly
booted world — live session state equals the document defaults at boot — so the
ouroboros round-trip still holds after a save learns to fold, for every
checked-in world (observed over all three by `proof.cs worlddoc`). Because the fold is
saved-bytes-only, `world.status`'s `session-drift` hint honestly persists past a
save: it names which live dimensions (`render`/`population`/`screens`) differ
from the in-memory document (`none` when a save would reproduce the file) — a
cheap verb-time comparison, never per tick.

**The protocol-version handshake.** `SessionRequest.Join` carries a
`ProtocolVersion`; `WorldServer.ApplySession` checks it against
`WorldProtocol.Version` and rejects a mismatch with `Accepted: false` and a
distinct `Reason` (`"protocol version N != server M"`) rather than admitting an
incompatible client silently.

Accept/reject console lines (stderr, always loud): `[world.mutation: <kind>
'<id>' applied]`, `[world.mutation rejected: <kind> '<id>' — <reason>]`,
`[world.undo: dropped <n>, <remaining> remaining]`. `proof.cs mutate` scripts
the round-trip end to end (see the proof suite below).

## Principals and grants

Every `IServerLink` write submission carries its acting **`WorldPrincipal`**
(`Protocol/WorldPrincipal.cs`) — a **seat** (a local roster slot), the
**console** (the one non-seat local authority `player.*`/`world.*`/mutation
verbs act as), an **addon** (a WASM guest, named), or a **peer** (a network/
population body). One server-side table, **`WorldGrants`**
(`Server/WorldGrants.cs`), holds every grant and is the ONE place a write is
authorized — the single primitive that engagement (`WorldEngagement`'s latch),
machine-input ownership, and addon slot ownership (`AddonHost.SlotOwner`) all
reduce to.

A **grant** (`WorldGrant`) is `(principal, capability, subject, exclusive)`.
**Capabilities** (`WorldCapability`): `Drive` (submit a body's intents/
commands), `Control` (engage a screen/machine route), `Mutate` (apply a
`WorldMutation` targeting a `WorldSection`), `Edit` (a player-profile
section, checked against the concrete `profile:<id>` subject). **Subjects**
(`GrantSubject`): the `all` wildcard, `body:<n>`, `screen:<n>`,
`section:<name>` (one of the eleven `WorldSection` values: kits, screens,
cameras, scene, spawns, motion, wander, population, render, addons,
bindings), or `profile:<id>` (a player profile's stable string id — the
subject matches `WorldPrincipal`'s index+nullable-string shape). **Exclusive**
generalizes the engagement latch: a second exclusive
acquisition of the same `(capability, subject)` by a DIFFERENT live principal
is rejected loudly; re-granting a subject a principal already holds is
idempotent; the SEEDED permissive defaults (the ordinary `all` wildcards AND
the seeded per-section `Mutate` rows) never block an exclusive acquisition —
the backdrop must never block a reservation, and enforcement makes the
exclusive holder the sole effective owner anyway. Storage is a per-principal
set of four subject sets — an
`Allows` check is one dictionary lookup plus a `HashSet` membership test,
allocation-free and O(1).

**Local play defaults permissive** — behavior is unchanged until someone
revokes: every seat holds `Drive` over its own body and `Control`/`Mutate`
(every section)/`Edit` over its whole domain; the console holds `Drive` over
every body and the same domain grants; every population peer holds `Control`
over every screen (the engagement route — peers never submit intents, so
they hold no `Drive`); addons get NOTHING until granted.

**Enforcement boundaries** — every write boundary asks `WorldGrants.Allows`
(or `AllowsAllSections`) before it acts:

| Boundary | Capability | Subject |
|---|---|---|
| Intent drain (`WorldServer.Step`) | `Drive` | `body:<entityIndex>` |
| `WorldServer.ApplyCommand` | `Drive` | `body:<command.EntityIndex>` |
| Mutation apply (`TryApplyMutation`) | `Mutate` | `section:<the mutation's WorldSection>` |
| Whole-document swap (`SubmitDefinition`/`world.load`) | `Mutate` | every `WorldSection` |
| Journal undo (`world.undo`) | `Mutate` | every `WorldSection` |
| Engage (`player.engage`, `WorldEngagement.Engage`) | `Control` | `screen:<index>` |
| Profile-section edit (`SetPlayerSection` — `profile.section`, the `profile.save` fold) | `Edit` | `profile:<id>` (the seeded `Edit/all` wildcard passes for local play) |

A denial is loud and DATA-shaped — never a new message kind — and the write
drops: `[world.grant denied: <principal> cannot drive body:<n> —
<command/kind> dropped]` / `... cannot mutate section:<name> — <mutation>
dropped]` / `... cannot mutate every section — world.load/world.undo
dropped]`. The intent-drain denial is edge-latched PER BODY (one line per
denial episode, reset the moment an allowed submission arrives), so a
revoked driver that keeps submitting logs its refusal once, not every tick.

**Verbs** (`WorldGrantCommandModule`) — `world.grant`/`world.revoke` route
`CommandRouting.Simulation` (so the stdin barrier serializes a following
read), but the grant table itself applies SYNCHRONOUSLY at submit, like a
command, never buffered to the tick boundary like a mutation:

| Verb | Grammar | Effect |
|---|---|---|
| `world.grant <principal> <capability> <subject> [exclusive]` | principal = `seat1..seat4\|console\|addon:<name>\|peer:<n>`; capability = `drive\|control\|mutate\|edit`; subject = `body:<n>\|screen:<n>\|section:<name>\|profile:<id>\|all` | adds the grant; an exclusive acquisition a live holder owns is rejected loudly (the seeded permissive defaults never block one) |
| `world.revoke <principal> <capability> <subject>` | same tokens, trailing `exclusive` is not accepted | removes the grant (a no-op reports "held no ...") |
| `world.grants [principal]` | — | echoes the whole table, or one principal's rows (Immediate; the barrier reads the settled table after a pending grant) |

**Addons as principals — the keystone proof.** A `WorldAddonRow` (name,
module path, hash, fuel, enabled) in the world document's `addons` section
(`world.addon.set`/`.remove`, `WorldSection.Addons`) is a data-only edit —
mounting happens once, at BOOT, when `Client/WorldAddonDriver.cs` composes an
`AddonHost` from the loaded definition's ENABLED rows through
`Puck.Scripting` (consumed, never modified): a `.wat`/`.wasm` module (text or
binary, detected by magic) compiles into one Wasmtime store, and an optional
declared hash pins its content. A mounted addon holds NO body until granted —
`world.grant addon:<name> drive body:<n> [exclusive]` is the ONLY binding;
the driver discovers its body from the grant table
(`WorldGrants.FirstDriveBody`) rather than a slot on the row. Once granted,
the driver flips the body's `IntentSource` to `Live` (so the wander producer
yields to the submitted stream) and, every tick, writes the ABI snapshot,
ticks the guest, translates its decoded virtual-pad commands into a
`PlayerIntent` over World's channel set, and submits it over the SAME
`IServerLink` a human seat uses,
principal `addon:<name>`. The SERVER decides whether it applies — capability
checks live there, never on the client, so *where* an addon runs (beside the
client or beside the server) is a hosting choice, not a contract change.
`world.revoke` mid-run drops its next submitted intent (the edge-latched
denial line) and the body idles — `IntentSource` stays `Live`, so it never
silently resumes wander.

Proven end to end by `proof.cs grants`: `world.addon.set` an autopilot row +
`world.save` (session A) → relaunch `--world <that file>` asserts the
`[world.addon: mounted autopilot ...]` boot line (session B) →
`world.grant addon:autopilot drive body:<n> exclusive` drives the body
(asserted by two `player.where` samples a second apart) → `world.revoke`
denies its next intent and freezes it (two more samples, identical) →
`world.revoke console mutate section:kits` makes `world.kit.tune` fail
loudly with `world.status`'s `dirty` counter unchanged, and a re-grant makes
the identical command apply.

**`WorldEngagement` is a view, not a table.** The engagement latch is
read and written entirely through `WorldGrants`' `Control` capability
(`SetControlRoute`/`ClearControlRoute`/`ControlRoute`/`CollectRouteHolders`)
— one table, not two. `proof.cs screens` is the engagement regression
coverage; `proof.cs grants` does not duplicate it.

## The player document + bindings

Player-scoped state lives in its own document family, `puck.world.player.v1`
(`WorldPlayerDocument`) — never a `puck.run.v1`/`WorldDefinition` section: a
profile travels with a PERSON across worlds, a run/world document describes a
WORLD. It is a **catalog**, exactly like the profile stack it absorbs: a
monotonic `Revision` (bumped on every save — a future ordering
key) plus `Profiles[]`, each a stable `Id` and four sections — `identity`
(display name + `#RRGGBB` color), `motion` (speeds + look-invert),
`bindings` (a `BindingProfileDocument?`, `null` = inherit the engine default),
and an open `preferences` bag — plus a document-level `Extensions` bag, so
unknown sections and unknown profile fields survive a round-trip untouched
(the data-side plugin posture, matching `PuckRunDocument`'s convention).
`WorldPlayerDocumentValidator` is the one thick gate (schema, non-negative
revision, unique ids/names, parseable colors, finite positive speeds, and a
non-null `bindings` section additionally gated through the existing
`BindingProfile.Compile`); a malformed stored document falls back LOUDLY to
the built-in default rather than taking the game down.

**One on-disk layout.** `WorldProfileStore`
(`Server/WorldProfileStore.cs`) persists the catalog as a PER-PROFILE split
layout — `world/player.json` (the catalog) plus one `world/profiles/<id>.json`
blob per entry, with the machine-local boot-seat sidecar at `world/local.json`
— the same address model the future cloud container uses (see **Storage**
below for why the split and the ordering/version-token fields exist). That is
the ONLY shape the store reads: a present catalog is assembled from its
per-profile blobs, and an absent one seeds the built-in default. No migration
ladder, no read-side tolerance for a superseded layout — supergreen means
there is no installed base to absorb. Machine-local boot seating and the sync cursor (which
profile player 1 wakes on, `LastSyncedRevision`) live ONLY in `world/local.json`
— they must never roam to the cloud.

**Bindings: layered authoring, grouped resolution.** The binding document
(`puck.bindings.v1`) is a list of CHORD ROWS — `(group, ordered chord) →
meaning`, where the meaning is a discriminated union: a **page** (an entry
table, `BindingPageDefinition`) or a **command** (a direct chord-to-command
binding with full entry semantics — HoldRelease shape, constant value,
label/icon; `BindingCommandDefinition`). Page switching is not privileged: it
is one meaning a chord can carry. Two questions, two mechanisms:

- **Authoring is layers.** `WorldSeatBindings` (one `PagedInputBindings` per
  local seat) resolves every seat's input from a document PRE-MERGE, compiled
  once per seat on any change (never per frame):

  ```
  effective document = engine default BindingProfileDocument   (WorldDefaultBindings — the play group AND the editor group)
                     ⊕ world overlay(s)                        (puck.world.def.v1 bindingOverlays — a contextual row, e.g. a kart world's remap)
                     ⊕ player profile bindings                 (the seat's selected profile, from puck.world.player.v1)
                     ⊕ live rebinds                            (session layer; folded into the profile on profile.save)
  ```

- **Runtime mode is the ACTIVE GROUP.** Every group is always compiled in; a
  seat holds one active group (`play` by default) and
  `WorldSeatBindings.SetActiveGroup` flips it as a POINTER-LEVEL switch on the
  compiled profile — no recompose, no document churn, and the seat's press
  latches, held chord, and armed command chords survive the flip. A mode is
  one seat's state, never a world `bindingOverlays` mutation (which would
  re-bind every seat).

Resolution is group-scoped and prefix-deep: within the active group, the page
row with the LONGEST chord that is a press-order prefix of the held modifiers
answers the seat's sources (the empty-chord RESTING page is the fallback), and
a command row fires its press edge on the very signal that completes its chord
— release when any member releases — synthesized as chord edges the
`InputRouter` folds with their own phase/value (`IChordEdgeSource`), so
chord-fired commands are snapshot-visible and held-tracked like any bound
press. Exactly one meaning per `(group, chord)` and exactly one resting page
per group, rejected loudly at `BindingProfile.Compile` (engine-gated by
Puck.Post's `binding-page` stage, group flips and the cross-flip latch
included).

`WorldBindingComposer.Compose` merges layers with explicit keys — chord rows
on **`(group, ordered chord)`** (a later layer's row for the same key
overrides: wholesale when the meaning kind or page id differs, entry-by-source
when both are the SAME page — the single-lane remap a per-world overlay
needs); modifiers union by id. This is the level the compiled
`LayeredInputBindings` primitive cannot express (it composes wholesale per
`(slot, source)`). The merged document then goes through
`BindingProfile.Compile` once, and the compiled result hot-swaps in via
`PagedInputBindings.Reload` (the seat's requested group carries over). The
console dispatch path (`BindingCommandSource`, dormant in World since the
router owns all physical input) derives from the SAME composed default-group
resting page (`WorldSeatBindings.ConsoleBaseTable`) — no second authoring
grammar.

**Verbs** (`WorldBindingCommandModule`):

| Verb | Effect |
|---|---|
| `player.bind <seat> <source> <command>` | live-remaps one binding into the seat's SESSION layer (unsaved until `profile.save`); `<source>` is an input source id for a resting-page entry, or a chord-row declaration: `chord:lt+rt` (the ordered chord, play group) / `chord:<group>:m1+m2` (an explicit group). Recomposes and hot-reloads that seat at once (Simulation-routed) |
| `player.bindings [seat]` | echoes the seat's composed ACTIVE mapping — the play resting page's `source→command` entries, then every chord row with its meaning (`chord play:[lt+rt]→editor.enter`, `chord editor:[lt]→page editor-camera`) (Immediate) |
| `player.signal <source> <press\|release\|value>` | synthesizes one raw input signal into the router on seat 1's device-neutral lane — the scripted twin of a physical pad, so chords are drivable over the pipe (a number is an analog Active sample; a trigger sweep `0.9`/`0` latches/releases a modifier through hysteresis; the signal folds into the NEXT tick's snapshot) (Simulation-routed) |
| `profile.save [seat]` | folds the seat's session rebinds into its selected profile's durable `bindings` section and persists through `SetPlayerSection` (gated on the `Edit` capability), then empties the session layer (Simulation-routed) |
| `profile.doc` | echoes the whole server-owned player document as JSON (`WorldQuery.PlayerDocument`) — the read-back an editor/agent pulls before editing a section (Immediate) |
| `world.bindings.set <overlay-json>` / `world.bindings.remove <id>` | upsert/remove a per-world `WorldBindingOverlay` (§ **Moldable state**'s mutation vocabulary); recomposes every seat on apply |

Live rebinding changes the input→command mapping mid-run — deliberately
breaking replay-stable command streams (World is not determinism-gated,
so this is accepted, not accidental). `player.bind`/
`profile.save` route `CommandRouting.Simulation`, so the stdin drain barrier
serializes a following `player.bindings`/`profile.doc` read-after-write, the
same pattern **Moldable state**'s mutate-then-`world.status` pairing uses.

**The checked-in example world**, `Assets/worlds/kart-remap.world.json`, is a
copy of the default world plus one `bindingOverlays` entry: East remaps from
`player.secondary` to `player.primary` for a kart's single-button drift lane —
proof that a world can override ONE entry inside the shared base page without
touching the engine default or any profile.

Proven end to end by `proof.cs bindings`: session A boots a fresh catalog,
asserts the engine-default composed mapping, live-rebinds a source
(`player.bind`), and folds+persists it (`profile.save`, asserting the document
`Revision` bumps); session B relaunches and asserts the rebind survived
(`puck.world.player.v1` persistence) and a plain boot does not itself bump the
revision; session C boots `--world kart-remap.world.json`, asserts the overlay
merges from tick 0, then `world.bindings.remove` live-recomposes every seat
back to the engine default. There is no CLI override for the store path, so the
proof backs up and restores the REAL `world/` subtree whole (byte-for-byte)
around every session — the real catalog is never destroyed.

**Chord pages** are the shape of every group: five pages per group, one per
ordered trigger chord — nothing held = page 0, `LT` = 1, `RT` = 2,
`LT`-then-`RT` = 3, `RT`-then-`LT` = 4 (press order is load-bearing;
`HeldOrderTracker` keeps the held set ordered, so 3 and 4 are genuinely
different pages). Holding the triggers IS the page turn: the binding bar
re-renders the selected page's twelve chips and draws the page's **name** by
the modifier pips, so the chord vocabulary is discovered by squeezing rather
than memorized. The play group's pages 1..4 are deliberately **sparse** — they
carry only the stick routers (a held analog re-dispatches against the active
page each tick) and wait to be authored through the binding document.

**Editor mode** is the editor
GROUP's tenancy: a per-seat client mode entered with **Gamepad Back / Keyboard
Tab** or `editor.enter [seat]` — no trigger combination enters a mode — and
exited with East / Back / Tab or
`editor.exit [seat]`. Entering flips the seat's active group to `editor`
(`WorldSeatBindings.SetActiveGroup` — a pointer switch, no recompose): the
**editor resting page** (sticks fly, shoulders rise/sink, South toggles
fly⇄orbit, D-pad steps speed, West echoes status) plus the **LT camera page**
(explicit fly/orbit + speed; North picks the row under the crosshair so orbit
has a pivot), the **RT select page**, the **LT+RT place page**, and the sparse
**RT+LT reverse page** — the
binding bar renders whatever page the group's chords select. The seat's
intent diverts through the existing `player.control idle` contract on both
halves (live devices mask; tapes and `player.press` still drive — script
outranks idle), its camera swaps from the chase rig to the session's
free-fly/orbit rig (seeded from the chase framing, restored by re-anchoring —
neither edge pops), and when exactly ONE seat edits among 2+ players the
layout gives it the full-height left 70% workbench with the playing seats
stacked in a live right rail. `WorldEditorSession` owns all of it client-side;
nothing crosses the wire beyond the existing `SetControl`. The console twins
(`editor.status` — which echoes `group=` + `page=`, the flip's assertable
truth — `editor.fly`/`editor.orbit`, `editor.cam.speed <v>`,
`editor.cam.pose <x> <y> <z> [<yawDeg> <pitchDeg>]`) script every chord act.
Proven on both backends by `proof.cs editor-mode` (group round trip, the five
ordered chord pages walked from data over `player.signal`, camera in pixels,
diversion honesty, the layout seam, the loud wire-level rule rejections).

**Selection and manipulation** builds the targeting and drag layers over
the mode. A selection is `(section, id-or-index)` — pure client state in
`WorldEditorTargeting`, never protocol — over scene rows, screens, spawns, and
cameras; it self-heals against every delivered definition, tints the selected
scene row amber in the render (a material swap at rebuild cadence), and
retargets the orbit pivot. Targeting is proximity (`editor.next`/`prev` cycle
candidates nearest-first around the camera focus) or the look ray:
`WorldEditorPicker` builds a fixed-point picking program from the DOCUMENT
(one material per row, proxy spheres for spawns/fixed cameras) behind the
existing `SdfFieldEvaluator`, rebuilt only on a definition delivery —
`editor.pick` names the row under the crosshair in microseconds. The static
scene itself generalized for this: `WorldScene.Rows` is a `$type`-discriminated
row vocabulary (`boulder` | `slab` — a slab carries its material as data),
addressed per-row by the `UpsertSceneRow`/`RemoveSceneRow` mutations, with the
render-envelope probe reserving documented authoring headroom (32 rows + 4
screen slots) so live placement works until the ceiling rejects loudly.

Continuous edits ride `WorldEditorDrag`'s pending-row preview channel:
`editor.grab` copies the selected scene row or screen client-side, the sticks
(re-routed from flight while the drag lives) or `editor.drag <dx dy dz>` move
it through `Puck.Authoring.GridSnap` (`editor.snap [on|off|<pitch>]`), the
frame source composes the pending row over the delivered definition so the
EXISTING rebuild path renders the preview, and release (`editor.grab` again /
`editor.release`) submits exactly ONE whole-row mutation — a whole drag is one
journal entry, one undo step; `editor.cancel` never existed. Ghost spawns
(`editor.spawn.boulder`/`.slab`, D-pad on the place page) preview a NEW row the
same way and enter the document only on commit. The discrete twins
(`editor.select <section> <key>`, `editor.move`/`editor.nudge <x y z>`,
`editor.place boulder|slab […]`, `editor.delete`) submit one mutation per act
with the ACTING SEAT's principal, so grant denials land on the seat that asked
and surface as toasts. The editor HUD (selection readout, candidate/snap
context, drag line) is a new writer on the unified overlay — per-seat, scoped
to each seat's viewport. Proven on both backends by `proof.cs editor-edit`
(place = one journal entry, drag coalescing at the wire, undo restores the
position, the look-ray pick, the highlight in pixels, capacity honesty).

**Creations and placements** make sculpted content world DATA. Two
sections in the whole-row pattern: `creations` — ASSET rows embedding a full
`puck.creation.v1` document INLINE-CANONICAL with its SHA-256 pinned beside it
(doc + hash always come from the same
`CreationCanonicalizer.Canonicalize` result; the compose boundary re-derives
and REJECTS any hash the pipeline did not itself compute, and the validator
re-verifies the pin so a tampered world file falls back loudly at boot) — and
`placements` — INSTANCE rows (`id`, `creationId`, position/yaw/scale, repeat +
mirror facets as data, a reserved nullable `role` for the driven-body rung).
Four mutations (`UpsertCreation`/`RemoveCreation`/`UpsertPlacement`/
`RemovePlacement`); removing a creation with live placements rejects loudly
(no cascade). Static stamps bake the full placement transform into every
shape's own segment (repeat rows auto-split so instance bounds stay tight);
placements of a FRAMED creation are ANIMATED — they replay their timeline
hold-style at the fixed 8-tick cadence on the render clock through a
reserved dynamic-transform pool (`WorldPlacementAnimator`, reconciled by
stable id at each delivery: pose edits write in place, content changes
release+recreate, removals release symmetrically). Every placement policy
value (stamp budget, headroom, repeat split, animated pool, cadence) lives in
ONE place — `WorldPlacementPolicy`, a candidate for future data-fication —
and the probe reserves boot+headroom worst-case stamps plus the whole replay pool, so
over-budget placements reject with the envelope's word-exact ceiling. The
place page grows place-by-name: D-pad Left/Right cycle the armed creation,
North ghosts a placement of it (commit = `editor.grab`, one mutation); the
typed twins are `editor.import <path> [id]` (the strict canonicalizer is the
only door in), `editor.creations`, `editor.place <creationId> [yawDeg
[scale]]`, and the JSON row verbs `world.creation.set`/`.remove` +
`world.placement.set`/`.remove`. Placements select, pick (reach-sized
proxies), drag, move, nudge, and delete like every other row, shimmer on
delivery like scene rows, and `world.save` re-canonicalizes every creation row
so the persisted pin can never diverge from its embedded bytes. Proven on
both backends by `proof.cs placements` (import/stamp in pixels, the corrupt
hash pin, drag coalescing, undo, the no-cascade reject, the proof-authored
animated probe's pixel motion, the capacity flood, and the save→reload→save
byte ouroboros with creations embedded — the proof authors its OWN creations
through the canonical pipeline; Demo content never ships as World content). Text runs COUNT against the per-stamp
shape budget but do not render yet (World binds no world-space glyph
atlas — the combined MTSDF decode is far too memory-heavy for a runtime bind; binding one later is emission-only).

**The sculpt workbench** is the creation SUB-EDITOR: a per-seat
`Puck.Authoring.SculptModel` (primitives, blends, the 16-slot palette,
hold-style timeline frames, IK chains — the analytic two-bone/spine
`ChainSolver`, deliberately float host-side math) edited inside a client-local
bench (`WorldWorkbench`). The live preview composes a synthetic creation +
placement over the delivered rows and renders through the SAME
`WorldPlacementStamper`/`CreationGeometry` path a committed stamp uses — what
you sculpt IS what stamps, byte-for-byte (the proof pins it in pixels). The
bench is a page GROUP (`sculpt` — a mode within editor mode): the resting
build page (South adds, North re-primitives, West/East walk the LOCAL undo
ring, D-pad cycles the target through shapes AND chain goals), LT bench
(Commit/Easel/zoom), RT style (blend/mirror/color/smooth/scale), LT+RT frames
(record/play/step/delete), RT+LT rig (chain define/kind/cycle/delete); the
move stick drives the sculpt target while the look stick orbits the bench.
`editor.sculpt.new <rowId> [x y z]` opens blank, `editor.sculpt.edit <rowId>`
loads an existing row (carried cameras/behavior/text-runs/extensions ride
verbatim); `editor.sculpt.commit` canonicalizes and submits ONE
`UpsertCreation` (doc + hash from the same `CanonicalCreation`) — live
placements of the row refresh on delivery, animated ones through the
animator's hash-diff release+recreate. UNDO HAS TWO HONEST DOMAINS, narrated
distinctly: `editor.sculpt.undo`/`redo` walk the bounded local ring
(mid-sculpt, gesture-coalesced, never touches the journal);
`world.undo` reverts committed acts (the journal). `editor.sculpt.easel`
authors the diegetic preview easel — a fixed bench camera plus an existing
screen row re-pointed at its view, two ordinary mutations through the live
camera/screen reconcile (the offscreen view renders the composed program,
preview included — the first composed diegetic surface). Every chord act has
an `editor.sculpt.*` typed twin (~40 verbs across four modules); the editor
HUD narrates the bench target, the `shapes n/48` stamp budget, the timeline
cursor, and the ring/uncommitted counts. Capacity: bench entry pre-verifies
the composed candidate against the probed envelope (ghost spawns fold the
bench in, so all client-local previews fit TOGETHER), and the model itself
enforces the 48-shape stamp cap. Proven on both backends by `proof.cs sculpt`
(sculpt-from-nothing over stdin, the stamp=preview pixel identity, the
two-domain undo split, a sculpted 2-frame timeline animating its stamp,
live refresh on re-commit — recreate on recolor, release when the frames
delete — the carrier round-trip of cameras/behavior/extensions members, the
easel's live screen bind, and the save→reload→save byte ouroboros).

## Storage (cloud-ready, local-proven)

This is the readiness layer the eventual cloud backend will build on, proven
against the local backend only — no live Azure wiring yet; `storage.status`
reports that truth rather than pretending otherwise.

**Three scopes, one persistence story.** World-scope state (`puck.world.def.v1`)
lives in a data file, durable through `world.save`. Session-scope state (seat
→profile selection, live rebinds, unsaved population/render settings) is not
durable by itself — it survives only by folding into a `world.save` or a
`profile.save`. Player-scope state (`puck.world.player.v1`) is what this
section covers: server-owned at runtime, user-owned durably, routed through a
local-always / cloud-per-user-when-identity-present store — the local half of
that routing is built and proven today.

**Per-profile blob layout.** `WorldProfileStore` (`Server/WorldProfileStore.cs`)
splits the catalog under the user's container (today a fixed local container,
`%LOCALAPPDATA%/Puck/World/<b1d5c0de-0002…>/`) into:

| Blob | Contents |
|---|---|
| `world/player.json` | the CATALOG: `schema`, `Revision`, `UpdatedAtUtc`, the ordered profile-id list, `Extensions` |
| `world/profiles/<id>.json` | one profile body per catalog entry — `identity`/`motion`/`bindings`/`preferences` |
| `world/local.json` | the machine-local sidecar: the boot-seat profile id + `LastSyncedRevision` — never roams |

This is the SAME address model the future cloud container uses, so two
devices editing DIFFERENT profiles are always independent; editing the
SAME profile from two devices stays whole-profile last-writer-wins — detected
(not merged) via the two mechanisms below.

**Two ordering mechanisms, kept deliberately separate.** `Revision`
(monotonic, bumped on every save) plus `UpdatedAtUtc` (the tiebreak) is the
**ordering key** — it answers "which copy is newer," and lives IN the
document, so it round-trips with the data. The storage version token
(`ObjectBlobReadResult<T>.VersionToken` / `ObjectBlobWriteResult.VersionToken`
— a SHA-256 content hash on the local backend, the download ETag on the
future Azure backend) is the **clobber guard** — it answers "did someone else
write since I read," via an optional if-match on write (`ObjectBlobWriteMode`
plus the write result's `PreconditionFailed` flag). ETags are opaque: they
can guard, they cannot order — the two mechanisms are never conflated. On the
local backend the guard is best-effort within one process (an inherent
read/write TOCTOU gap); true optimistic concurrency is an Azure-backend
property a cloud-backed store turns on without reshaping this seam.

**Derived dirty, never a volatile flag.** `WorldProfiles.Dirty` is
`Revision > LastSyncedRevision` — crash-safe by construction, since both
numbers are persisted (`LastSyncedRevision` lives in the `world/local.json`
sidecar). With no cloud wired, `LastSyncedRevision` stays 0 forever, so
`Dirty` is always true — the honest truth that the local copy has never
synced.

**Identity → container, exactly two implementations today.**
`IPlayerStorageIdentityResolver` (`WorldStorageIdentity.cs`) resolves the
acting principal to a per-user container id, or DECLINES. Exactly two
exist: `ExplicitOverridePlayerStorageIdentityResolver` (a `--user-id`/
data-file override — an Entra `oid`-shaped Guid; a non-Guid value declines
loudly rather than inventing a container) and
`DecliningPlayerStorageIdentityResolver` (the local-only default). NO token
parsing, NO claims flow yet — those require a
real ID token, never a parsed storage access token (`DefaultAzureCredential`
yields an opaque JWT; decoding it is unsupported-brittle, and under
CI/managed-identity its `oid` would be the APP's, silently collapsing
per-user into per-app).

**The reserved endpoint field + CLI reflections.** The world doc's `storage`
host-section (`WorldStorageDefaults`: `endpoint`, `userId`) is RESERVED —
nothing constructs an Azure target from it yet. `--storage-uri` and
`--user-id` overlay the document defaults at boot
(`WorldStorageSettings.Resolve`); `storage.status` echoes both.

**`storage.status`** (`WorldStorageCommandModule`, Immediate — a pure local
read, no protocol round-trip) is the one verb this readiness surface acts
through:

```
[storage.status: tier local (authoritative); cloud unwired | identity <resolution> |
  endpoint <endpoint|none> | catalog revision <N> lastSynced <N> dirty <on|off> |
  token <token|none> lastWrite <ok|precondition-failed> | file <path>]
```

`identity` is one of `explicit override userId=<guid>`, `declined — no user
identity (per-user sync off, local-only)`, or `declined — explicit override
userId '<value>' is not a container Guid; declining (local-only)`. It always
reports the honest truth: with no cloud wired, identity is absent or
override-only, the endpoint is reserved, and the local copy is authoritative
and (since `LastSyncedRevision` never advances) permanently unsynced.

**Not yet built (nothing here needs reshaping when it is):**
constructing an `AzureBlobObjectStorageTarget`, the real claims-based identity
implementation, the cloud-async sync policy + retry loop, `storage.sync`'s
live round-trip, Azurite proofs, a live smoke test against
`blob.byteterrace.com`, and the optional `Puck.Storage` Post-stage question.

Proven end to end by `proof.cs storage`: a fresh boot against the cleared REAL
store asserts `storage.status`'s honest baseline (local authoritative / cloud
unwired, identity declined, endpoint none, a present catalog revision +
version token), then the cheapest revision-bumping verb (`profile.set`) is
asserted to bump the revision, change the version token, and flip `dirty on`,
with the on-disk split layout (`world/player.json` + `world/profiles/*.json`
+ `world/local.json`) present; a relaunch asserts the same persisted revision
survives; a `--user-id <valid oid Guid>` boot asserts the explicit-override
echo, and a `--user-id not-a-guid` boot asserts the declining echo. Like
`proof.cs bindings`, it backs up and restores the REAL `world/` +
`profiles/` subtrees whole around every session — the real catalog is never
destroyed.

## Native capture (the recording graph)

The world records itself to a modern, free AV file — microphone + system audio +
video, everything defined as data. The primitive lives ABOVE any one game: a
**recording graph** (`puck.recording.v1`) — frame source → data-defined
compositor → encoder ladder → muxer — its own document family in `Puck.Recording`,
consumable by the World, the demo, or a headless render alike.

**The recording document is HOST-scope data** — like the `storage` host-section,
it describes an operation the running world *performs*, not the world's own
simulation state, so it lives outside `puck.world.def.v1`. It is resolved once at
boot: `--recording <path>`, or the checked-in
`Assets/recordings/default.recording.json` beside the executable, loaded and
validated through the document's own thick validator with a LOUD baked-default
fallback (`RecordingDocumentLoader`, mirroring the world-definition loader). The
document carries the codec ladder (`["av1","h264"]` — the free-standard AV1
preferred, H.264 the universal fallback), the audio topology (mic + loopback,
mixed to one stereo track by default because most single-track upload workflows
read one track; `"track":"isolated"` splits a row onto its own archival track), and
**capture-only overlays** — text, rectangle, and running-timecode rows composited
into the recording AFTER the render tap, so they exist in the file and **never in
the game window** (the baked default carries a `PUCK WORLD - CAPTURE ONLY` label
proving exactly that). The container is a hand-rolled Matroska/WebM muxer: `.webm`
(doc type `webm`) when AV1 + Opus negotiate, `.mkv` (`matroska`) for the H.264
fallback; Opus audio via the managed Concentus codec at 48 kHz; a `Colour` element
signals BT.709 limited range so a player renders the stream correctly.

**Live capture is a present tap.** The world's render root is wrapped once, for
its whole lifetime, in the backend-neutral `CapturingRenderNode`. The live
windowed present path hands GPU surfaces, so the tap reads each captured frame
back to CPU pixels through the SDF engine (`SdfEngineNode.ReadOutputPixels`) and
composites the overlays onto that copy — a **synchronous GPU readback per captured
frame** on the render thread. It costs nothing until a session is armed (the
`RecordingTap` gate), so the tap is free when idle; `capture.status` echoes the
readback posture, and the frame counters plus `world.fps` reveal the live impact
(no cost figure is claimed without measurement).

The control plane is three Immediate verbs (no simulation effect):

| Verb | Effect |
|---|---|
| `capture.start [output-path]` | Resolves the encoder ladder + audio sources against THIS machine (opening only what it can encode and capture), arms the render tap, and echoes the negotiated codec and any device declines (a mic privacy denial is a loud, honest decline — the loopback still records). |
| `capture.stop` | Drains and finalizes the container (final cluster, cues, patched duration) and echoes the output path, negotiated codec, frames captured/dropped, audio drops, and byte size. |
| `capture.status` | Reports running/idle, the negotiated codec, frames captured/dropped, audio tracks and drops, bytes written, the output path, and the source document. |

The Media Foundation encoder ladder and WASAPI loopback/microphone sources are the
Windows platform backend (`AddRecordingPlatform`); off Windows the factories
decline honestly and `capture.start` echoes why. On this box the ladder resolves to the
NVIDIA AV1 hardware encoder → a true `.webm`.

Proven end to end by `proof.cs record`: a real GPU-windowed boot arms a session,
records ~5 s of the autonomous crowd, finalizes, and asserts the produced
container — the EBML doc type matches the negotiated codec, an Opus audio track and a
video track are present (audio asserts track presence, not loudness — a
headless-ish run may capture silence), the file is non-trivial, `capture.status`
reads idle before and after, and the recording document used carries the
capture-only overlay row.

### The sim clock, and what an offline renderer already has

`RecordingDocument.Clock` picks the timestamp source. `wall` is the live-capture
story. **`sim` takes a frame's presentation time from the engine tick**
(`ticks x 1e9 / 50400`) rather than the wall clock, so a render can be divorced
from real time entirely. The validator enforces the pairing rule:
**a `sim`-clock document rejects audio rows** — a deterministic render carries no
live audio, so a sim-clock reel is silent or scored later
(`RecordingDocumentValidator`).

The mode is shipped but **unexercised**: nothing in the tree drives the loop from
a tick counter yet. The seams an offline/sim-clock renderer would compose are all
already present, so nobody needs to re-derive them:

| Need | Already shipped |
|---|---|
| Deterministic stepping | The fixed-step launcher + `Puck.Maths` fixed point |
| Scripted input | The proof-corpus format and the feeder's pacing machinery (`scripts/proof.cs`) |
| Frame tap with tick timestamps | `CapturingRenderNode` → `CaptureFrame.TimestampTicks` |
| Sim-clock muxing | `RecordingDocument.Clock = Sim` → `RecordingSession` |
| Encoders, overlays, muxer | The whole recording graph described above |
| CPU pixels off-screen | The same readback the live tap uses, under headless hosting |

The genuinely missing piece is an **offline pump** — a host mode that advances the
fixed-step loop from a tick counter instead of the presentation clock and blocks
on the encode queue instead of dropping when it backs up (offline inverts the
drop policy: correctness over liveness).

## True deterministic replay (record → verify)

Distinct from the video recording graph above: this is a **deterministic
world-state replay**. A `WorldReplayTape` (record side) captures, while armed, the
running session's authoritative SERVER state and input stream into a self-contained
`WorldReplaySnapshot`; `replay.verify` rehydrates a **fresh world** from that recording
and re-drives it to prove a recorded-vs-replayed hash match.

**What a recording captures (the honest scope).** The starting state is the
record-start `WorldDefinition` (embedded as its canonical JSON) plus the active seats —
the population's body state at that instant is the deterministic *boot image* of that
definition, which the fresh world reconstructs exactly, so no per-body pose is
serialized. The driving is the **per-tick server-input stream** — the intent
submissions and authority commands that reach the loopback each tick, captured by
`LoopbackTransport`'s `IntentTap`/`CommandTap` and closed per fixed tick by
`WorldSimulation.Step`. **Screen machines and their pixels, cameras, overlays, and audio
are PRESENTATION and are excluded** — re-derived from the definition each frame, never
fed back into simulation — so a replay reproduces the authoritative population
trajectory (the hashed poses) bit-for-bit, not the emulated cabinets or the HUD.

**The replay is a fresh, offline recomputation, verified against the LIVE session.**
`WorldReplaySnapshot.Drive` builds a brand-new `WorldServer`/`WorldPopulation` from the
recording, re-joins the seats, and re-drives the captured stream tick-by-tick (commands
before the step, intents drained at it — the exact live order), computing the same
per-tick FNV pose hash. The record side does NOT re-drive to produce its reference hash —
it samples the **live** population's tail pose hash off the running session, so a **MATCH
proves the fresh re-drive reproduces the actual live session**, not merely that one
re-drive equals another. Because the fresh world starts from the definition **boot
image**, a MATCH is a fidelity proof precisely when the live session was still at that
boot image at record-start (a boot-anchored capture); a **mid-session capture** — the
session already moved from boot — faithfully re-drives its stream but from the boot image,
so `replay.verify` honestly reports **MISMATCH** (full per-body record-start rehydration,
so a mid-session capture also MATCHes, is the identified next lever). Because the replay
runs over an isolated shadow world that never touches the live session, live seat input is
**structurally excluded** from a playback (the strongest lockout), and the verdict is
readable **synchronously** the instant `replay.verify` returns — no per-tick drain to wait
out. The hashed pose state is fixed-point or an exact integer tick — no wall-clock, no
float. (The serialized command stream carries the recorded commands' authored float fields
verbatim; floats round-trip bit-exactly and convert to fixed-point deterministically, so
they never break the guarantee — but the on-disk form is not float-free.)

| Verb (all Immediate) | Effect |
|---|---|
| `replay.record <name>` | Arms recording; captures the starting state and the per-tick server-input stream, and begins sampling the live tail hash. |
| `replay.stop` | Persists `<name>.puckreplay` (under `%LOCALAPPDATA%\Puck\World\Replays`) under the live tail hash, re-drives it once, and echoes the path, tick count, and **MATCH/MISMATCH** verdict. |
| `replay.cancel` | Aborts the active recording without persisting it. |
| `replay.verify <name>` | Rehydrates a fresh boot-image world from the recording, re-drives it offline, and echoes **MATCH** or **MISMATCH** with the recorded (live) and replayed tail hashes. |
| `replay.list` / `replay.status` | Lists saved recordings / reports mode, active name, and ticks captured. |

Immediate stdin verbs never reach the loopback, so the `replay.*` verbs never record
themselves; physical device input and Simulation-routed world verbs do, so a replay
re-drives the operator's driving and any world edits they made. World is not
determinism-gated (constraint 8) — the bit-for-bit guarantee on the underlying
`Puck.Commands` snapshot machinery is Post's, self-referential; this is the live
record/replay surface, the seed of a future `Puck.Replay`. **One capability loss vs. the
demo remains, deliberate and recorded (OQ-17, 2026-07-19):** the demo's
`tick.explain`/`tick.watch`/`hash.mark` divergence-introspection is not ported. Ruling
R-A (2026-07-19) brought World a fresh-world replay that re-drives through a FRESH world
and compares against the **live** session's tail hash — a genuine live-vs-replay fidelity
check. It holds for **boot-anchored** captures (the fresh world starts from the definition
boot image); a mid-session capture honestly reports MISMATCH until full per-body
record-start rehydration lands (the identified next lever). The demo's
`OverworldDeterminism` compared two fresh re-drives of the same stream; World's verdict is
strictly stronger (live-vs-replay) but not yet a superset across mid-session start points.

## The command wire (stdin format)

The console is a hot path — a flood corpus lands tens of thousands of `player.*`
lines in bursts — so the stdin wire is tuned to waste as few cycles and bytes as
possible per line. A plain `verb arg arg…` line takes a **span dispatch** fast
path in `CommandRegistry.Submit`: the line is tokenized into a `stackalloc`
`Span<Range>` (no `Split`, no substrings), the verb is looked up by its span
through a `FrozenDictionary` alternate lookup (the verb token never
materializes), a single cached `CommandContext` is reused, and the migrated
drive-a-player verbs (`player.run`/`fly`/`warp`/`face`/`pose`/`motion`/
`reconcile`/`where`/`stop`) receive their arguments as a zero-copy `WireArgs`
view that parses floats/ints straight off the line span. The dispatch allocates
**nothing** on the heap (only the sim's own `TapeSegment` and the inbound
`ReadLine` string remain, both out of scope). Quoted or `@`-response lines still
fall through to the full System.CommandLine parse, so all error text stays
identical.

`wire.ack [on|quiet]` is the response contract. Default **on** echoes every
accepted command (the default). **quiet** drops the SUCCESS acks of the
side-effecting wire verbs — so a 37k-line flood costs no acknowledgement bytes —
while errors (a bad arg count, an unparsable value, an unknown target) and query
verbs (`player.where`) ALWAYS echo, so a scripted run still reads back poses and
still sees its failures on a quiet pipe. In quiet mode a wire verb never even
builds the ack string, so the flood path stays zero-alloc. The flood corpus
sends `wire.ack quiet` in its setup; the parade keeps acks on for its byte-exact
transcript.

## Simulation authority

**Every one of the 128 entries is a simulated player advanced on the SERVER from
a `PlayerIntent` — no entity is pose-driven.** Each entry (seats included) owns
its own `WorldBody` in the entity table and integrates its pose from a merged
intent every tick. Drivers may only PRODUCE inputs: the client submits each
seat's device intent per tick, the console verbs reach all 128 over the command
wire (`player.run`/`warp`/`face`/`stop` — 1..4 seats, 5..128 population
entries), and the gentle wander is the server-side `IntentSource` producer
staged below the same submitted-intent stream a network peer's stick would feed.
**Poses are never accepted from outside the sim — the wire protocol is INTENTS,
and poses flow OUT via the tick snapshot.** A warp or a spawn is a
server-authoritative COMMAND, not a pose stream; a live tape on an entry
overrides its wander automatically (tape > submitted in the intent merge), so
`player.run 1 0 0 2 57` drives entry 57 exactly as a remote input would.

**Full range of motion (6DOF).** A player's pose is ALWAYS 6DOF — a free
`Vector3` position and a `Quaternion` attitude — so the platform hosts space sims
and momentum-heavy swimming, up to 128 craft flying with full 3D translation
and orientation, through this same intent wire and renderer (the renderer is
6DOF-native: `DynamicTransform` and camera `SdfAnchor` both take a
quaternion). What changes per entity is the
**motion model** the intent integrates under, set by `player.motion
[grounded|free] [player]`:

- **grounded** (the default) is the ground avatar — the byte-for-byte shipped
  math: yaw from the `Turn` rate, a planar step along the heading, Y pinned to
  the plane, a pure-yaw attitude. Every existing producer (keys, sticks, wander,
  tapes, the flood, the planar verbs) runs this and moves identically to before.
- **free** integrates all six intent channels in the BODY frame: linear velocity
  `(forward·facing + strafe·right + up·up)·MoveSpeed` with no ground pin, and
  yaw/pitch/roll rates composed into the attitude about the body's own axes — a
  banking craft's chase camera banks with it.

`PlayerIntent` gained `MoveUp`/`Pitch`/`Roll`, all defaulting to zero so every
planar producer stays byte-compatible. Two verbs fill the 6DOF channels:
`player.fly <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> [player]` is
the 6DOF tape segment (the flight twin of `player.run`), and `player.pose <x> <y>
<z> <yawDeg> <pitchDeg> <rollDeg> [player]` is the full-pose teleport
(`warp`/`face` remain the planar shorthands). Angles follow the codebase-wide
Tait-Bryan convention (yaw about world up, pitch about the body right, roll about
the body forward): `+pitch` is nose-up, `+roll` lifts the body's right, `+up`
climbs, `+forward` at level flies -Z. `player.where` reads the full pose back —
`pos=(x, y, z) yaw=ddd° pitch=ddd° roll=ddd°`, one format always (a grounded
entity prints `y=0.00 pitch=0 roll=0`). A model switch is authoritative like a
game-mode change (free→grounded snaps to the plane and levels the attitude);
render interpolation generalizes to a shortest-path orientation nlerp, and the
`reconcile` render-error eases an orientation error quaternion to identity
alongside the position offset.

**The locomotion kits (movement as data).** Every game-flavored movement noun is
a `WorldKit` ROW in `WorldDefinition.Kits` — the engine's vocabulary is intents +
parameterized integrators + abstract action channels; a kit row names a way of
moving: its `MotionModel` (an engine fact selected per row), its `MotionTuning`,
its wander-producer `WanderFlavor` (the drift/wave/bank/threshold/altitude
constants), and its action-lane bindings (`ActionSpec` compositions). Entities are
distributed across rows by the definition's `WorldRowAssignment` policy — `hash`
(the default R1 low-discrepancy mapping, `WorldPopulation.RowFor`, parameterized
by row count) or `table` (a `kit = Table[index % Table.Count]` cycle of kit
names, resolved to row indices once at construction); the
`world.population` census derives its names and counts from the rows, so the
default five (`flyer, swimmer, jumper, runner, kart`) echo byte-identically. The
snapshot carries each entity's kit row index (`EntitySnapshot.Kit` — drives
render selection, never who is driving). Seats construct from the
definition-designated `DefaultSeatKit` row (`runner` in the default world — the
default grounded tuning with the jump bound; a seated profile's speeds still
override live). A new way of moving is a new ROW, not an engine enum.

**Action channels (abstract lanes, kit-bound).** Buttons are a distinct input
currency from analog movement, so `PlayerIntent` carries an `ActionLanes Actions`
bitmask (one bit per channel; `Primary = 1`, `Secondary = 2`) alongside the six
movement channels — defaulting to `None`, so every movement-only producer (keys,
sticks, wander, tapes, the flood) stays byte-compatible. A channel is **not** an
opcode and there is deliberately no jump sim verb: a channel is a timed HELD
state on a separate **action track**, merged into the intent every sub-step
**independently of the movement tape** — so a tape-driven runner mid-`player.run`
can still fire, exactly as a real pad's buttons are independent of its sticks.
Two producers feed the track: live **edge** presses (`player.primary` on
Space/South, `player.secondary` on East — a button down until its release edge)
and **timed** presses (`player.press <lane> [holdSeconds] [player]`, the
scripted/wire path, which reads held for a clamped `0..2` s then auto-releases).

**The action vocabulary (what a press DOES is composed data).** A kit binds each
channel to an `ActionSpec` — a composition over the closed engine vocabulary,
never a bespoke engine kind:

- **Facts** (`ActionFact`, engine code): `Grounded`, `Airborne`, `Rising`,
  `Falling`. Admission rule: a new fact is privileged sim state the predicates/
  effects cannot derive; add only then.
- **Predicates** (`ActionPredicate`, data-composable gates; records only, no
  expression language): `Now(fact)`, `Recently(fact, window)` (coyote =
  `Recently(Grounded, w)`), `CooldownElapsed`, `UsesBelow(n)` (double-jump/
  air-dash budgets; uses reset on ground contact), `All(...)`.
- **Triggers**: `OnPress` carries a `latchSeconds` buffer — the press stays
  pending until its gate opens or the latch expires; `OnRelease` is the second
  channel, evaluated on the edge, never latched.
- **Effects** (`ActionEffect`, fixed-point ops at the tick boundary):
  `SetVerticalVelocity`, `ScaleVerticalVelocity` (the cut),
  `PlanarImpulse(bodyDir, speed, duration)` (a timed velocity overlay through
  its own accumulator — integration itself untouched), `StartCooldown`,
  `ConsumeUse`. RESERVED (named on the record, admission rule: new kind = new
  record member + fixed-point body support): `AttitudeBurst` (a barrel roll),
  `EmitWorldEvent` (a shoot — routes to the world-event seam when one exists).

The two Default-world compositions are the worked examples (`ActionSpec.Jump`/
`.Dash`/`.Surge` over a kit's tuning): **jump** (grounded kits + seats, Primary)
= press gated `All(Recently(Grounded, coyote), UsesBelow(1))` with the buffer
latch → `SetVerticalVelocity(impulse) + ConsumeUse`; release gated `Now(Rising)`
→ `ScaleVerticalVelocity(cut)` — value-for-value the shipped feel. **Dash**
(grounded kits, Secondary) = press gated `All(Now(Grounded), CooldownElapsed)` →
`PlanarImpulse(forward, 2.5× move speed, 0.25 s) + StartCooldown(1.5 s)`.
**Surge** (free kits, Secondary) = press gated `CooldownElapsed` →
`SetVerticalVelocity(impulse) + StartCooldown(1.5 s)` (the channel bleeds off at
the rise gravity under free flight) — two different behaviors from the same
primitives, zero new engine kinds. Rise/fall gravity multipliers stay in the
integrator/tuning: integration is mechanism, not policy.

**The jump feel kit.** The jump composition's read of the `Primary`
channel is
World's jump feel: a tight, responsive kit — a variable-height jump with asymmetric gravity (you fall
faster than you rise), a jump **cut** (releasing while still rising multiplies
up-velocity by `0.45`, so a tap is a short hop and a hold a full leap), and the
two forgiveness windows — **coyote time** (`0.09 s` grace to still jump just after
leaving ground) and **jump buffering** (`0.10 s` window before landing where a
press is remembered and fires on touchdown). Every SPATIAL constant is scaled by
one named factor — `MotionTuning.DefaultActionScale = 0.5` (`DefaultMoveSpeed 4`) — so
retuning the whole feel to a new scale is a single number. Linear scaling keeps
the PROPORTIONS: `JumpSpeed 5.5 u/s`, `RiseGravity 14`, `FallGravity 23`
give an apex of `≈1.08 u` over a `~1 u` avatar, rise `≈0.39 s`, total
air `≈0.70 s`; the cut ratio and the (time-valued) forgiveness windows are NOT
scaled. (The scale factor lives as `MotionTuning.DefaultActionScale` in
`WorldDefinition`.) Horizontal motion uses instant-velocity planar integration with no
separate ground or air acceleration. Vertical motion rides the pose's
Y through interpolation and `player.where` (a resting avatar reads `y=0.00`
exactly; airborne shows `y>0`). Coyote time is unreachable on the current flat
plane (a body only leaves the ground BY jumping, which zeroes the window) but the
window is kept so a future stepped/edged world gets ledge-jumps for free. Live:
**keyboard Space** and **gamepad South-when-active** both drive the Primary
channel (`player.primary`) on both edges for variable height (South stays the
confirm button while a seat is pending — one button seats a player and then acts
for them, no restart).

**Server corrections (`player.reconcile`).** `player.reconcile <x> <z>
<yawDegrees> [seconds] [player]` is the authority-snap/render-ease correction the
same 128-player wire carries alongside intents. The SIM pose snaps to the target
INSTANTLY — an end-state identical to a `warp`+`face`, so `player.where` reads the
corrected pose immediately — and the tick's snapshot carries an
`EntityContinuity.Correction` hint, so the CLIENT eases the *visual* error (its
last drawn pose minus authority) to zero over `[seconds]` (default 0.25, clamped
0.05..2), smoothstep-eased and decayed by the snapshot's engine-tick budget
so the settle is fixed-step-rate independent. A correction whose position error exceeds
`EntityContinuity.MaxSmoothError` (3 u — the shared server/client ceiling; the
client re-checks its own easer basis) reports a plain snapshot teleport and POPS
instead of gliding — the escape shipping
netcode uses so a respawn-scale jump reads as a cut, not a slide across the arena.
The eased offset is strictly client presentation: the sim never reads it and it is never
part of the pose flowing out to `player.where` — the wire stays intents +
corrections, poses flow OUT clean.

**Idle stand-ins (the sole-driver posture).** `world.population [count]
[idle|wander]` takes its two tokens in any order: a bare integer sets the active
stand-in COUNT (0..124; new activations take the stored peer-source default), a
bare `idle`/`wander` sets that default AND sweeps every peer's `IntentSource`
(last-writer-wins over any per-entity `player.control`). `wander` (the boot
default) has them gently drift as a living crowd;
`idle` silences the wander producer so a stand-in holds perfectly still until a
`player.run` segment drives it and returns to rest when it expires — making a
SCRIPTED CORPUS the sole driver of entries 5..128, the remote-server posture the
128-player stdin proof pipes (no wander drift to fight while reading poses back).
It is render-inert: the source reshapes only the intent producers, never the
declared set or palette, so it forces no program rebuild.

**Host-owned fixed-step simulation.** Puck.World registers the Launcher/Hosting
fixed-step easy path. Launcher alone defines the cadence and owns the ONE
wall-clock accumulator, attributes window, gamepad, and simulation-routed
console input to per-tick `CommandSnapshot`s, applies each snapshot, and invokes
`WorldSimulation` once per exact host step. The world has no rate constant or
second accumulator, and `WorldFrameSource` never advances authority. Player pose,
velocity, timers, tuning, wander, and intent are `Puck.Maths` fixed-point or
engine-tick durations; authored floats are compiled once at the boundary and
render floats never feed back. Integration and timers consume
`FixedStepContext.StepTicks`, so World never reconstructs Launcher's rate. A
one-second tape therefore consumes exactly 50,400 engine ticks and a
`player.run 1 0 0 0.2` at the default 4 u/s lands at exactly 0.8 u.
The shared snapshot/record/replay and binding machinery is gated by Post Tier A;
World-specific live behavior is still verified by running the game and its
proof feeder rather than adding a World-only Post stage or golden.

**Render interpolation.** At a present rate above the fixed-step rate, authority would judder
— the crowd would visibly step between sim ticks. So the client's `WorldClient`
view keeps each entity's PREVIOUS and CURRENT snapshot poses, and once per frame
— after the host's due ticks — the renderer
reads an INTERPOLATED pose: `Lerp(previous → current, alpha)` for position and a
shortest-path orientation nlerp, where `alpha` is the launcher's accumulator
residual over that same host step (both use the same `FixedStepContext`, so the
fraction the launcher banks toward the next step is exactly the fraction between
the world's last two sub-steps — they mirror by design). The interpolated poses
are what the avatars AND their camera rigs consume, so
the avatar and its chase camera track the same smooth pose. A snapshot entry
carrying `EntityContinuity.Teleport` (`warp`/`face`/`pose`/model switch, or an
over-ceiling `reconcile`) snaps both endpoints to the new pose so the client never
interpolates across a jump; a frame that banks zero sub-steps holds a stable lerp
(previous == current), never a snap-back. This is pure presentation: the wander's
steering and every `player.where` still read the raw SIM pose off the server
bodies — only the poses the renderer draws are interpolated.

## Graphics options (all live verbs, no-arg echoes current)

`world.quality low|medium|high` (preset) · `world.shadows off|low|medium|high|0..1|0%..100%
[crowd-radius]` (named notches alias continuous reach) · `world.ao on|off` · `world.render-scale
native|three-quarter|half|quarter|eighth|0.125..1|12.5%..100%` (named tiers or fine-grained internal-resolution sweep;
output reconstruction is controlled independently) · `world.upscale-sharpness
bilinear|balanced|sharp|0..1|0%..100%` (continuous bilinear-to-clamped-Catmull-Rom blend) ·
`world.target <positive-hz>|display` ·
`world.shadow-mask auto|exact|camera-tile` (live performance/visual A/B) ·
`world.shadow-march auto|exact|fast` (48-step/12-unit quality vs bounded 16-step/6-unit fleet path) ·
`world.ao-quality auto|exact|fast` (three-rung quality vs calibrated one-sample contact AO) ·
`world.view-refresh 1..8` (diegetic-view refresh divisor; default 4) ·
`world.debug-view off|depth|normals|raydir|material-id|iteration-count|termination|slice|mask|overshoot`
(live diagnostic output; `depth` isolates the primary march from post-hit shading) ·
`world.timing on|off` + `world.gpu` (per-pass GPU ms over the pipe).

The graphics API is the boot-time categorical choice `--backend directx|vulkan`.
Direct3D 12 is the default on Windows 10+; Vulkan is the default elsewhere and
remains the explicit comparison path on Windows. Backend selection builds the whole
render host on that API — neutral compute services, device, presenter, jumbotron
views, and SDF bytecode move together. It is intentionally not a live presenter-only
toggle because changing APIs requires rebuilding the render graph.

Swapchain mode is the boot-time categorical performance knob `--present-mode
vsync|mailbox|immediate|adaptive`; World defaults to measured-best `immediate` under VRR. It is frozen for the session because changing
it recreates the swapchain. The live `world.target` is a genuinely continuous fractional-Hz pacing control; named 60/120
tiers elsewhere are facades over the same control. Explicit targets are capped at the effective display ceiling. `display`
uses an explicit DisplayID/HDMI/FreeSync range when one is advertised, targets `max(Vmin, min(Vmax, active signal) - 3 Hz)`,
and otherwise follows the active physical signal without inventing a VRR floor.

The exact-128 built-in posture is four local humans + 124 autonomous network-human stand-ins: 2,041 dynamic leaf
instances and 10,205 authored avatar instructions before the static scene. The live proof treats 60 FPS as a floor,
not a dashboard number: after the mixed-role choreography reaches steady state, its last rolling two-second sample
must report both average and worst frame at or above the configured `--min-fps` threshold. The 120-Hz desktop posture
remains the quality target above that floor.
The Windows display query reads the active display path's physical signal rate, not the enumerated list of selectable
desktop modes (which can otherwise misreport a 120 Hz Dynamic Refresh Rate signal as 60).

The overhead and first-person feeds are presentation views: their last images persist between refreshes, and
`world.view-refresh 1` restores full-rate offscreen rendering for an A/B.

**Crowd shadows** are the 128-player lever: seats always cast soft shadows;
population entries cast only within `crowd-radius` (default 15) of a joined
seat — per-frame, no rebuild (the engine's `DynamicTransform.CastsSoftShadow`
lane, built for this project). Bounding who casts is what scales the crowd,
since soft shadows dominate the GPU cost; the remaining crowd cost is the
primary march. At 16 or more simulated stand-ins the world also reuses the
camera-tile instance mask for shadow rays instead of paying the exact per-pixel
shadow-grid gather; this deliberate fleet approximation can omit an off-camera
caster whose shadow reaches into the tile. Smaller sessions retain the exact
gather. `world.render-scale` is the available whole-frame scaling lever.
The same fleet threshold selects one-sample contact AO; it retains the crease/grounding cue while exact mode keeps the
full three-rung ladder for authoring and A/B comparison.

`world.upscale-sharpness 0` retains the four-tap bilinear fast path; any positive value enables a clamped Catmull-Rom
path and continuously blends toward it, while native scale remains an exact copy. Render scale and shadow reach are
genuinely continuous: named tiers are only console/preset facades over live float values. Do not assume lower scale is
monotonic for a large instance field: fewer/wider tiles can admit more leaves into each mask and make mask/beam more
expensive. Always read both `world.gpu` and `world.fps` at the intended population/view layout.

Arming `world.timing` also publishes worst-of-60 CPU frame tiles (pump, clock, input snapshot, command application,
simulation callback/overhead, output, GPU drain, produce, present, post-present, and pacer), CLR pause correlation, and a
World-simulation split across population/roster/machines. That probe isolated a repeatable one-time 15–18 ms pump-thread
suspension around 26–30 seconds to Dynamic PGO's mid-session tier transition—not GC or simulation work. Puck.World
therefore sets `TieredPGO=false` in its generated runtime configuration while retaining ordinary tiered compilation,
removing the mid-session hitch.

## The diegetic screens

Three primitives, cleanly split: a **surface** (a `WorldScreen` slab — its world
frame `Origin`/`Right`/`Up` + half-extents, the sampled face), a **source** (the
signal it carries — a closed `WorldScreenSource` hierarchy), and a **route** (a
`WorldScreenRoute` — whether a player may ENGAGE it and within what radius).
Screens are pure DATA in `WorldDefinition`; the world AUTHORS nothing — it
declares which producer feeds a slot and the engine samples it. `WorldScreenBinder`
is the per-slot hypervisor that binds each declared index to a live GPU source.

`WorldDefinition.Default` is the built-in five-screen broadcast plaza. Screen 0
is a diegetic overhead-camera display and doubles as the runtime-overwritable
machine bay; screen 1 is the live self-capture of the game's own "Puck: World"
window; screen 2 is THE JUMBOTRON, rendered from a diegetic camera attached to
an avatar eyeball; screen
3 is the live webcam; screen 4 is the native ARM7TDMI AdvancedGamingBrick engine,
asset-free by default (no bundled cartridge, no BIOS dump baked into the repo) —
it boots into the binder's graceful "no content configured" fault until a real
deployment supplies a cartridge path (`screen.insert 4 <path> advanced-gaming-brick`
live, or a future data-file loader). The engine's `direct` option is its default
and selects the legal zeroed replacement BIOS; `bios=<path>` opts into an explicit
image. The two view-fed screens obey `ViewStack`'s self-reference rule, so neither
recursively samples its own face and compounds feedback.

Five source kinds, one adapter (`CpuSurfaceSource`) under the pixel-fed ones:

- **test-pattern** — a deterministic animated card off the sim tick (never the
  wall clock), still available as a runtime source even though the built-in
  screen 0 now carries the overhead view.
- **machine** — a booted deterministic machine, resolved against a registered
  `IScreenMachineEngine` by id (`gaming-brick` for the SM83 family and
  `advanced-gaming-brick` for the native ARM7TDMI machine; another engine registers alongside —
  the world never names a concrete machine). Engageable,
  so a player's intent rides the SAME wire onto it (`player.engage`); `world.screens`
  reads it `machine:<engine>`.
- **camera** — the live webcam (Media Foundation CPU tier). The source declares
  a preferred extent and maximum cadence. ONE session is opened engine-wide and
  SHARED by every camera screen (two sessions on one device flicker); multiple
  declarations combine into the richest requested envelope, and uploads skip
  unchanged `FrameVersion`s.
- **capture** — a live compositor capture through Windows Graphics Capture on Windows 10 2004 (build 19041) or newer;
  non-Windows hosts report the source unsupported. The selector rides the one `Capture` source: a desktop window by
  title fragment (window mode), or a whole monitor by 0-based index (0 = primary, monitor mode; the `MonitorIndex`
  field null means window mode). The compositor-owned feed fixes its output extent and cadence, publishes only completed
  latest-result-wins BGRA frames, and never waits on the target or capture work from the render pump. World resolves the
  target when the feed starts and may reacquire a returning window title or a reconnected monitor only after disposing
  an ended feed. On the Direct3D 12 host the feed publishes GPU-side into three shared simultaneous-access textures the
  screen samples directly (zero-copy, opened on the render adapter; divided-cadence CPU readbacks still feed the room
  glow); the Vulkan host keeps the CPU-pixel transport, and camera capture stays CPU on both.
- **view** — a JUMBOTRON: this same world rendered from a placeable
  `WorldCamera` (`WorldDefinition.Cameras`) through an engine `ViewStack`; every
  camera declares its own render extent and vertical FOV. One
  registered view = one extra offscreen world render per frame (~0.09 ms at the
  native panel size); the screen's own face binds nothing inside that render
  (the self-reference rule), so no feedback compounds.

A **machine** is the engine-neutral primitive `Puck.Abstractions.Machines`
defines: `IScreenMachine` (assigned/content state, a per-frame `Step(deltaTicks,
MachinePadState)`, a native framebuffer handle, emitted light, publish/device-lost/
save-flush), an optional `IMachineMemoryPeek` capability (the `screen.peek` read), an
optional `IAudioMachine` capability (`SampleRate` + `ReadSamples`, gated on host
attachment — no device drains it in World today, see "Known screen limitations"),
and `IScreenMachineEngine` (the factory: a kebab `Id` + `Create(options, bytes,
savePath)`, each engine owning its options vocabulary). `Puck.GamingBricks`
implements the SM83 family (`GamingBrickEngine` → `MachineHost`), while
`Puck.AdvancedGamingBricks` adapts the native ARM7TDMI core
(`AdvancedGamingBrickEngine` → `AdvancedMachineHost`). `Program.cs` registers
both; the binder keeps that DI-collected registry and resolves a declared or
inserted machine by engine id.

Precedence is **Machine > Camera > Capture > View > Pattern**, so a runtime source
overlays the declared one and an eject reveals it again. An unbound slot gets the
engine's procedural no-signal card — never black. Live-feed deadlines are
source-owned and measured in exact engine ticks (a screen holds its last frame
in between; the GPU handle stays stable). A missing device / a window not found /
an unknown view camera is a loud fault visible in `world.screens` and
`screen.state` (on a machine with no webcam, screen 3 reads `camera unbound` and
`screen.state` carries `no camera device present` — loud data, no crash).

**The engage contract.** A machine screen's `route` makes it engageable:
`player.engage <i> [player]` walks a player up and diverts their intent wire onto the
machine — the SAME `PlayerIntent` currency that drives their avatar now feeds the
machine, so no second input path exists. The world translates the intent ONCE,
generically, into a neutral `MachinePadState` (movement channels → the left stick
verbatim, the Jump lane → South); the machine's engine folds THAT down to its own
buttons (the gaming-brick's `BrickPad` quantizes the stick to the d-pad and reads
South→A / East→B / Start / Back→Select). So a forward run → left-stick forward → the
console d-pad Up, and a jump press → South → A. It is mechanical data-following,
checked in order: the route must permit engagement, the slot must carry a machine (an
engageable `None`/test-pattern screen errors loudly — nothing to control), and the
avatar must be within the route's radius of the screen origin (a planar distance).
Engagement is **multiplayer**: every engaged player's pad image merges into one per
screen (buttons OR, stick axes sum and clamp — the shared-cabinet shape), and
`player.disengage <i>` drops a player and clears any residual held input so nothing
leaks across the boundary. A disengaged player drives their avatar again with no
restart.

Live verbs (all wire-native, `wire.ack quiet` drops only successes):
`screen.insert <i> <content> [engine] [options…]` (engine omitted → the sole
registered one; `gaming-brick` options are `dmg|cgb|agb` + `dmgspeed`, and
`advanced-gaming-brick` accepts `direct` or `bios=<path>`) ·
`screen.camera <i>` ·
`screen.capture <i> <windowTitle…>` · `screen.desktop <i> [monitorIndex]` (whole-monitor capture; monitorIndex
defaults to 0 = primary) · `screen.view <i> <cameraName>` ·
`screen.eject <i>` (clears ANY producer kind) · `player.engage <i> [player]` /
`player.disengage <i> [player]` · queries `screen.state <i>` /
`screen.peek <i> <addr>` · `world.screens` (the whole listing). Verify with
`dotnet run src/Puck.World/scripts/proof.cs -- screens`.

**Measured cost.** The current five-screen plaza carries two offscreen world
views (overhead + avatar eye), the native AGB screen (unconfigured/idle unless a
cartridge is inserted, live or via `screen.insert`), the webcam, and asynchronous
self-window capture. Measure the complete default posture with the proof runner:
its final rolling sample must keep both average and worst frame at or above 60
FPS. With a cartridge live on screen 4 (`screen.insert 4 <path>
advanced-gaming-brick`), the reference 1280x800 Direct3D run holds comfortably
above the floor; the AGB completes its fixed steps with **0 pending**, proving
real-time emulation rather than render-only decoupling. When stepping, the native AGB owns a single ordered
emulation worker and swaps complete 240x160 frames at the native ~59.7 Hz video
cadence, preserving every exact tick/input segment without letting a cartridge
stall presentation. The two 256x144 diegetic world-camera views hold their last
complete frames between the default every-fourth-frame refreshes. CPU-fed screens
upload through `CpuSurfaceSource` or the machine's equivalent native-frame
upload; window grabs stay on their dedicated latest-result-wins worker too.

**Known screen limitations.** One seam is declared but not implemented:
**screen transforms** — a moving/re-posed screen slab is expressible (the surface carries a
full world frame) but unused by World so far, so every screen is static and
world-axis aligned. The neutral `MachineButtons` image already carries the
East/West/North/shoulder/Start/Back buttons the gaming-brick maps South→A / East→B /
Start / Back→Select; the growth point is the `PlayerIntent` action-lane vocabulary
(only the Jump lane fills a button today — new lanes light up the rest in
`WorldEngagement.Translate`, not a per-button verb).

## Audio (the mixer core, the world data model, and the device)

`Audio/` holds the pure mixing core; the world data model sits on top of it,
and the WASAPI device (below) drives both without reshaping either.

**The rate.** 48000 Hz, fixed: device-native, exactly **200 frames
per 240 Hz sim step** (`WorldAudioMixer.FramesPerSimStep`), 21/20 engine ticks
per audio frame. Machines configure to 48000 directly — the only resampler in
the machine path is the core's own exact-rational one.

**The mix law** (`WorldAudioMixer.MixBlock`) is fixed-point end to end:
s16 samples × Q16 composite gains → int32 accumulate → a deterministic
polynomial soft-clip → s16. Per block, each emitter derives TARGET
coefficients from the `WorldAudioSnapshot` — finite-support squared-smoothstep
attenuation (smoothstep over the SQUARED-distance ratio between
`MinRadius²`/`MaxRadius²`: no square root, and the support's zero IS the
cull — a fully-silent emitter is bit-identical to an absent one and its source
is never pulled), equal-power pan from listener-relative azimuth (one
`FixedQ4816.SinCos` per point emitter; beds center-pan with a
`FadeFrames`-bounded presence slew) — and the LIVE coefficients ramp linearly
across the block from the previous block's values (the zipper-noise killer;
ramp state keys on stable emitter ids). Sources are shared identities: each
distinct `WorldAudioSourceKey` is pulled ONCE per block through the
`IAudioBlockSource` seam and every feed taps the scratch (`left|right|mix`) —
a stereo pair is two rows sharing one source. The soft-clip is the smooth-knee
cubic `y = H + G·(1 − (1 − t)³)`: bit-transparent to `H = 24575` (0.75 FS),
C¹-saturating over `t = (|s| − H)/24576` into the `32767` ceiling at 1.5 FS —
never a libm call.

**The synth** (`WorldVoiceSynth`): 32 fixed-struct voices, zero
steady-state alloc — sine as a `FixedComplex` rotor, Q32 phase-accumulator
pulse/saw/triangle, seeded `Pcg32XshRr` noise with a one-pole tilt, ADSR in
sample units, control-rate (64-sample) pitch sweep + triangle vibrato, one
Chamberlin SVF per voice. Triggers ride snapshots with strictly increasing
sequence numbers (once-only under snapshot hold); allocation steals the
QUIETEST voice, oldest on ties. Patches arrive as post-`Normalize`
`puck.synth.v1` documents, flattened once at registration
(`WorldVoicePatch.FromDocument`).

**The two-driver contract**: `MixBlock` is synchronous and owns no
thread. The offline proof (`scripts/audio-mix.cs`) and the future device pump
are two drivers of the same code — the proof drives tune audio through a
SYNCHRONOUS headless Humble core (never `QueuedMachineWorker`), steps the
scripted pose table at the sim cadence, and SHA-256-hashes the raw s16 PCM.

**The PCM-hash doctrine.** The proof's printed PCM hashes are
self-referential: they show the whole path is deterministic (two full fresh
runs agree bit for bit), never that history is preserved — a deliberate
mix-law correction is EXPECTED to change them; re-run and take the new values.
They are a harness observation, **not a landing gate** (see the proof-suite note).
Verify with `dotnet run src/Puck.World/scripts/audio-mix.cs` (the two hash
proofs plus structural batteries: pan geometry, the cull contract, single-pull,
soft-clip exactness, ramp bounds, seeded-synth reproducibility, voice steal,
SVF, bed fade, and the world-document fixture pipeline).

**The world data model.** Sound is DOCUMENT data: `Speakers`
(`WorldSpeaker`, `$type fixed|anchored|bed` — the camera family's audio
sibling; a feed is a shared source identity + `mix|left|right` selector +
gain, and anchored rows ride the shared `WorldAnchor` union, placements
included), `Tunes` / `Patches` (inline-canonical `puck.audio.v1` /
`puck.synth.v1` assets with pinned hashes — the same hash-pin/canonicalize
contract as creation rows: a foreign hash rejects loudly at the compose boundary, the validator
recomputes the pin, `world.save` re-canonicalizes), the `Audio` defaults row
(master gain, attenuation coalescing, bed fade, the `focus|seat:<n>|<camera>`
listener policy), nullable `Emission` facets on scene rows and placements
(root-only under repeat), and `CreationBehaviorDocument.Sounds` (inline synth
voices a placement auto-surfaces as emitters). `Client/WorldAudioDirector`
derives the emitter table from the delivered definition with STABLE ids (a
property edit keeps its id; a kind/anchor/source-identity change — asset hash
included — re-enters from silence), resolves poses per produced frame (entity
leaves from the REAL packed gait-swung transforms; placements from the
stamped/animated transform), fires one seeded trigger per synth-fed emitter
arrival (`SubmitTrigger` is the ONE trigger-production seam the cue
producers reuse), and publishes `WorldAudioSnapshot`s over a 4-slab rotation.
Tune hosting (`TuneMachineSource` — the `Puck.Forge` compile chain over a
synchronous Humble core) acquires while referenced and releases when
orphaned, active while a mixer is attached (the device pump live; the offline
proof headlessly). Verbs: `world.speaker.set/remove`, `world.tune.set/remove`,
`world.patch.set/remove`, `world.audio.set`, `world.speakers`,
`audio.emitters` (the deterministic derived-table dump), `audio.state`
(the live device echo, below), `speaker.state` (the per-row live status +
transient-cue tail), and `world.volume` (the session lever). The
document-side battery is
`dotnet run src/Puck.World/scripts/proof.cs -- audio`.

**The device.** `Audio/WorldAudioRenderService` is the world speaker —
an `IHostedService` (one dedicated bounded-join worker owns the device
lifecycle, never a bare DI singleton) owning ONE mixer and one **governor thread**: it opens the
default render endpoint through the `Puck.Platform.Audio` factory seam
(`AudioRenderPlatform.CreateFactory()` — `null` off Windows, and the service
parks as `unsupported` without starting a thread), attaches the mixer to the
director on success, and watches the stream. The Windows device
(`WasapiAudioRenderDevice`, sharing its threading shape with the capture source)
owns a second, dedicated MTA COM thread: enumerator → Activate →
`Initialize(Shared, event-driven)` → `GetService(IAudioRenderClient)`, an
init handshake, a bounded join on dispose. It requests OUR s16/stereo/48000
format with `AUTOCONVERTPCM|SRC_DEFAULT_QUALITY` — on real endpoints 48000 is
the shared-mode native rate so the convert is the trivial s16→float widen;
the SRC flags are only the exotic-endpoint net. Per event wake the pump reads
`GetCurrentPadding` and fills the free space in ≤256-frame quanta
(`WorldAudioMixer.MaxBlockFrames`) DIRECTLY into `GetBuffer`'s mapping — zero
copies, zero steady-state allocation — through
`WorldAudioDirector.TryMixBlock` (latest-snapshot hold + `MixBlock` under the
director's one reentrant gate; the gate also serializes reconciles and
machine rebinds against the pump, uncontended in steady state).

**Failure posture: plays silent, never crashes.** Any failing HRESULT
(mid-stream device invalidation included) parks the device pump, the governor
detaches the mixer, and the service retries the DEFAULT endpoint every ~1 s
(`RebindPeriodMilliseconds` — a contract invariant); a fill-callback defect
degrades to one silent quantum and a counted fault, never a dead stream.
`audio.state` echoes the whole story — device token
(`playing|silent|rebinding|unsupported|stopped`), frames delivered across
device generations, rebind attempts, fill faults, bound sources, live
voices, the monotone output-peak meter, dropped triggers, derived emitters,
and the last fault.

**Machine audio is ALWAYS ON** (a flagged engine seam):
`IScreenMachineEngine.Create` carries `audioSampleRate` and World boots every
screen machine at 48000, accepting ~192 KB of ring + low-single-digit % CPU
per booted machine so speakers bind at ANY time without a machine reboot
(emulator snapshots are provably unaffected — core audio carries no state).
The director resolves the binder's live machines by REFERENCE each produced
frame (`MachineSourceResolver` → `WorldScreenBinder.AudioMachine`): a
boot/eject/live-swap rebinds the stable `machine:<slot>` source key, and a
cartridge booting late into an already-referenced slot self-heals with no
verb. The live smoke is
`dotnet run src/Puck.World/scripts/audio-device.cs` — in-process failure
paths against mock factories (unsupported / declining-with-rebinds /
mid-stream fault + reattach), the real endpoint delivering frames, and the
full session: speaker row first, cartridge second, `audio.state`'s sources
and peak going live — structural liveness only (sample content stays the
offline hash proof's job) — plus the `screen.boot` cue firing on the real
cartridge boot.

**THE CUE TABLE.** World events tie to sound as DATA: the
`Audio` row's `cues` list — `(event, patchId, gainThousandths?, placement)`
where placement is `at-site` (spatial, at the event's world position — the
shimmer's audio twin; falls back to the listener when no site is derivable),
`listener` (rides the listener pose: distance 0 = full gain, the mixer's
on-top-of-listener pan hold centers it), or `emitter:<speaker>` (the named
speaker's pose and support). Event tokens are a CLOSED published vocabulary
(`WorldAudioCue.EventTokens`): `mutation.applied`,
`mutation.rejected`, `grant.denied`, `player.footstep` (gait-phase derived,
local seats, one footfall per half-cycle), `screen.boot`, `screen.fault`,
`seat.join`, plus `player.jump`/`player.land` (RESERVED — no producer wired;
the client view carries no grounded signal). Mechanics: each fired cue takes
one slot of a **4-deep reserved transient-emitter pool** (charged off the
32-row snapshot cap so a full plan can never starve a cue; nearest-expiry
eviction at capacity), lives its patch's own envelope (looping patches take
the 2 s invariant cap), and lands with its trigger in the SAME snapshot so
voice release can never race. Producers feed
`WorldAudioDirector.SubmitCue` — the edit-echo lane, the binder's machine
lifecycle tap, the frame source's gait/roster edges. `speaker.state` echoes
each speaker row's live status (bound/silent/faulted, resolved pos,
`inMix`) plus the live transient tail (`cue:<token>=<patch>`).

**Speakers in the editor.** `(speakers, name)` joins the selection
vocabulary (Fixed/Bed pick by proxy spheres, drag through the selection/drag channel
with whole-row `UpsertSpeaker` on release; anchored rows edit their OFFSET
via `editor.move`/numeric verbs — they never drag), `editor.speaker.place/
move/gain/channel/radius/delete` are the name-addressed numeric twins
(console-only — every place-page chord slot is honestly spoken for), and
editor mode renders overlay GIZMO chips at each speaker's resolved pose
(the director's own anchor resolution; selection lights the accent tier, a
change shimmer the held tier, beds carry a translucent radius ring) through
`Puck.Overlays.EditorGizmoStore/Writer` — a new writer plus two grammar
icons and one RING element kind, never a world-geometry marker (the frozen
render envelope stays untouched).

**The master-volume lever.** `world.volume <0..8>` is session state on
the render-levers asymmetry: the document's `audio.masterGain` owns boot
(and flows live until the lever first engages), the lever owns "now"
thereafter, `world.save` folds it back into `audio.masterGain`
(`WorldSessionCapture` — `world.status` names the `audio` drift dimension).

## Engine boundaries worth knowing

- **16384 instances**: mask-first cull measured at the cap; mask memory ~41 MB
  static; per-tile mask width scales with **declared** (not active) instances —
  which is why this project emits active avatars only and holds the 128
  worst case via capacity floors probed at construction.
- **Soft-shadow gather tier**: the per-pixel shadow cull addresses ≤1024
  instances; beyond that it falls back (bit-identical, cheaper-but-coarser
  camera-tile masking). A near-16384 scene keeps correct shadows at the coarser
  tier.
- **`ViewStack.MaxRegisteredViews = 64`**: far above the 4 local viewports; do
  not register a rendered view per population entry (spectate-any-of-128 would
  need a raise or a different shape).
- **XInput = 4 Xbox-family pads locally** (a Windows API bound that happens to
  equal the local-seat cap; HID pads are uncapped).

## Settled questions — do not re-litigate

These were argued, verified against the tree, and decided while the moldable-state
and UI/editor substrates were built. Re-deriving or re-flagging them wastes a
session; changing one is a deliberate decision, not a cleanup.

**The standing genre-neutrality audit.** Every new or changed contract surface a
future arc proposes — an editor message, a UI binding surface, a genre-specific
verb — must answer: **"Would an RTS / FPS / RPG / MMO / puzzle world need a
different *message* here, or just different *data*?"** If the answer is "a
different message," the surface is wrong: generalize it, or move the specificity
into data. This is the calcification audit applied to the wire, and it did not
retire with the arc that introduced it.

**Identity and extensibility conventions.** Every row a document carries is
addressed by a string id (screens are position-addressed by index): kits, screens,
cameras, spawn points, boulders, addon rows, binding-overlay rows, profiles.
Mutations target ids; ids never carry meaning beyond identity. Both document
families carry `[JsonExtensionData] Extensions` bags — `puck.world.def.v1` at the
document level, `puck.world.player.v1` at both the document and per-profile level
— the same posture as `PuckRunDocument`. Unknown sections and fields survive a
round-trip untouched; that is the data-side plugin story until the addon ABI grows
host imports.

**Decided, with the reason:**

- The binding-profile stack (`BindingProfileDocument`, `BindingProfile.Compile`,
  `CompiledBindingProfile`, `PagedInputBindings` with per-slot chords) lives in
  `Puck.Commands`, public. There is nothing to lift and no copy to migrate.
- Compiled-level `LayeredInputBindings` is **not** the per-entry overlay
  primitive — it composes wholesale per `(slot, source)`. Document pre-merge keyed
  `(page id + ordered chord, source)` is the rule. Do not swap back.
- The player scope is a **catalog** of seat-selectable profiles, not a
  single-person record. Flattening it regresses couch co-op.
- Storage version tokens are opaque: they **guard** (if-match), they cannot
  **order**. `Revision` + `UpdatedAtUtc` orders. Two mechanisms, never conflated.
- Section-granular protocol messages do **not** imply section-granular storage.
  The storage grain is per-profile blobs; same-profile cross-device concurrency is
  whole-profile last-writer-wins with detection.
- Azurite's connection-string path carries no Entra principal — it can prove blob
  mechanics, never the identity resolver.
- Ownership latching is unified through principals and grants. There were exactly
  three ad-hoc precedents (`WorldEngagement`, machine-input ownership,
  `AddonHost.SlotOwner`); do not invent a fourth.
- `WorldServer.Step`'s buffered drain fits mutations as-is — mutations buffer like
  intents. Commands applying in the pre-Step window (read-after-write inside one
  stdin batch) is a documented deviation, not a bug in the command path.
- `puck.run.v1` is not the player-data home.
- `Guid.ToString()` is a valid Azure container name — verified against Azure's
  container-naming rules. The storage target and the claims-based resolver both
  need the container-id shape; do not re-derive it.

**Steady-state performance contracts for this substrate.** These are narrower than
the repo's general doctrine and specific to the moldable-state pipeline:

- The per-tick pipeline — intent fold → sim step → snapshot emit → binding
  resolution — **allocates nothing.** Document and JSON work is confined to
  boundaries: load, save, and mutation application.
- Binding composition compiles **once per change** (then
  `PagedInputBindings.Reload`), **never per frame.**
- Mutation application **rebuilds only the derived state of the changed section**,
  never the whole document's.

**Accepted asymmetries, by design:**

- **Cameras are document-only.** `world.camera.set`/`.remove` upserts the row; the
  change applies at next boot, not live.
- **Screen-source live-apply is index-scoped** — live for existing screen indices,
  with geometry fully live. Population, render, and camera defaults are
  document-only edits (their *session* state still folds back on `world.save`).
  **Addons are the exception and are not in this by-design list** — their
  boot-only mounting is deferred work with a prerequisite, under Known
  limitations below.
- **`BindingCommandSource` is dormant in World.** The dispatch path exists and
  derives from the same composed base layers, but `InputRouter` owns all physical
  input, so nothing drives it today.
- **Per-profile `Edit` subjects are not granular.** `Edit` scopes to a section kind
  (identity/motion/bindings/prefs), not to which catalog profile is being edited.
  Finer-grained per-profile trust is unbuilt.

## Known limitations

- **Addon mounting is boot-time only. This is DEFERRED WORK WITH A PREREQUISITE,
  not a by-design asymmetry** (owner ruling, 2026-07-19).
  `world.addon.set`/`.remove` edits and journals the document row correctly, but
  `WorldAddonDriver.Create` mounts only the **enabled** rows of the definition it
  is constructed with, and it is constructed once, at `Program.cs:282` — so a new
  or newly enabled addon does not mount until **save + relaunch**. Closing this
  means either a `world.addon.reload` verb or remounting through
  `WorldAddonDriver` on `UpsertAddon`. **The prerequisite is deciding what a
  mid-session driver swap means for the principal's grants and for the body's
  in-flight state**, plus an `AddonHost` per-instance teardown proof. That
  prerequisite is why it has not been built — not a decision that it should not
  be.
- **A cartridge DECLARED in the world document fires no `screen.boot` cue.**
  Constructor-time declared machine boots run before the binder lifecycle tap is
  wired, so the cue producer never observes them. Runtime inserts and
  reconcile-driven source changes all fire the cue correctly. This is an
  asymmetry to fix, not an intended distinction.
- A signal from an unbound control or inactive binding map can reserve an input
  slot even though it dispatches no command. The reservation remains until the
  device mapping is replaced or the session restarts.
- Simulation mutations are applied on fixed ticks. A query issued immediately
  after a mutating console command can observe the state from before that tick;
  wait for the next tick before relying on the query result.
- The first South-button press from a new controller can both seat the player
  and activate Jump because seating and command dispatch consume the same
  snapshot.
- Losing window focus can prevent release or cancellation edges from reaching
  the input router. A held simulation action may remain active until another
  edge clears it or the session restarts.
- **Authoring gestures sit outside the simulation-replay contract.** A committed
  mutation and the journal are deterministic once the final row exists, but stick
  drag integrates *presentation* `deltaSeconds` and then persists the resulting
  float row — so replaying identical command snapshots need not reproduce the
  authored coordinates. This is the presentation/artistic exception applied
  deliberately to edit gestures. If a future arc needs gestures themselves to
  replay to the same coordinates, integrate them at fixed ticks instead; do not
  assume today's drag path already does.

## Verifying

Run it: `dotnet run --project src/Puck.World -c Release` (`--backend directx|vulkan`,
`--width/--height`, `--exit-after-seconds`). Greenfield: verified by running and by piped scripts —
no Post stages, no `--validate` flags. A typical assertion session over stdin:
`world.timing on` → `world.population 124` → `world.gpu` → `player.run 1 0 0 2`
→ `player.where`. Engine seams this project drove (viewport-capacity floor,
binding `AnyModifiers`, shadow participation, `WithTrailingArgs`) are gated by
the Post battery as usual.

## The proof suite (`scripts/proof.cs`)

> **Goldens are not a gate (owner ruling, 2026-07-20).** World has no golden
> corpus and does not depend on one yet. Byte-identity checks — the ouroboros
> load→save round-trip, `git diff --exit-code` on the shipped worlds, "re-golden
> the baseline" — are **observations, never acceptance criteria** for World
> feature work; verification is by RUNNING the game and driving stdin verbs. If a
> shipped world's JSON moves as a side effect of a landing, note it and move on.
> The idea is kept: when the data settles, golden replays and baselines become
> worth building. `Puck.Post`'s engine-tier batteries are a separate thing and are
> untouched. Full ruling: **R18** in
> [docs/demo-to-world-port-plan.md](../../docs/demo-to-world-port-plan.md).

The proof tooling is ONE .NET 10 **file-based app** — no project, no NuGet, run
straight off the source (as the rest of the codebase prefers over PowerShell):

```
dotnet run src/Puck.World/scripts/proof.cs -- <subcommand> [options]
```

It is the reference "N remote players" session: 1–4 live local seats versus a
scripted corpus driving entities 5..128 over stdin — the console standing in for
the remote server, sending **inputs only**, per the simulation-authority
contract. Nineteen subcommands:

- **`generate --kind parade|flood|flight|hop|expo`** `[--population N] [--seed S]
  [--out PATH]` (+ flood knobs `--duration/--control-rate/--arena/--correction-interval`)
  — emits a timed STDIN corpus. Deterministic per `--seed` (each entity draws
  from `Random(seed*1000 + n)`); `--out` writes a file, else it flows to stdout.
- **`run`** `[--corpus PATH | --kind K] [--headless] [--loop] [--quality
  low|medium|high] [--width W] [--height H] [--no-build] [--tolerance T]
  [--yaw-tolerance Y] [--min-fps FPS] [--log PATH] [--world-arg PATH]` — the
  feeder: builds Puck.World (skip with
  `--no-build`), launches it, paces the corpus into stdin by wall clock
  (group-writing every due line in ONE pipe write), collects stdout/stderr on a
  reader thread per stream (no PowerShell event-queue ceiling under a 38k-line
  flood burst), MARKS each sweep and asserts it, prints the `world.fps`/
  `world.gpu` evidence block, then parses the **last** rolling `world.fps`
  sample and fails unless both its average and worst frame are at least
  `--min-fps` (default 60; pass 0 to disable). `--world-arg` forwards a
  `--world <path>` argument to the launched child (e.g. to force the
  baked-default fallback with a nonexistent path). It writes a transcript under
  `%LOCALAPPDATA%\Puck\World\proof-logs`, and returns a `0/1` exit code.
  `--headless` = one pass + exit code (the agent/CI posture); default = LIVE (the
  window stays up — grab pads for seats 1–4), `--loop` repeats the choreography
  every cycle, asserting each pass. **Default kind when nothing is specified:
  `expo`.** The child owns a GPU device and is NEVER orphaned — `Ctrl+C`,
  process-exit, and the `finally` all kill the whole tree.
- **`compare --reference A --candidate B`** `[--tolerance T] [--yaw-tolerance Y]`
  — the correctness bar for a stochastic corpus: rerun byte/near-identity of two
  transcripts' final `player.where` sweeps, plus the crowd's dispersion
  statistics (centroid, radius quantiles, rms) for the coverage claim.
- **`screens`** `[--width W] [--height H] [--no-build] [--rom PATH]` — the
  diegetic machine-screen route proof; it also checks that passive screen 0's
  overhead view rejects engagement.
- **`worlddoc`** `[--no-build] [--width W] [--height H] [--exit-after-seconds
  N]` — the world-document proof for `puck.world.def.v1` (informational, not a
  gate): (a) the **ouroboros round-trip** — `world.save` on EVERY checked-in world (`default`, `kart-remap`, and
  `expo`) reproduces it byte-for-byte, and saving THAT copy again reproduces it a
  second time (so a save that folds session state stays idempotent on a
  fresh boot, for each); (b) **baked-default parity** — a `--kind hop` feeder run
  against the checked-in file and one against a nonexistent `--world` path
  (forcing the loud fallback) compare byte-identical on their final sweeps, with
  the `[world] definition: baked default (...)` line present only in the fallback
  run's transcript.
- **`mutate`** `[--no-build] [--width W] [--height H] [--exit-after-seconds N]`
  — the mutation-vocabulary round-trip proof: (a) a scripted
  `world.kit.tune` → `world.status` (dirty 1) → `world.undo` → `world.status`
  (dirty 0) → `world.kit.tune` → `world.save <temp>` → `world.status` (dirty 0,
  compacted) round-trip, asserting the journal-length `dirty` counter at every
  step and the server's loud accept/undo lines; (b) **rejection honesty** —
  `world.kit.remove` on the `DefaultSeatKit` is rejected loudly (the
  `defaultSeatKit` validator invariant) and `world.status` still reports the
  unchanged kit count; (c) **survival** — relaunching `--world <the saved
  file>` boots it (the `[world] definition:` line names the path) and the
  saved JSON carries the tuned value while untouched kits keep theirs. The
  protocol-version handshake's rejection path is proven by the implementing
  session, not re-covered here (no scripted `Join` exists over stdin without a
  debug verb this proof does not add).
- **`grants`** `[--no-build] [--width W] [--height H] [--exit-after-seconds N]`
  — the principals/capability-grants proof: (a) `world.addon.set` mounts an authored `autopilot` `WorldAddonRow`
  and `world.save` writes it (session A; data-only — the driver mounts
  enabled rows only at boot); (b) relaunching `--world <the saved file>`
  (session B) asserts the `[world.addon: mounted autopilot ...]` boot line,
  then `world.grant addon:autopilot drive body:<n> exclusive` is asserted to
  move the granted body (two `player.where` samples a second apart);
  (c) `world.revoke` mid-run asserts the edge-latched `[world.grant
  denied: ...]` line and that the body then holds perfectly still (two more
  samples, identical); (d) **denied-mutation honesty** — `world.revoke
  console mutate section:kits` makes `world.kit.tune` fail loudly with
  `world.status`'s `dirty` counter unchanged, and re-granting makes the same
  command apply. The engagement-view regression (`WorldEngagement` as a view
  over the same grant table) is `proof.cs screens`'s coverage, not
  duplicated here.
- **`bindings`** `[--no-build] [--width W] [--height H] [--exit-after-seconds N]`
  — the player-document + layered binding-resolution proof
  (see **The player document + bindings**): (a) session A boots the REAL local
  player-document store fresh, asserts the engine-default composed mapping
  (`player.bindings`), live-rebinds a source (`player.bind`), and
  `profile.save` folds+persists it (the document `Revision` bumps, read back
  through `profile.doc`); (b) session B relaunches and asserts the rebind
  survived and the revision did not bump again on a plain boot; (c) session C
  boots `--world kart-remap.world.json` and asserts its `bindingOverlays` entry
  merges over the engine default, then `world.bindings.remove` live-recomposes
  it back. There is no CLI override for the
  player-document store path, so this proof backs up the REAL
  `world/` subtree whole (byte-for-byte) before every session
  and restores it in a `finally` — the real catalog is never destroyed.
- **`storage`** `[--no-build] [--width W] [--height H] [--exit-after-seconds N]`
  — the cloud-readiness proof (see **Storage**), proven against
  the local backend only: (a) a fresh boot against the cleared REAL store
  asserts `storage.status`'s honest baseline (local authoritative/cloud
  unwired, identity declined, endpoint none, a present catalog revision +
  version token), then the cheapest revision-bumping verb (`profile.set`) is
  asserted to bump the revision, change the version token, and flip `dirty
  on`, with the on-disk split layout present; (b) a relaunch asserts the same
  persisted revision survives; (c) a `--user-id <valid oid Guid>` boot
  asserts the explicit-override identity echo; (d) a `--user-id not-a-guid`
  boot asserts the declining echo. Like `bindings`, it backs up and restores
  the REAL `world/` + `profiles/` subtrees whole around every
  session — the real catalog is never destroyed.
- **`expo-author`** `[--no-build] [--width W] [--height H] [--exit-after-seconds
  N] [--out PATH]` — regenerates the expo world reproducibly.
  Boots a baked-default Puck.World, feeds the checked-in `scripts/expo-world.txt`
  authoring session (a new kit row + retunings + a `table` policy, a warmer
  four-pillar scene, staggered spawns, three asset-free screens, and live render/
  census levers) over stdin, then `world.save`s to `Assets/worlds/expo.world.json`
  (`--out` overrides) — the trailing save FOLDS the live render levers + census
  into the document. The script and the artifact are the checked-in reproducible
  pair; the JSON is NEVER hand-edited.
- **`expodoc`** `[--no-build] [--width W] [--height H] [--exit-after-seconds N]`
  — the second-world + session-write-back proof: (a) `--world
  expo.world.json` boots the loud `[world] definition:` line; (b) a
  distinguishing `world.status` fact — expo's `kits 6 screens 3` differs from the
  default's `kits 5 screens 5`, a visibly different game with zero code, and a
  fresh boot reports `session-drift none`; (c) the write-back SLICE not covered
  by `mutate` — a live SESSION lever (`world.population`) is changed, saved to a
  temp copy, and a relaunch `--world <that copy>` boots a census whose
  `networkPlayers` survived the fold (the saved JSON carries it, `world.population`
  echoes it), while the checked-in artifact carries its own folded census; (d) the
  third fold dimension positively — a runtime `screen.insert` of a real ROM makes
  `world.status` name the `screens` drift and `world.save` fold the live machine
  into that screen row's `Machine` source. Expo's ouroboros is covered by
  `worlddoc`.
- **`record`** `[--no-build] [--width W] [--height H] [--seconds S] [--out PATH]`
  — the native-capture proof (see **Native capture**). Boots a real GPU-windowed
  Puck.World (the tap reads each captured frame back through the SDF engine, so a
  live present surface is required), asserts `capture.status` reads idle, arms a
  `RecordingSession` with `capture.start` (echoing the negotiated codec and any
  device declines — a mic privacy denial is a PASS path), records ~`S` seconds of
  the autonomous crowd, finalizes with `capture.stop`, and walks the produced
  container: the EBML doc type matches the negotiated codec (`webm` for AV1, `matroska`
  for the H.264 fallback), an Opus audio track is present (track presence, not
  loudness), a video track is present when video negotiated, the file is non-trivial,
  and `capture.status` reads idle again. It also asserts the recording document the
  world resolved carries the capture-only overlay row. `--out` copies the artifact
  out for a real player to open.
- **`ui-floor`** — the rendered-overlay floor on both backends: the unified
  overlay node, the console panel, per-seat binding bars, toasts, and HUD.
- **`editor-mode`** — editor entry/exit, binding groups, chord meanings as data,
  and the group round trip (see **Editor mode**).
- **`editor-edit`** — selection, drag-coalesced manipulation, whole-row commit,
  frozen-preview retirement, and per-seat viewport clipping.
- **`editor-cameras`** — the client-side editor camera rig swap and live camera
  reconcile.
- **`placements`** — creation/placement rows as world data: import and stamp in
  pixels, plus the corrupt-input rejection path.
- **`sculpt`** — the sculpt workbench, whose preview is stamp-identical to the
  committed stamp by construction, including the 48-shape stamp cap.
- **`audio`** — the audio world data model end to end: the emitter graph,
  emission facets, cues, and the device's honest-silence posture.

Three standalone harnesses sit beside `proof.cs` in `scripts/`, run the same way
and covering the same surface (they gate nothing — see the note above):

| Harness | Proves |
|---|---|
| `audio-mix.cs` | The pure mixer core offline, with two self-referential PCM hashes — no device, no GPU |
| `audio-device.cs` | Device liveness, structurally: the failure paths (unsupported, declining, mid-stream fault) degrade to silence, count rebinds, and stop without a throw |
| `overlay-envelope.cs` | `OverlayFrameBuilder` at its declared maxima with no GPU: saturation drops are counted (never silent), the toast tail reservation cannot be starved, and clip-table overflow drops rather than bleeding unclipped |

Everywhere numbers are formatted or parsed runs through the invariant culture, so
a corpus and its asserts are locale-stable.

### Corpus format

A corpus is line-oriented text:

| Line | Meaning |
|---|---|
| `@<seconds> <command>` | a command written to Puck.World stdin at `<seconds>` |
| `#expect p<N> <x> <y> <z> <yaw> <pitch> <roll>` | a closed-form final-pose assertion |
| `#expect-band p<N> <x\|y\|z> <lo> <hi> [class]` | a per-entity axis-band assertion (the mid-air jump band) |
| `#expect-separation <x\|y\|z> <classA> <classB> <minGap>` | `min(classA) − max(classB) > minGap` (the variable-height proof) |
| `#sweep-at <seconds> [name]` | opens a SWEEP: the `player.where` lines at `<seconds>` are read back here, and the `#expect*` lines that follow attach to it |
| `#loop-start` | everything below repeats each cycle under `--loop` |
| `#cycle-end <seconds>` | the cycle length (for loop timing) |

The **`#sweep-at`** directive makes multiple timed sweeps first-class in one
feeder, so the hop's mid-air band and landed tableau share the same runner. A
corpus with no `#sweep-at` (parade/flight/flood) synthesizes a single final
sweep at its last `player.where` time.

### THE EXPO — the primary corpus

`--kind expo` (the default) is a **mixed-genre render proof**: an even split of
the population across five archetypes in LAYERED ZONES of one shared scene, a
loop-capable ~90 s cycle — the headline "124 real people, five genres at once"
load. Each archetype's assertion strategy matches what it can prove:

| Archetype | Motion | Zone | Choreography | Assertion |
|---|---|---|---|---|
| **Karts** | grounded | oval ring (rx 38 / rz 30) | an offline kart model — accel to a cruise speed, corner slowdown + strafe-led drift on the two hairpins (facing decoupled from velocity), staggered around the field | rerun near-identity (final `reconcile` pin) |
| **Platformers** | grounded + jump | central plaza (r ≤ 12) | a run/hop show, a re-anchored mid-air hop SHOWCASE (full/short split, some mid-`run`), a re-anchored landed finale | closed-form landed `#expect` + a mid-air `#expect-band`/`#expect-separation` |
| **Ships** | free | airspace y 18–30 | a banked formation pass + a synchronized barrel roll, then a closed-form ring dive | pose-anchored closed-form `#expect` (final tableau) |
| **Submarines** | free | low band y 4–10 | a slow damped glide with gentle pitch undulation (momentum-heavy swimming), then a straight closed-form finale | pose-anchored closed-form `#expect` (final tableau) |
| **Walkers** | grounded | plaza-rim annulus (r 14–22) | the flood human mixture (idle/walk/run/sprint dwell states, heading drift, two-sided boundary steering), confined to the annulus | rerun near-identity (final `reconcile` pin) |

Setup sends `world.timing on` → `world.population <N> idle` → `wire.ack quiet`;
evidence reads at mid-show and cycle end; the finale sweep reads back all 124
(ships/subs/platformers assert closed-form, karts/walkers are captured
report-only for the `compare` envelope). The mixed-genre headline: one scene,
five genres, one wire — the finale tableau asserts closed-form, and two full
cycles compare at rerun near-identity.

### The other kinds (`--kind …`)

- **parade** — the byte-exact machinery proof: THE MARCH (a 12-wide phalanx
  sweeps the seats), THE RING (all 124 orbit a 22 u circle on curved tapes), THE
  CONVERGENCE (four wedge teams run radius 30 → 10, closed-form `#expect`). Every
  wave re-anchors so stdin jitter never accumulates. `run --kind parade
  --headless` asserts 124/124 exact.
- **flood** — the realism twin: a seeded offline model of 124 fake humans
  (idle/walk/run/sprint mixture, human dwell times, `PlatformerTuning`-derived
  accel ramps, boundary steering for uniform coverage) as pure `player.run`
  streams plus `player.reconcile` **server corrections** every ~6 s — the
  intents-forward/corrections-back netcode shape. No `#expect`; the bar is `run …
  --kind flood` ×2 then `compare --tolerance 0.75 --yaw-tolerance 3` (rerun
  near-identity + the dispersion rms vs the uniform-disc prediction).
- **hop** — the JUMP action-lane proof: grid-anchored entities launched with
  `player.press jump <hold>`, a mix of taps and full holds, some mid-`player.run`
  (lane/tape independence). Two `#sweep-at` sweeps assert all three classes
  through the one feeder: **(a)** a mid-air band, **(b)** a landed tableau
  (`y=0.00`, closed-form `x/z`), **(c)** the variable-height separation (full
  mid-`y` clearly above short).
- **flight** — the 6DOF proof: a population switched to `player.motion free`
  through THE ASCENT, THE ROLL (a barrel roll), and THE DIVE (a ring aimed inward
  and down, a straight `player.fly` to a closed-form descending shell) — altitude,
  pitch, and roll all asserted. Run with `--tolerance 0.12`; the sign conventions
  (nose-up `+pitch`, etc.) are empirically probed and encoded in the generator.

These remain World-owned live proofs (closed-form tableaux and paced console
journeys), not a World-only golden/hash gate. The engine contracts beneath them
— fixed numerics, command snapshots, CLI routing, bindings, and record/replay —
are enforced by Post Tier A.

### Known-marginal assertions

The deterministic tableaux (parade 124/124, flight 124/124, hop landed 124/124,
expo finale 75/75) are the byte-exact contract. Three assertions run marginal
by nature — treat a matching failure profile as environmental, not a
regression:

- **FPS gate** — marginal at the proof default 2560×1440 (worst hovers 59–75).
  A run failing ONLY the FPS assertion with its tableau green: re-run once
  (`--no-build`) before treating it as a regression.
- **hop/expo mid-air bands** — the wall-clock-paced mid-air sample lands out of
  band for a machine-dependent subset; the landed/finale tableaux stay
  exact.
- **flood rerun envelope** — the ±0.75 u compare is session-phase marginal: the
  corpus's jitter-sensitive entities (p14/p17/p36/p70 at seed 128) drift between
  wall-clock attractors on a loaded machine. Cross-check against a
  pristine-baseline pair before ruling a regression.
