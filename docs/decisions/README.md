# Design decisions

These pages explain choices that affect more than one subsystem. Current API
contracts belong in the subsystem guides; proposed changes belong in
[plans](../plans/README.md).

- [Engine design decisions](engine-design.md): document ownership, the paths a
  document names, simulation and presentation boundaries, composition, the one
  acting identity and what a grant names, portals as planes a body passes
  through, [combat as world content on generic queries, fields and input](engine-design.md#combat-is-world-content-on-generic-queries-fields-and-input),
  [weather as a region's composition of generic pieces](engine-design.md#weather-is-a-regions-composition-of-generic-pieces),
  and verification policy.
- [State and the authoring language](state-and-language.md): the rebuilt
  state substrate, its ceilings as prices, the language as a projection with
  modules, records, pools, and tests as worlds, the costing model, and the
  acceptance games.
- [Companions across instances](companions.md): a companion is the player's; code never crosses, the destination accepts the visitor's pinned driver, runs its own, or refuses it, a refused or separated companion goes home and the player is told, and home re-adopts it by incarnation.
- [Residency follows what is observed](residency.md): only presentation and
  result-invariant caches follow the observer; portals are edges, worlds and
  optional authored areas are nodes; one selector orders and bounds residency
  from every demand point; a door never waits for loading.
- [Input channels](input-channels.md): typed packed input, shared action names,
  authoritative modes and slot allocation, authored switch continuity, timed input
  history, and the live-document migration.
- [Runtime and delivery](runtime-and-delivery.md): products and their
  manifests, the ROM ledger, compiled worlds, release pairs, the runtime's
  ownership boundaries, the presentation view and its two transports, and
  release evidence.
- [Machines and cartridges](machines-and-cartridges.md): named machine
  identity under retained screens, the settled hosting contracts, firmware and
  content policy, and the cartridge program model.
- [Play](play.md): the reference game's shape, creatures and scale, groups
  and matchmaking, and the agent surfaces.
- [UI](ui.md): one retained tree as presentation, per-seat roots, a neutral
  design language with looks layered on top, and mods' UI in a presentation
  entry.
- [Rendering](rendering.md): the baked-mesh pipeline, counted work as evidence
  beside quiet-machine worst-frame timing, attachment ownership, the hybrid
  resolve, the persistence boundary, distribution, hardware- and
  environment-only checks run once at the programme's
  close, [limits that price the mesh pipeline](rendering.md#limits-price-the-mesh-pipeline-not-the-interpreter),
  [bloom as a camera's scatter](rendering.md#bloom-is-a-cameras-scatter-applied-once-before-tonemapping),
  and
  [how worlds reach the GPU](rendering.md#how-worlds-reach-the-gpu) — the pass
  interface and its generated declarations, one binding contract grouped by
  update frequency, HLSL through the pinned DXC, the state mirror at
  presentation time, residency from adapter properties, and tiers as variants —
  and [the frame graph and nesting](rendering.md#the-frame-graph-and-nesting):
  the graph as a document, views as graph instances, sources by transport and
  content class, hit-to-source mapping, temporal reconstruction, HDR output,
  and baked assets derived from SDFs; and
  [global illumination](rendering.md#global-illumination): a three-level
  irradiance volume traced on the CPU against baked meshes, shared by the
  residency's views, with sampled first-hit surface visibility and nearest-clear
  probe relocation.
- [Worlds and federation](worlds-and-federation.md): what the MMO foundations
  round settled about trust between worlds: three answers for anything worth
  fighting over, scarce value at one settlement door per sealed domain,
  neutrality as a vouched role, bodies under custody with the active seat as
  the domain's right, what a visited world may write, state across author
  edits, hidden and visible seams, the tables, contested places, revocation,
  player hosts, and identity, storage and hosting as abstractions. The model
  itself (authority, admission, transfers, and the relationships between
  worlds) is [the architecture page](../architecture/worlds.md).

A decision should state the problem, the chosen approach, and its consequences.
When it changes, update both the decision and the affected contract. Preserve
useful reasoning without making historical implementation details mandatory
reading for new contributors.
