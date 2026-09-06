# Modules

A module is a `puck.world.def.v1` fragment the island imports under an alias
(`imports: [{"document": "modules/<name>.world.json", "as": "<alias>"}]`),
rooted under one court placement named `<alias>Court` at local origin and
exporting only its control rows.

## granaries

The granaries turn the platform's user-data storage accounts into a storeyard:
timber buildings, green roofs, sealed side bins, and a beacon over the
public-content anchor. Each building is one discovered account. The court and
the buildings are ordinary authored creations; the Azure adapter knows nothing
about granaries.

### How the court is dealt

`granaryStores` is a placement carrying a `deal` facet: a template whose
children are dealt from the keyed text row `granaryNames`, one child per cell,
over the template's own `distribution` region — a scatter of 16 offsets, the
row's capacity. A child is an ordinary placement row named
`granaryStores/<accountName>`, parented to the template at the offset it was
dealt, carrying the template's prototype, `solid`, `grip`, `region`, and
`emission`. The template itself renders nothing and collides with nothing.

`deal.variants` reads the same row at the same key and maps the cell's text to
a prototype: `bytrcstp001` deals `granaryAnchor` (the beacon), every other
account deals `granaryStore`.

The server's per-tick sweep (`WorldServer.SweepPlacementDeals`) re-deals only
when the row moves. A cell that arrives takes the lowest free offset; a cell
that leaves frees its offset and moves no sibling; a re-authored variant re-deals
that one child in place. Children land as ordinary placement mutations under the
world principal, so they journal, `world.undo`, and replay through the one
placement door.

Read back:

```text
world.placements          # 'granaryStores' … dealt from granaryNames (<n> of 16); 'granaryStores/<name>' … dealt by granaryStores
world.budget              # placements … 16 dealt offset(s) over 1 template(s)
world.state granaryNames
world.state.cell.set granaryNames bytrcstp004 bytrcstp004   # deals a fourth building on the next tick
world.state.cell.remove granaryNames bytrcstp004            # removes it, the others stay put
```

### Visit the live deployment

From the repository root, with Azure CLI signed into the approved tenant:

```text
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/granaries.world.json --extensions-config-file src/Puck.World/Assets/hosting/granaries.extensions.json
```

The checked-in host configuration selects the `byteterrace` resource group,
lists storage accounts beginning with `bytrcstp`, excludes the fabric account
`bytrcstp000`, and projects `name`/`id`/`location`/`kind`/`sku` into the five
`granary*` text rows. Resource metadata reads only: no keys, blob contents, or
Azure resource mutations. Set `world` to the importing document's exact id and
name the rows as the importing document spells them (an aliased import
prefixes them `<alias>_`). Copy the configuration and set an independent
lineage UUID for another deployment; choose `managedIdentity` for an
appropriately configured host identity.

Discovery begins on the first closed tick, collects on one-second scans, and
refreshes once per minute of simulation time; pausing the world pauses
discovery. With no extension configuration the court stays empty until a row
is written. A read failure retains the last complete rows; a confirmed empty
collection empties them, and the sweep removes every dealt building.

### What the metaphor means

The beacon marks `bytrcstp001` because the hosting convention fixes public
content to partition zero, whose account offset is one. Other buildings are
potential private user-data homes. The account inventory is not the active
Orleans partition count: discovering five accounts does not prove five
partitions are serving. No bin fill level, traffic, health, grain occupancy, or
migration status is invented from inventory. A later reading of the platform
enters the same way — an observation writing a row a placement, body, or rule
reads.

### Import and reinterpret

Move `granaryCourt` (restate its position and yaw from the importing document)
to place the whole storeyard; keep its scale at one. Replace the three
prototypes to reinterpret the same accounts. The court's title uses the
hash-pinned font at `fonts/inter-regular.ttf`, resolved relative to the
importing root world. The module exports its five rows as reads, spawns at
`granaries-arrival` on the court's near edge, and walks the `granaryYard`
surface domain.
## What the metaphor means

The beacon marks `bytrcstp001` because the checked-in hosting convention fixes
public content to partition zero, whose account offset is one. Other buildings
represent potential private user-data homes. The deployment's account inventory
is not the active Orleans partition count: discovering five accounts does not
prove five partitions are serving a running host. No bin fill level, request
traffic, health, grain occupancy, or migration status is invented from inventory.

An author could later use separately measured occupancy to fill bins, a confirmed
migration to move a wagon, or a telemetry alarm to attract pests. Those events need
their own explicit sources and gameplay rules. The current scene makes only the
inventory claim it can substantiate.

## Import and reinterpret

Import `modules/granaries.world.json` from another shipped world. The main Puck
world already imports it. Move `granaryCourt` to place the whole storeyard, or
replace the three prototypes to reinterpret the same accounts. Generated buildings
are children of the court, using its position and yaw. Keep its scale at one.
The court's title uses the existing hash-pinned font at `fonts/inter-regular.ttf`,
resolved relative to the importing root world; provide that asset when relocating
the module.

Copy the host configuration, set `world` to the importing document's exact ID,
and retain or narrow its source scope and disclosure. World imports never enable
Azure access themselves. A text world can use the tables alone by omitting
`observations[].placements`. Different sources can project into different tables
and reserved placement prefixes through the same mechanism.

The example bounds inventory at 16 items and reserves 16 future placements at
boot. The court is sized for the present two rows; enlarge its authored grounds
for a larger deployment. Buildings use static per-shape rendering, with no body
simulation, per-tick cloud calls, or unchanged-snapshot rebuilds. Complete changes
are admitted as one batch. Sorting account keys fixes identity and ordering, while
membership changes can reposition buildings in the grid. Reserve `granary-` for
this source rather than placing unrelated authored rows under that prefix.

See [service composition](../../../../Puck.World.Server/ExtensionConfiguration.md)
for projection authority and lifecycle, and the
[Azure adapter](../../../../Puck.World.Azure/README.md) for query settings and bounds.

# The kart district (`kart.world.json`, alias `kart`)

A closed racing loop: a `curves` row (`kartTrack`, eight knots, constant curvature — an
exact circle, never control points) traces the lap direction, and `kartCourt` carries a
single flat floor creation as the one drivable surface, banked wall dressing and a ramp
parented to it at fixed local offsets, and three gate placements (`gate0`/`gate1`/`gate2`,
each an ordinary creation carrying a `region` facet) spaced a quarter-turn apart around
the loop.

**What a kart is, today.** The charter calls for "a rigid kit with a `drive` row"; neither
exists as a document shape. `WorldRigid` is passive physics furniture — it reads no
intent, so a rigid body cannot be player-driven — and there is no kit facet literally
named `drive`. The vocabulary the schema and server actually carry for this shape is the
one `Puck.World.Schema/README.md` already documents: a kit's ordinary `motion` row,
its frame resolved by the `ResolveDriveFrame` body-motion op instead of the walking
`ResolveYawAttitudeAndPlanarFrame` (steering integrates into the body's own heading at a
speed-scaled authority curve, `turn.referenceSpeed`/`turn.falloff`, rather than snapping
to the commanded direction every tick), and a `shaping` row carrying an `across` facet —
the anisotropic drive decomposition: longitudinal throttle/brake/coast via `along`,
lateral grip converging out slip via `across.lateral`. `kart.world.json` authors exactly
this: kit `kart`, program `kartDrive` (`ResolveDriveFrame`, `ResolveHold`,
`ComputePlanarTargetVelocity`, `ShapeVelocity`, `RunActionTriggers`, `ApplyHold`,
`IntegratePlanarAndVerticalVelocity`, `CommitPose` — the walking program's `SnapYawToPlanarIntent`
dropped, since the drive frame owns heading itself), a `ground`/`air` Gravity hold pair
copied from the walker's own numbers, and one shaping row (`engage`/`release`/`reversalRate`/
`backwardSpeed` under `along`, `lateral` under `across`). This module is the first document
in the repository to author `ResolveDriveFrame` and an `across` shaping row; nothing here
is a second implementation of an existing mechanism.

**What a kart still lacks.** No drift channel (a held-gated shaping row ahead of the
ordinary one, the documented spelling for a swappable lateral-grip row) and no boost —
the retired prototype's `dash`/drift tuning is not carried over, since the task asks for
a track, a kit, gates, and a lap counter, not the full feel pass. No banking FUNCTION:
the two `bankWall` placements are decorative dressing baked into the creation's own
shape rotation (a placement's `yawDegrees` turns only about world Y, so a genuine banked
turn — the floor itself tilting — is out of reach of today's placement transform; it
would need either a per-shape roll baked into a curved floor creation authored specially
for each turn, or a placement-level roll/pitch facet beside `yawDegrees` that does not
exist). No chassis look — the kit collides as a bare capsule and renders through
whatever fallback presentation an unlooked body gets; a `looks` row and a matching
`puck.creation.v1` chassis are future authoring, not a missing primitive. The
`kartSurface` navigation domain is declared (module contract: every walkable district
carries one) but unread by anything today — the kit is seat-driven, not
`BodyTargetSource.Navigated`; an NPC kart wandering the loop is a future producer wired
onto the same domain.

**The lap mechanism.** `gateStage` (int, non-negative, starts at 0) advances one gate at
a time: `kart-gate-0`, `kart-gate-1`, `kart-gate-2` each gate an `Edge`-mode rule on
`gateStage == <ordinal>` AND the gate's own `$region:gate<n>` occupancy, so a body must
cross the gates IN ORDER — sitting inside gate 1 first advances nothing, because the
stage guard is still 0. The third gate additionally increments `lap` and resets
`gateStage` to 0, so a completed circuit never needs to return exactly to the start
line. `lapTicks` mirrors `$tick` every tick (an unconditional Level rule) — a lap timer
in engine ticks, read alongside `lap`. `exports` surfaces `lap`/`lapTicks` as reads and
`lap` as a binding; nothing is exported as an action (nothing outside the module writes
into a kart row).

**Proving it.** `tests/Puck.World.Canaries/kart-lap/host.world.json` imports the module
under alias `kart` with one local seat spawned at `kart-arrival`. The positive leg's
`body.fly 1 0 0 -1 0 0 5` (full throttle, a constant steer matching the curve's own
authored direction of travel) drives the seat around the loop; by tick 151 (already
past a full circuit) `kart_lap` reads 1 and `kart_gateStage` has been reset to 0. The
discriminating leg negates only the steer sign (`body.fly 1 0 0 1 0 0 5`): the kart
drives the opposite way around the SAME loop, in the SAME time, and never opens even the
first gate — `kart_gateStage` stays 0 and `kart_lap` never reaches 1 — proving the gates
discriminate on ORDER, not mere proximity.
# The jump district

`modules/jump.world.json` is a course of platforms rising from a starting deck: `jumpCourt` (the one root
placement every other row parents under, so the island moves the whole district by restating that row's
position and yaw alone), four stepped platforms and a wall panel climbing away from it, a trophy floating
in open air off to one side, and a wide catch net well below the whole course. The `vaulter` kit is the
district's own move-set on today's holds vocabulary (`Puck.World.Schema.WorldHold`): a `ledge` hold (a
steep overhang band, cone `[112, 150]`) and a `wall` hold (cone `[65, 110]`, narrower than the walker's
`[60, 120]` at the island level) both release on the `jump` channel exactly as the walker's own wall hold
does; `ground` and `air` holds carry the walker's fall/rise arc, and BOTH additionally carry `thrust: 0.6`
on the `MoveUp` role — a hover-assist a plain walker does not have, present so a seat (or `body.fly`, the
canary's own verification path) can climb the course under sustained vertical input instead of chaining
jumps, and left in for play as the district's own feel. `jump` (a double-jump, `jumpCounter` capped at 2,
resetting on `Grounded`) is bound the same way the shipped walker's own jump is.

## What a district module CANNOT do today — properties do not survive an aliased import

The brief's own wording called for "a trophy placement with a region and an interaction whose Edge effect
writes `reached`" — the generalized `properties` + `interactions` (`WorldInteractionCoOccurrence.Region`)
primitive `puck.world.json`'s own `wren`/`hound` rows already use. That primitive DOES NOT survive
composition under a NON-EMPTY import alias, and this module is always imported aliased (`{"document": "…",
"as": "jump"}` — the island does this for every district). The mechanism (`WorldNameRegistry.cs`,
`WorldModuleNamespace.cs`) prefixes a module's OWN declared `state` row names with `<alias>_` on import and
rewrites every registered reference to match — including `WorldInteraction.Left`/`Right` — but
`WorldPropertyRegistrySection.Names` is one of the many members `WorldNameRegistry.Exclusions` deliberately
leaves unprefixed ("property names are the properties section's own namespace"). A property's own backing
row (a keyed `int` `state` row of the identical name, per `WorldPropertyRegistrySection`'s own contract) DOES
get the alias prefix like any other declared state row, so after import the declared row is named
`jump_vaulter` while `properties.names` still reads `["vaulter"]` and the interaction's `left` reference has
followed the row to `jump_vaulter` too — three spellings that no longer agree, and
`WorldDefinitionValidator`'s `ValidateProperties`/interaction PropertyUnknown checks correctly refuse the
mismatch. This was reproduced directly (`puck registry`/`WorldNameRegistry.cs` need no edit to see it —
author the property + interaction exactly as `puck.world.json` does, import the module under any alias, and
the composed document is refused at boot) and is a genuine gap in the module system, not a mistake in this
district's authoring — every future district that wants a property-tagged region interaction on an
ALIASED import hits the identical refusal. It is out of this module's file list to fix (`WorldNameRegistry.cs`
is shared, edited by other tasks for their own new name sites) and is recorded here so the next district
that reaches for `properties` under an alias does not re-derive it from a boot refusal.

`reached` and `falls` are therefore plain (non-keyed) `int` rows, each written by an ordinary `rules` entry
reading the reserved `$region:<placementId>` occupancy count directly (`Puck.World.Schema.WorldRuleFacts.RegionPrefix`
— no `properties` section, no `interactions` section, entirely self-contained under any alias): `jumpTrophyReached`
sets `reached` to `1` the tick the trophy's region count crosses above zero (`mode: "Edge"`), and
`jumpFallsCounter` adds one to `falls` the same way whenever a body enters the wide net below the course.
Neither is keyed by which body triggered it — the region-occupancy read is an aggregate count, not a
per-body attribution — so "reached" answers "has ANY body reached the trophy", not "which seat's". A design
that needs the latter needs the `properties` gap above closed first.

## Kit and placement names are NOT alias-prefixed either

Unlike `state`/`rules`/`tables`/`patterns`/`topologies`/`generators`/`fields`/`dynamics` row names, a kit's
own name (`WorldKit.Name`), a placement's id (`WorldPlacement.Id`), a spawn point's id (`WorldSpawnPoint.Id`),
and a navigation domain's name are ALSO in `WorldNameRegistry.Exclusions` — composing this module under an
alias leaves `vaulter`, `jumpCourt`, `jump-arrival`, and `jumpSurface` exactly as authored, never
`jump_vaulter`/`jump_jumpCourt`/etc. This is why the module contract's own naming convention
(`<alias>Court`, `<alias>-arrival`) is a hand-authored discipline rather than something the engine derives:
these names are a global flat namespace across every import, and a host references them bare regardless of
which alias it imported the module under. A host's own `bodies.seatSpawns`/`defaultSeatKit` therefore name
`jump-arrival`/`vaulter` verbatim; only a `world.state`/rule/exports reference to `reached`/`falls` needs the
alias prefix (`jump_reached`/`jump_falls`).

## Verifying headless

`tests/Puck.World.Canaries/jump-trophy/host.world.json` is a minimal `standard.basis.json`-based world that
imports `modules/jump.world.json` under alias `jump` and spawns its one local seat on `jump-arrival` with
the `vaulter` kit — the same shape a future island import uses. Drive it directly:

```text
dotnet run --project src/Puck.World -c Release -- --headless --state-dir <tmp> \
  --world tests/Puck.World.Canaries/jump-trophy/host.world.json < tests/Puck.World.Canaries/jump-trophy/positive.script.txt
```

`body.fly 0.65 0.30 0.22 0 0 0 4.5` (forward/strafe/up channels, no yaw/pitch/roll, 4.5 simulated seconds)
carries the seat clear of every platform's solid geometry into the trophy's region; `world.state jump_reached`
then reads `value=1`. A LEFT-Z convention note for whoever authors the next course: a Bipolar `forward`
channel at `yaw=0` moves the body toward WORLD -Z (observed directly with `body.where`, not assumed), so this
course runs from the arrival deck at `z=+3` out to the trophy at `z=-10` — a course authored the opposite way
around would need its `forward` sign flipped in any driving script, never the channel itself.
# The studio district

`studio.world.json` is a flat stage for character work: a lit floor, a turntable a
body stands on, a mirror wall that shows the stage's own camera, and a counter a
seat cycles by pressing the jump channel while standing on the turntable. It carries
no bodies of its own — a visiting seat brings its own avatar and kit.

## Shape

- One creation, `studioStage`, is the floor and the turntable's raised disc marking —
  rendered and collided from the same shapes, so nothing sits on ground that never
  matched what a body stood on.
- `studioTurntable` is a child of `studioCourt` carrying a `region` facet (radius 1.6)
  over an empty (invisible) creation — a sensing volume flush with the floor, not a
  second piece of standing geometry.
- `studioMirror` is a child wall carrying a `view` screen face (`mirror`) that shows
  the `studioStage` camera, itself anchored on the turntable and looking back at it —
  the reflection a body on the turntable sees of itself.
- `studioCourt` is the district's one root placement; moving it (position and yaw)
  moves the whole stage, its turntable, and its mirror together.

## The look counter

`look` is a slot `int` row, range `0..7`, that a rule (`studio-look-cycle`) advances
by one (wrapping) on the tick a body's seat presses the world's `jump` channel while
the region reads at least one occupant:

```text
gate: $region:studioTurntable >= 1  AND  $channel:1:jump >= 1
mode: Edge
effect: setState look = (look + 1) % 8
```

A world-rule channel read is a fixed 1-based seat number, not "whichever body is in
the region" — the closest primitive the engine offers today reads local seat 1's own
`jump` channel, so a second local seat standing on the turntable does not itself
advance the counter. `look` is the reveal ladder's future selector into whatever the
importing world's own `looks.rows` declares (the wren's look among them) — this
district only counts; nothing here swaps a body's rendered look yet, since no
document primitive binds a placement or body look to a live state row (that swap is
reveal-wave work the counter is built to feed).

Exported: `reads: [look]`, `bindings: [look]` — a host may gate a rule on the count
or bind it to a HUD/overlay element; `actions` is empty, since nothing outside the
module drives the counter.

## Import

Import `modules/studio.world.json` under an alias (`{"document": "modules/studio.world.json",
"as": "studio"}`). `studioCourt`/`studioTurntable`/`studioMirror` and the `studioStage`
camera are placement/camera names, which the engine never rewrites on import — keep
them distinctive so a second module's own bare names cannot collide with them. The
module's own declared rows (`look`, the `studio-look-cycle` rule, the `studioFloor`
navigation domain) still get the alias prefix, so a host reads the counter as
`<alias>_look` (`world.state studio_look` under alias `studio`) and poses a seat at
`spawn:<alias>-arrival` (spawn point ids are not rewritten either — the bare name
already carries the alias word, by convention, exactly as the granary placements
above do).

The importing host must declare its own `channels` row named `jump` (the shipped
island already does) and a `placements.policy.derivedFaceScreens` reservation of at
least 1 for the mirror's face to bind; a host authoring neither still boots the rest
of the district, but the counter never advances and the mirror shows the no-signal
card.
