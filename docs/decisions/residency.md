# Residency follows what is observed

As a world grows, what the player can see should be built, loaded and kept
first, and what nobody sees should cost little. This decision settles which
layers may follow the observer, how passive engine help and an author's explicit
choice combine, and where portals fit.

The owner set the position: passive help from the engine everywhere and an
explicit choice by authors (Metroid Prime style areas joined by doors) are
complementary, and asked whether portals are the right abstraction for the
explicit route.

## Only presentation follows the observer

A layer may depend on what a player observes only if two machines with the same
document and the same input could differ in it without differing in state. That
admits presentation and caches whose results do not depend on when they were
filled:

- **May follow the observer:** GPU residency, bake order, store loads and
  landing, the choice of level of detail, the GI window, light selection, and
  nested-view and screen rates. So may what a remote peer is sent, which is
  disclosure, not authority.
- **Never follows the observer:** simulation, rules, physics, sleep and wake,
  machines and world rates, door and portal state, admission, reservation and
  transfer, which sessions open, and the compiled document.

The project graph already holds the line: `Puck.World.Server` does not
reference `Puck.SdfVm`, and contact reads the field program or the distance
grid, never a baked mesh. Sleep stays contact-driven, and pause is never
view-driven ([worlds](../architecture/worlds.md)). With several players, one
authority steps everything; each client keeps residency for its own views, and
split-screen seats and nested views are separate demand points whose demands are
united, so no player's position constrains another's.

**Presentation rates stay tiers.** The 30 and 15 Hz presentation tiers are
kept rather than a continuous rate. On a fixed grid a floor's period is whole
refreshes (`PresentationRate.GridSteps`), and with a 30 Hz tick on a 60 Hz
display only every other refresh is free of a step, so the useful periods are
two and four refreshes, which the tiers already are; a continuous rate would be
quantized back to them and add no placement freedom.

## Portals are edges; worlds and areas are nodes

A Metroid area is a node: geometry made resident as a unit. A door is an edge
whose far side must be resident before it is seen or crossed. All three drafts
reached the same answer: portals and areas are complementary, and neither
generalizes the other.

Puck's portal is already a general edge. It is a face with a frame isometry, a
window through an off-axis frustum fitted to its glass, and an authored
fallback. A pair in one world carries a body through in place
(`destination: "self"`, the debug world's link, fall and mouse-hole pairs;
[a portal is a plane](engine-design.md#a-portal-is-a-plane-a-body-passes-through)),
and a door onto another world adds the crossing transaction.

Across worlds the node exists: the world, whose routed residency
(`routed$<digest>`) lives while a session or seat shows it. Its work is ordered
by the mapped demand of the windows onto it, and it is staged by that demand,
so it is resident before its glass comes into view without a render of a view
nobody sees. Arriving means its bake is pending or its region still owes the
baked meshes' words or their upload (`SdfWorldResidency.MeshesArriving`), not
only that its bake is pending. Until staging by demand is built, an unseen
window onto an arriving world still renders at the least share
(`WorldPortalVisibility.ArrivalShare`, `WorldViewGraphHost.ReserveArrivals`);
[the plan](../plans/streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)
holds what replaces it. Within one world there is no node, and that is the gap.

Making every room a world would turn each doorway into a reserve-and-commit
transfer, stop physics and rules at every door, and give every room its own
tables, a probe table of about 193 MiB among them. Making a portal a kind of
area would lose its isometry and its crossing.

The primitives:

1. **World** (exists): the authority and its document, with one residency per
   endpoint whose lifetime follows its sessions.
2. **Portal face** (exists): the frame map, the crossing and the window. It
   also hands the selector a *mapped demand point*: the viewer's eye taken
   through the face's isometry into its destination, in the same world or a
   routed one.
3. **Residency selector** (policy, one per presentation across its residencies):
   turns demand points into priority per group and level for bake, store loads,
   landing, GI placement, and eviction. It feeds the engine job scheduler; its
   machine-byte budget is a reservation from the job system's single physical
   byte-accounting owner. Demand snapshots are published safely for parallel
   view recording; workers never read a selector while it mutates.
4. **Area** (new, optional, authored): a named volume with openings, its members
   found by containment, an opening being an aperture or a portal face. It
   reuses an existing volume vocabulary rather than adding a kind of volume.

A door is not an engine primitive: it is a creation in the standard package
whose opening a state binding closes.

## One selector, passive by default, explicit by choice

The selector supplies residency policy to the
[engine job system](../plans/open-items.md#engine-job-system), which owns
scheduling and physical byte accounting. Rings, areas, and portals contribute
demand and priority rather than creating executors.

- **Demand points** are every view's camera, every seat, every mapped eye
  through a portal face, each led along its velocity by the landing latency
  (the valley plan's "planned against the authored flight speed").
- **The passive default needs no authoring.** A group's priority comes from its
  projected size and error at the nearest demand point: the tier demanded is
  the coarsest whose cell stays under half a display pixel there
  (`SdfMeshTier.Of`), and a finer tier replaces the resident one only once the
  demand has held for `SdfMeshTier.HoldFrames`, so nothing thrashes at a rung
  ([bake demand tiers](../plans/bake-demand-tiers.md#the-rule)). It is the
  reasoning a mesh's collapse levels already switch by at draw time
  ([a raster pass draws only what it can show](rendering.md#a-raster-pass-draws-only-what-it-can-show)),
  applied to what is baked and kept.
  Rings at fixed metre radii are rejected in favour of the projected-size
  rule, because it follows the existing ruling, holds at any scale from a card
  to a mountain, and needs no tuning. The coarsest level
  stays resident within the far distance while the budget allows. A group that
  has not landed has no recorded error yet, so its planned box stands in: a box
  whose diagonal projects past a pixel is demanded, and the work that lands it
  (its lattices, its CPU stages, its store load, its words in the mesh region)
  goes nearest first, by the pixels a metre covers at the box
  (`SdfMeshDemand`). Ordering by projected size alone ranked far terrain and
  shells above the occupied room's small parts, which then landed no sooner
  than in document order.
- **An area only adds demand.** While a demand point stands in an area, the area
  and its neighbours through open or soon-to-open openings are demanded whole.
  It raises priority and never lowers it, so an author uses it where the passive
  rule mispredicts: a long sightline through a door, a wing that should arrive
  as a unit, or a world over budget, where whole unoccupied areas go first.
- **Portal destinations** are demanded from the mapped eye, so a painting's
  quarter-scale room is ready at its frame though it lies far away in world
  coordinates. A routed residency keeps its session-driven lifetime; demand
  orders its work and decides how much it holds for presentation, and replaces
  rendering a hidden view to make it bake.
- **One reservation** across every live residency, granted from the job
  system's physical byte-accounting owner and applied by selector policy. A
  byte is admitted before it is allocated, in the pool of the physical memory
  it occupies. Eviction goes by priority, never wholesale and never refused on
  a miss ([machine budgets are bytes](rendering.md)), and a device that
  refuses a growth lowers the budget instead of ending the World. A mesh
  region's limit is what its grant reserved for it
  (`SdfResidencyGrant.ReserveGrowth`), never past the region's `GpuRegion.MostBytes`,
  and the residency's detail budget never passes the limit's share
  (`SdfResidencyGrant.Budget`): a list the region would refuse presses the
  budget, so the bake coarsens detail before it leaves a hole, on every
  backend and whether the region is mapped or grows by copy. Storage the
  draws can do without (a product waiting beside the mesh it replaces, a
  mesh no list names) is held to the residency's share of the elastic budget
  (`SdfResidencyGrant.HeldLimitBytes`), never to the room the pool shows:
  that room belongs to the fixed storage of views that have not opened yet,
  and a region that filled it would push that storage past the pool's limit.
  A share that falls under what the region holds takes that storage back
  (`SdfMeshRegionArena.SetLimit`). A drawn mesh and a kept floor stay, since
  refusing either leaves a hole, and the budget coarsens the drawn meshes.
- **A refusal never ends the World.** The account refuses only storage whose
  holder can do without it: an elastic, workspace or cached owner past its
  floor, an allocation made as a replacement, or work that can wait. Storage
  that cannot give way is admitted past its pool's limit and past a device
  profile's authored bars, counted against its taker, and marks pressure
  (`MemoryAccount`). A bar is a play budget the process degrades under. A
  device profile's bar on device memory leaves headroom under it
  (`MemoryAccount.SetDeviceBar`), as the host's does, so storage admitted
  past the limit still lands under the bar while the elastic owners give
  its room back.
- **The unit that leaves is a bake tier.** Each group's floor tier, its own
  unrefined lattice (`SdfMeshTier.Floor`), stays as its floor, and the tier a
  view demands is detail above it, so evicting hands the draws back to the
  floor and bakes nothing.
- **The ladder does not end at the floor.** Past its floor a tier coarsens the
  lattice itself, down to a rung whose cell fits only a few times along the
  group (`SdfMeshTier.Coarsest`), so a group a view resolves at a few pixels
  holds a few cells. A ladder that ends at the floor prices every far group,
  and every world behind a small glass, at its floor: the debug world's
  floors alone are most of its mesh region and most of its groups draw
  theirs.
- **The bias stops at the floor.** A budget's pressure coarsens a demand as
  far as the group's floor and no further (`SdfMeshTier.Demanded`); only what
  a view cannot resolve finer holds a coarser lattice. A bias that reached
  past the floor took the world a player stands in down to a few cells a
  group, lettering and thin parts first, to pay for storage detail does not
  own. Memory the floors cannot give back is the fixed classes' to give.
- **Prediction decides arrival, and observation decides retention.** What a
  view would resolve is computed ahead from planned boxes, so a turn or a
  first sight finds its meshes. What a pass actually used is read back, and
  under pressure detail nothing uses leaves before detail in view coarsens.
- **Residency is broader than visibility.** Shadow casters and GI blockers
  within their support regions stay resident though offscreen, and the
  full-detail floor reaches at least as far as any interaction, since picking
  reads the GPU.

## A door never waits for loading

Metroid holds a door shut until the next room has loaded. Here that would make
a state change depend on one machine's disk and GPU, so replays and players
would diverge (rule 4). A door opens on simulation terms only: a rule, a key, an
authored opening time. The selector treats the opening as a deadline: demand
reaches the far side through the area graph before the door is reachable. A
miss degrades presentation only: the coarse level, the portal's fallback, or a
crossing's held image, and it is counted. An author who wants the Metroid beat
authors a door that takes time to open. A gameplay loading barrier, if ever
wanted, is a separately designed, ordered and replayed authority event.

What a miss may look like and what normal play may show are different bars.
"Never a hole" bounds the first: a floor, haze, a fallback or a held image
each satisfies it. The [seam ruling](#the-owners-rulings-on-look-and-policy)
sets the second: any of those is a seam when a player sees it, so a crossing
in normal play shows no miss at all.

Two more guards: no rule fact ever exposes whether something is resident, and a
settled capture waits until its views' demanded set has landed, so captures do
not vary with landing time. Bake order affects latency only: a mesh is a function
of its geometry, and its identity hashes only what the bake reads.

## Order of work

[The streaming and memory plan](../plans/streaming-and-memory.md#the-build-order)
holds the build: its order and step names, where each stands against the
code, the decisions it rests on and the reading that judges each step. No
other page numbers the steps. What the build delivers, each taken when the
measured need appears:

- **A measure:** time until everything near the spawn has landed (cold and
  warm), mesh bytes by level, `ProbeCache` bytes after a walk and a valley
  flight, program build and replan time against placement count, and store
  read rate against the landing rate.
- **Near-first ordering:** `SdfMeshDemand` ranks demand by pixels per metre at
  the nearest demand point, and `SdfMeshPathBake.Residency` applies it to
  groups and store loads. This is the base policy for the engine scheduler;
  the occupied room returns first.
- **Lighting on the same demand points:** the lighting reads the selector's
  demand points in place of its own view list (each level of its window
  already centres on every view that fits, anchored on the first:
  `SdfMeshIrradiance.WindowLow`), the irradiance scene and its pending gate are
  scoped to what the window reaches, `ProbeCache` is byte-bounded, and a
  place's lighting is ready before it is first seen.
- **Mapped demand through portal faces**, with a world staged by that demand
  in place of the blanket baking share.
- **Eviction under the byte budget**, by bake tier above a floor. A tier owns
  its vertices, so dropping one frees them.
- **Authored areas**, tried first on the gallery's floors: each level an area,
  the stairwell, the lift shaft and the window bands its openings (the outdoors
  is then an area beside every floor). Distance and projected size misjudge
  floors, since the level above is a few metres away behind a slab; an occupied
  floor and its neighbours through openings rank first, and under a lowered
  budget unoccupied floors evict first. The measure is time until the occupied
  floor has landed, with and without the areas. Approaching an opening raises
  the next floor gradually; nothing switches at a trigger.
- **Source tiles for terrain** as units in the same selector, meeting under
  the plan's one
  [seam rule](../plans/streaming-and-memory.md#the-source-tile-and-its-seam-rule),
  then the moons through routed residency.

Document and program assembly become spatially incremental only if measurement
shows them dominating after this. Area-scoped rule sleep, driven by authoritative
body occupancy rather than views, is legal under rule 4 but is a simulation
design and needs its own decision. Card tables depend on none of this
work; the board game room on level 2 against a spawn in the lobby makes them a
test case for near-first ordering.

## The owner's rulings on look and policy

- A group that has not landed, or was evicted, shows its coarsest level, which
  stays resident within the far distance; where no level is resident it reads
  as distance haze in the world's sky colour, never a hole.
- Level-of-detail switches cut, since the projected-error rule already places
  them below a pixel; a group that lands late fades in with a short dither.
- Closing doors come after the floors: one door in the standard package, its
  opening time authored there, once the scheduler and areas have landed.
- The resident-byte target is set on the RTX 4070; the Steam Deck is a second
  qualification on the same budget mechanism.
- A seam is whatever breaks immersion for the player in normal play conditions.
  A crossing between worlds is seamless when nothing a player sees, hears or
  feels in the controls gives it away: no hitch, pause, correction, camera
  jump, dropped input, audio cut, lighting snap or late surface. That the world
  is split into worlds, shards or authorities is not a seam; a transfer that
  shows only in logs or tooling is no leak ("Learning that their world is
  sharded by mining logs isn't a 'leak'").
- Normal play conditions are a player's real setup: a fresh process on a bar
  device, each backend the device ships, the prewarmed mesh store a player's
  build ships, the first crossing after boot and the return. An empty store or
  a lowered budget is reported beside the acceptance and never is it.
- The cold-bake bar's minute covers what the boot's views demand; a tier a later
  view asks for is a cache miss, derived and counted, and a tour's whole bake is
  a trend beside the bar.

The seam ruling is read by
[the seam walk](../plans/streaming-and-memory.md#the-seam-walk): the debug
world's crossings walked as a player takes them, with every counted miss a
failure.
