# The island's districts

Every district of the one world is a module in this directory: a `puck.world.def.v1` fragment with no
`basis`, `host`, `bodies` census, `views`, or `channels`, imported by `puck.world.json` under an alias
(`imports: [{"document": "modules/<name>.world.json", "as": "<alias>"}]`). A module declares its own
`prototypes`, `placements` rooted under one court placement named `<alias>Court` at local origin, `state.world`
rows, `rules`, `interactions`, at least one spawn point named `<alias>-arrival`, `navigation.domains` for its
walkable ground, and an `exports` record naming the only rows a host may read, write, or bind. Names are
spelled bare inside the module and compose as `<alias>_<name>` (`world.state dive_depth`); placement ids,
spawn point ids, kit names, and camera names compose as authored, which is how the island restates a
district's court (`{"id": "diveCourt", "position": [...], "yawDegrees": ...}`) and its arrival spawn without
naming anything private. A navigation domain can name the court as `parent`, making its origin and grid axes
local to that court; the granaries use this form. Rows that still carry absolute positions — a `state.lattices`
origin, an unparented navigation origin, a curve's knots — require translation when the district is placed.
A module authors no world-level tunable (a simulation rate, a seat
count); it reads the island's.

| Alias | File | District |
|---|---|---|
| `granaries` | `granaries.world.json` | The platform twin's storage court, dealt from the deployment's inventory rows |
| `arcade` | `arcade.world.json` | Two cabinets and a handheld, each booting an authored cartridge |
| `dive` | `dive.world.json` | The pool: a medium lattice, a diver kit, fish, a depth row |
| `kart` | `kart.world.json` | A track on a curve, a kart kit, gates, a lap counter |
| `jump` | `jump.world.json` | A platform course rising from the shard steps, a vaulter kit, a trophy |
| `arena` | `arena.world.json` | The hp/targeting/attack and elemental suites in a walled yard |
| `studio` | `studio.world.json` | A flat stage for character work, look cycling, a mirror wall, a gate that opens once awakened |

The island places the courts on its crown: `dive` north at (0, 0, -46), `kart` east at (48, 0, 0), `jump`
south at (0, 0, 52), `studio` west at (-42, 0, 0), `arena` north-east at (46, 0, -46), `arcade` at (26, 0, 12),
and `granaries` at (-10, -0.5, 40); the market hall, the proving ground, and the garden are the island's own
districts under `marketCourt`, `provingCourt`, and `gardenCourt`. `world.imports` prints every layer with its
alias and exports; `body.pose spawn:<alias>-arrival` stands a seat in a district.

## What the island's derived limits decided

A document composes under ceilings the engine derives from its guarantees, and the first composition of every
district met three of them. The arena's `autoAttack` channel is gone: sixteen drive-reach ordinals is the
wire's ceiling and the island's eleven channels plus two target registers left room for three; flip the
arena's `autoAttackToggle` row with `player.state.cell.toggle` instead. The arena's body-pair `heat-ignites`
and `cold-spreads` interactions are gone: a pair interaction costs capacity squared against the two-million
work-unit rule budget, so elemental spread between bodies waits for a region-scoped pair primitive; the
pit and pool still ignite and chill a body that enters them. The dive district's pool is the island's one
water: a document admits one physical field topology, so the garden's own pond lattice, its `water` row, and
its fish left with it. The quilt shards under `../shards/` and the file neighbour resolver are landed but the
island authors no `adjacencies` yet: the seams were sited at the old island's edge and its shards refuse the
gravity areas and cameras they inherit as basis deltas; re-siting them to the crown's extent is the next step.

## The partition granaries

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
when its source row, variant row, or template changes. A cell that arrives takes the lowest free `dealSlot`;
a cell that leaves frees its slot and moves no sibling; a re-authored variant re-deals
that one child in place. Children land as ordinary placement mutations under the
world principal, so they journal, `world.undo`, and replay through the one
placement door.

### Grow and rearrange the court

The account inventory owns membership and each child's `dealSlot`. The template's `deal.preserve` lets an
instance own its transform, prototype, and other facets independently. Removing an inventory cell removes its
child; changing another account does not move an existing store. A child is still an ordinary placement.

A placement's `spatial` array describes named local volumes. `Occupation` reserves the building's space;
`Clearance` reserves access around it. Occupation conflicts with occupation or clearance, while two clearance
volumes may share space. `Influence` is nonblocking coverage on an author-defined channel. All three use finite
boxes or spheres, including height, local offsets, placement scale, and yaw. A bridge can pass above a store,
and diagonal boxes use their actual orientation. These authored volumes are planning contracts; the rendered
creation and solid-field collision remain the physical geometry the author must keep aligned with them.
Static queries require positive effective radii/extents on the Q48.16 grid and currently admit compiled
coordinates and extents through ±4,194,304 world units. Geometry outside that numeric envelope is reported as
unsupported; it never silently becomes a zero-sized blocker.

The granary district supplies blue irrigation and gold power coverage. Ordinary rules read
`$influence:water:granaryStores` or `$influence:power:granaryStores` with `key: "$each"` to address a dealt child.
The result counts distinct other providers covering the center of the target's first occupation volume.
Several matching volumes from one provider count once. A missing or unsupported target reads absent; a present
target outside coverage reads zero. An unsupported provider makes its channel absent rather than falsely
reporting complete coverage. Channel labels remain shared when a module is imported under an alias.

Water and power each have three authored coverage sizes. A request selects a text vector from a keyed choice
row; the provider's spatial extents and visible coverage marker bind to that same value. The rule commits the
selected vector and displayed stage together. This uses the existing document-value binding system instead of
replacing a whole placement for each size. Under the shipped `granaries` import:

```text
world.state.cell.set granaries_granaryIrrigationRequest $value 1
world.state.cell.set granaries_granaryPowerRequest $value 2
world.state granaries_granaryIrrigationStage
world.state granaries_granaryWaterCoverage
```

Stages run from 0 through 2, including shrinking back to an earlier stage. The module exports these requests
and the coverage rows for hosts to bind. The survey table is an authored landmark; it has no click or engagement
handler. The current HUD reports stages and survey page, while the console provides the working control path.
Coverage rows store provider counts and write only when a value changes. They catch up one tick after a coverage
expansion; after an account is removed, its building disappears during the deal sweep and its coverage cells
are cleaned on the following tick.

Planning never changes the world. Start a preview, inspect its transforms and price, then commit or cancel:

```text
world.reflow.preview granaryStores
world.reflow.status
world.reflow.commit
world.reflow.cancel
```

The preview verb also accepts a JSON `WorldPlacementReflowRequest`: `templateId` selects the authored policy
and distribution, `placementIds` optionally selects a bounded group, and `edits` supplies seed position, yaw,
scale, or spatial-volume changes. A seed edit and its neighbors' rearrangement enter the same batch. For example,
after reading a store's actual id from `world.placements`:

```text
world.reflow.preview {"templateId":"granaryStores","edits":[{"placementId":"granaryStores/bytrcstp001","scale":1.25}],"preserveInfluenceCoverage":true}
```

Use the imported placement names when the module has an alias. `preserveInfluenceCoverage` asks the planner to
retain at least the existing number of providers on each channel at affected occupation targets; gains are allowed.
Explicit groups share the selected template's parent frame and offsets. A preview is a proposal, not a reservation. Nearby new,
moved, removed, or enlarged spatial volumes invalidate its spatial read. Its named template, membership, and
state inputs are guarded separately; spatial guards also capture state-bound geometry. Distant changes and unrelated state counters remain independent. Current
authority, whole-document validity, render capacity, and exact payment are checked again at commit. Failure
changes neither layout nor payment. Undo restores the entire edit together.

`deal.reflow` authors the candidate work budget and optional Int `costPerMove`, `costRow`, and `costKey`.
A paid operation guards current solvency and the exact debit, so an advancing balance or an intervening deposit
is allowed when enough funds remain. The court's policy is free. Search is bounded to 64 static members and
prefers current positions before nearby distribution offsets; it does not promise globally optimal packing.
Pinned placements stay fixed. Selected members must have no child placements: subtree motion needs its own
plan. Moving or otherwise unrepresented frames refuse static planning without
restricting their use elsewhere in the world.

Console controls are bindable. Programmatic clients use `WorldQuery.ReflowPreview`, `ReflowStatus`, and
`ReflowCancel` through the attributed link. Status carries the same typed `WorldPlacementProposal` that the
console reviews; its ordinary batch is the commit artifact. The console workflow currently addresses its local
server; a federated console link without explicit-principal queries refuses that workflow. Cached proposals belong to the submitting principal,
expire after five minutes, and are discarded without payment when cancelled.

Moving or turning `granaryCourt` carries its children and the `granaryYard` navigation frame. Navigation retains
workspaces and routes only after proving the same fixed cell and edge bake, then rebinds to the current solid
query. Uncertain domains rebuild. This proof still samples geometry; it is not tile-level SDF invalidation.
Coverage does not allocate finite supply, route pipes or wires, or resize a physical field lattice. Those remain
separate authoring and simulation problems.
Read back:

```text
world.placements          # 'granaryStores' … dealt from granaryNames (<n> of 16); 'granaryStores/<name>' … dealt by granaryStores
world.budget              # placements … 16 dealt offset(s) over 1 template(s)
world.state granaries_granaryNames
world.state.cell.set granaries_granaryNames bytrcstp004 bytrcstp004   # deals a building on the next tick
world.state.cell.remove granaries_granaryNames bytrcstp004            # removes it, the others stay put
```

### Visit the live deployment

From the repository root, with Azure CLI signed into the approved tenant:

```text
dotnet run --project src/Puck.World -c Release -- --extensions-config-file src/Puck.World/Assets/hosting/granaries.extensions.json
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
surface domain. It also exports coverage and expansion state, so a host can bind
its own controls. World imports never enable Azure access themselves: the host
configuration supplies that authority. A text world can consume the inventory
rows without the court's creations and dealt placements.

Inventory is bounded at 16 accounts. Buildings use static rendering, with no body
simulation, per-tick cloud calls, or unchanged-inventory rebuilds. Placement ids
under `granaryStores/` belong to the deal; author unrelated furniture elsewhere.

See [service composition](../../../../Puck.World.Server/ExtensionConfiguration.md)
for projection authority and lifecycle, and the
[Azure adapter](../../../../Puck.World.Azure/README.md) for query settings and bounds.

## The kart district (`kart.world.json`, alias `kart`)

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
line. `lapStart` records `$tick` when the first gate fires and `lapTicks` is written once per lap as
`$tick - lapStart` — a lap timer in engine ticks, read alongside `lap`, that costs the quiet tick nothing
(a rule that writes every tick installs the document every tick). `exports` surfaces `lap`/`lapTicks` as reads and
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
## The jump district

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
## The studio district

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
- `studioGate` is a child wall opposite the mirror (local `(0, 1.1, 3.6)`, past the
  arrival spawn) carrying a `respond` facet: its base creation is the solid
  `studioGateClosed` box, and each entry swaps it to the collision-free
  `studioGateOpen` creation once one local seat's own `identity.0-awakened`/
  `identity.1-awakened` cell reads nonzero — the front door onto whatever the
  importing world places beyond it.
- `studioCourt` is the district's one root placement; moving it (position and yaw)
  moves the whole stage, its turntable, its mirror, and its gate together.

## The look counter

`look` is a slot `int` row, range `0..7`, that a rule (`studio-look-cycle`) advances
by one (wrapping) on the tick local seat 1 presses the world's `jump` channel while
the region reads at least one occupant; the same edge also sets the fact `awakened`
on that seat's own driving identity (body key `0`), through the reserved `identity`
lane an importing world declares (see below):

```text
gate: $region:studioTurntable >= 1  AND  $channel:1:jump >= 1
mode: Edge
effects: setState look = (look + 1) % 8
         setIdentityFact key:0 fact:awakened value:1
```

`awakened` is the reveal ladder's own name for "has cycled its look at least once" —
an importing world reads it off `$identity:body:<n>:<fact>` to gate whatever the
studio's own gate faces, and to re-pose a returning identity elsewhere at boot
without waiting for it to walk back through the studio at all.

A world-rule channel read is a fixed 1-based seat number, not "whichever body is in
the region" — so each local seat needs its own rule, and a seat's channel compiles
only in a host that declares that seat. The module carries seat 1's rule; the island,
which seats two, adds seat 2's (`seat2-studio-look-cycle` in `puck.world.json`,
writing `studio_look` and key `1`). The region predicate reads any occupant, so a
seat pressing `jump` while the other stands on the turntable advances the counter
and wakes the pressing seat's identity. `look` is the reveal ladder's future selector into whatever the
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
card. A host that also wants the gate to open and the fact to travel declares a
keyed `int` row named `identity` in its own `state.world` (`WorldIdentityFactLane`) —
without it `setIdentityFact`/`$identity:` both refuse by name at compile, and
`studioGate` simply never leaves its authored `studioGateClosed` prototype.

## The arcade

`modules/arcade.world.json` is the arcade district: two cabinets and a handheld
on a stand, each booting an authored `puck.cartridge.v1` document from
`src/Puck.World/Assets/cartridges/` rather than a ROM file. The game is the
same on both bricks: a pip walks a room whose four walls are the district
colors — dive `#2E6FD9` to the north, kart `#D9552E` east, jump `#3FB950`
south, studio `#C9A227` west. The CGB target's palette holds four entries and
the floor takes one, so its west wall is a checker of the kart and jump
entries; the AGB cartridge paints all four. The d-pad walks the pip, the walls
stop it, and `a` counts a step into the `steps` variable (`0xC202` on the CGB
brick), so a piped run can read the cabinet's game state through `screen.peek`.

Import it under the alias `arcade`:

```json
{ "document": "modules/arcade.world.json", "as": "arcade" }
```

## What it declares

| Row | Bare name | Notes |
|---|---|---|
| `placements` | `arcadeCourt` | The court: a floor and a back wall with a lit strip, at local origin; every other row is its child. |
| `placements` | `cabinetCgb`, `cabinetAgb` | The two cabinets, solid, facing +Z toward the court. |
| `placements` | `handheldStand`, `handheld` | The stand and the handheld cradled on it. |
| `screens` | indices 8, 9, 10 | The CGB cabinet's face, the AGB cabinet's face, the handheld's face. |
| `kits` | `arcadePad` | A pad map over the island's channels: forward and strafe to the left stick, turn to the right stick, jump to South, rise to East. Never worn by a body. |
| `bodyMotionPrograms` | `arcadePad` | The kit's required program. |
| `state.world` | `cgbScreen`, `agbScreen`, `handheldScreen` | Int slots holding the three screen indices; exported as reads and bindings. |
| `state.world` | `handheldHeld` | 0/1: whether seat 1 currently holds the handheld. |
| `spawnPoints` | `arcade-arrival` | Court-local `(0, 0, 2)`, facing the cabinets. |
| `navigation.domains` | `arcadeFloor` | A `Surface` domain over the court floor. |
| `rules` | `handheld-pickup`, `handheld-release` | Toggle the handheld's `attach` facet onto/off seat 1's body — see "The handheld" below. |

Placement ids, kit names, spawn points, screens, and domains keep their bare
spellings under the alias; only the state rows compose as `arcade_<name>`.

## How a cabinet is engaged

Each `screens` row is engageable with `engageChannel: "jump"` — the island's
own channel — and `kit: "arcadePad"`. A seat standing within `engageRadius`
(2.2 m in the slab's XZ plane) whose jump channel rises is composed onto the
cabinet captured, so its pad drives the game instead of the avatar; the seat
leaves with `body.engage off`. `body.engage screen:8` composes the same
application from the console.

## Where the screens sit

A `screens` row is a world-space slab, not a placement: the row's `origin`
is the cabinet's screen face, authored to sit in the cabinet prototype's bezel
with the court at the origin. The island restates the three rows' `origin`s
beside `arcadeCourt`'s `position` and `yawDegrees` whenever it moves the court.
The cabinet prototypes declare no creation face, since a face's own source would
boot a second machine at a derived index with no engage route. The arcade takes
indices 8 through 10; the island's own screens stay below 8, and every index
stays below the derived-face band.

Content paths inside the module are spelled relative to the importing
document's directory (`../cartridges/hgb-mirror.cgb.cartridge.json` from
`Assets/worlds`), as every asset row's path is; a host elsewhere mirrors that
layout.

## The handheld

`handheld` is a chassis with a d-pad, two buttons, start and select, and the
screen at index 10 as its face, cradled on `handheldStand` until a body holds
it. The primitive a held handheld rides is a placement's `attach` facet
(`{"bodyIndex", "localOffset", "localYawDegrees"}`): the row's pose follows the
named body's root pose, colliders and regions included — no new schema member
needed, since a body carries no joint transforms `attach` could target in the
first place (an avatar's rig authors named IK chains and tip joints, e.g.
`handRight`, for the climbing grip solve alone, resolved client-side in
presentation float; there is no server-side, deterministic query for a joint's
current world transform, so `attach`'s local-frame root offset is already the
most precise anchor the engine can honor today).

An ordinary pair of `rules` toggles `attach` live — the exact idiom
`studio-look-cycle` already ships (`compareState` over a region and a channel,
`Edge` mode): `handheldStand` carries a `region` (radius 1.5) a rule senses;
seat 1's body standing in it with the `jump` channel pressed and
`handheldHeld` at 0 gets `handheld` upserted with `attach` pointing at
`body:0`; walking back out of the region with `handheldHeld` at 1 upserts it
back to the stand's authored pose. `Puck.World.UpsertPlacementEffect.Cost`
derives from what the row's own facets would rebuild rather than a flat
number (`Puck.World.WorldPlacementEffectCost`) — the handheld carries neither
`inhabit` nor `solid`, so each firing costs only the document-write floor
(`WorldPlacementEffectCost.DocumentCost`), well inside
`Puck.State.RuleCapacity.MaxWorkUnitsPerTick`'s headroom
(`world.budget.rules` prints the composed total against the ceiling).
`tests/Puck.World.Tests/HandheldAttachLawTests.cs` proves the mechanism
against an isolated document; `RealArcadeModuleLawTests` in the same file
proves the shipped pair against the composed island. What still keeps this a
fixed-index attach: `upsertPlacement`'s embedded placement is a literal,
compile-time constant — its `attach.bodyIndex` cannot resolve to
`$left`/`$right` or any other live carrier the way a
`setState`/`setBodyVerticalVelocity` effect's `key` can, so the body a rule
attaches to must be a fixed seat index, never "whichever body engaged the
screen." A `screens` row still does not ride a placement either — the slab is
world-space, so the picture stays on the stand while the chassis leaves with
the body.

## Verify

Boot a host that imports the module and pipe:

```text
world.imports
screen.state 8
body.pose spawn:arcade-arrival
world.wait 30
body.where 0
body.pose -1.5 0 -1.4 0 0 0 0
world.wait 5
body.press jump
world.wait 5
screen.state 8
screen.peek 8 0xC200
body.press strafe 1 1
world.wait 40
screen.peek 8 0xC200
```

`world.imports` names the module `as arcade` with its exports; each
`screen.state` reads `assigned` with `cartridge <path> hash <source> rom
<image>`; `body.where` reads `grounded` at the arrival; stderr carries
`[world.engage: seat1 auto-engaged screen:8 — context button]` after the
press, and the second `screen.peek` reads a larger `x` than the first.
## Dive

`modules/dive.world.json` is the pool: a `diveCourt` root placement (a small wooden
dock, itself the district's whole moveable frame — every other row below carries
`parent: "diveCourt"` and moves with it), a `diveBasin` placement (one floor-plus-walls
creation, open at the top) holding a dedicated `pool` field lattice whose `water` row
carries the `medium` trait, a `diver` kit (grounded: a `ground` Surface/Gravity hold for
the dock, a `water` Medium hold with `idleDrift`/`equilibriumOffset`/`settleRate` and
`thrust: 1` so the world's own `MoveUp`-role channel — whatever a host names it — drives
vertical swimming, and an `air` Free/Gravity fallback; no jump hold, no `jump`-channel
release), and four fish (`fishKit`, `bodyContact: Overlap`, the same `water`/`air` hold
pair as the diver) inhabiting the pool on a flock producer. A `pool` `Medium` navigation
domain anchors the fish's `movementDomain`. `dive-arrival` stands on the dock.

**Depth has no direct primitive to read from — this module derives it geometrically.**
No reserved rule channel today reads a body's raw world position, or the medium hold's
own per-tick displacement error, so `depth` cannot be computed by comparing a body's
height against the water surface directly. `depth` (a Fixed slot row) is instead driven
by three concentric `region` placements (`diveDepth1`/`2`/`3`, radii 0.8/1.6/2.4, all
centered on the basin floor) whose live `$region:<id>` occupant counts four mutually
exclusive `Level` rules sum into a 0/0.8/1.6/2.4 staircase — closer to the floor trips
more of the nested spheres, which reads as "deeper". `dived` (an Int slot row) latches to
1 the first tick `depth` reaches 1.6 and never resets — a reveal-ladder fact for whatever
reads identity-carried facts. Because `$region:` counts every active body inside the
sphere, not one seat's own, `depth` genuinely answers "how deep is whatever is nearest
the floor" rather than "how deep is the diving seat" specifically; the fish spawn off to
one side of the probes precisely so they do not corrupt the seat's own reading in the
ordinary case. A per-body position or per-body medium-displacement rule fact would let a
future module drop this geometric workaround; it does not exist today.

**Body motion program names are not alias-scoped.** Kit names, body-motion-program
names, and placement ids are excluded from `WorldNameRegistry` (only state rows, rules,
tables, patterns, topologies, generators, fields, and dynamics rows are prefixed under
an import's alias — see `WorldNameKind`). The island's own `puck.world.json` already
declares generic `walk`/`fishMotion`/`school` programs for its own creatures, so this
module deliberately spells its own `diveDiverMotion`/`diveFishMotion`/`diveSchool`
rather than the shorter generic names, to never collide with a host's own programs of
the same shape. A future module should do the same rather than assume programs are
namespaced the way state rows are.

**The pool's field lattice and navigation domain do not ride `diveCourt`'s frame.**
`WorldPlacement.Parent` composes a placement's transform over another placement's, but
`state.lattices` topologies and `navigation.domains` carry their own absolute `origin`
with no parent concept — this module authors them assuming `diveCourt` sits at the
module's own local origin `(0, 0, 0)`. An importer that restates `diveCourt`'s position
to move the whole district (the convention every module here follows) must also
translate the `pool` lattice's and navigation domain's own `origin` by the same offset;
nothing does this automatically today.

Prove the pool in isolation: a minimal host importing `modules/dive.world.json` under
alias `dive`, with a one-seat `diver` kit, boots headless, waits, and poses the seat body
into the basin (`body.pose 0 -2.6 5 0 0 0` for the shipped basin's own coordinates).
`body.where` reports `facts=grounded|inmedium`; `world.state dive_depth` reads positive
(2.399993896484375 at the floor); `world.state dive_dived` reads 1 once depth crosses
1.6. See `tests/Puck.World.Canaries/dive-medium/` for the full two-leg proof — the
successor of the retired `frozen-fish-medium-buoyancy` canary.
