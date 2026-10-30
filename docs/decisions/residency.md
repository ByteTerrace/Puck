# Residency follows what is observed

As a world grows, what the player can see should be built, loaded and kept
first, and what nobody sees should cost little. This decision settles which
layers may follow the observer, how passive engine help and an author's explicit
choice combine, and where portals fit.

The owner set the position: passive help from the engine everywhere and an
explicit choice by authors (Metroid Prime style areas joined by doors) are
complementary, and asked whether portals are the right abstraction for the
explicit route. Claude, Codex and Antigravity each drafted an answer
independently against the code; this record is the reconciliation.

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
(`routed$<digest>`) lives while a session or seat shows it, and whose unseen
window keeps baking at the least share (`WorldPortalVisibility.BakingShare`,
`WorldViewGraphHost.ResetFootprints`) so it is ready before its glass comes into
view. Within one world there is no node, and that is the gap.

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
3. **Residency selector** (new, one per presentation across its residencies):
   turns demand points into one priority per group and level, which orders
   bake, store loads and landing, places the GI window, and decides eviction
   under one machine byte budget.
4. **Area** (new, optional, authored): a named volume with openings, its members
   found by containment, an opening being an aperture or a portal face. It
   reuses an existing volume vocabulary rather than adding a kind of volume.

A door is not an engine primitive: it is a creation in the standard package
whose opening a state binding closes.

## One selector, passive by default, explicit by choice

There is one scheduler; rings, areas and portals are inputs to it, never
separate systems.

- **Demand points** are every view's camera, every seat, every mapped eye
  through a portal face, each led along its velocity by the landing latency
  (the valley plan's "planned against the authored flight speed").
- **The passive default needs no authoring.** A group's priority comes from its
  projected size and error at the nearest demand point: a level is demanded
  where the coarser level's recorded error would project past about a pixel,
  with a hysteresis band, which is the rule a level of detail already switches
  by ([a raster pass draws only what it can show](rendering.md#a-raster-pass-draws-only-what-it-can-show)).
  Antigravity proposed rings at fixed metre radii; Claude and Codex the
  projected-size rule, adopted because it follows the existing ruling, holds at
  any scale from a card to a mountain, and needs no tuning. The coarsest level
  stays resident within the far distance while the budget allows.
- **An area only adds demand.** While a demand point stands in an area, the area
  and its neighbours through open or soon-to-open openings are demanded whole.
  It raises priority and never lowers it, so an author uses it where the passive
  rule mispredicts: a long sightline through a door, a wing that should arrive
  as a unit, or a world over budget, where whole unoccupied areas go first.
- **Portal destinations** are demanded from the mapped eye, so a painting's
  quarter-scale room is ready at its frame though it lies far away in world
  coordinates. A routed residency keeps its session-driven lifetime; demand
  only orders its work, and replaces rendering a hidden view to make it bake.
- **One budget**, applied in the selector, across every live residency. Eviction
  goes by priority, never wholesale and never refused on a miss
  ([machine budgets are bytes](rendering.md)), which removes today's throw past
  `GpuRegion.MaxByteCount`.
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

Two more guards: no rule fact ever exposes whether something is resident, and a
settled capture waits until its views' demanded set has landed, so captures do
not vary with landing time. Bake order affects latency only: a mesh is a function
of its geometry, and its identity hashes only what the bake reads.

## Order of work

Each step is taken when the measured need appears:

1. **Measure:** time until everything near the spawn has landed (cold and warm),
   mesh bytes by level, `ProbeCache` bytes after a walk and a valley flight,
   program build and replan time against placement count, and store read rate
   against the landing rate.
2. **Near-first:** order lattices, store loads and owed meshes by priority from
   demand points. Today all three run in document order
   (`SdfMeshPathBake.TryTakeForFrame`, `SdfWorldTables.MeshRegion`). Small, and it
   serves the edit-to-run target: the room you stand in returns first.
3. **GI on the same demand points:** the probe window follows every view, not
   `Views[0]` alone, the irradiance scene and its pending gate are scoped to what
   the window reaches, and `ProbeCache` is byte-bounded.
4. **Mapped demand through portal faces**, replacing the blanket baking share.
5. **Eviction under the byte budget**, with coarse levels owning compacted
   vertices so dropping a fine level frees its vertices, not only its indices.
6. **Authored areas**, when a world shows the need: doors that close, a
   mispredicted sightline, a world over budget.
7. **Terrain tiles** (the valley plan's step 4) as units in the same selector,
   then the moons through routed residency.

Document and program assembly become spatially incremental only if measurement
shows them dominating after this. Area-scoped rule sleep, driven by authoritative
body occupancy rather than views (Antigravity), is legal under rule 4 but is a
simulation design and needs its own consensus. Card tables depend on none of this
work; the board game room on level 2 against a spawn in the lobby makes them a
test case for step 2.

## The owner's calls

- What a late or over-budget group shows: the coarse level, the fallback colour,
  fog, or nothing; and whether level and residency switches fade or cut.
- Whether the debug world gets doors that close, and whether the gallery
  authors areas or relies on the passive rule.
- The resident-byte target machine: the RTX 4070 now, the Steam Deck later.
- Door feel: how long a standard door takes to open.
