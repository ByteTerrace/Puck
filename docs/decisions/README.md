# Design decisions

These pages explain choices that affect more than one subsystem. Current API
contracts belong in the subsystem guides; proposed changes belong in
[plans](../plans/README.md).

- [Engine design decisions](engine-design.md): document ownership, simulation and
  presentation boundaries, composition, and verification policy.
- [State and the authoring language](state-and-language.md): the rebuilt
  state substrate, its ceilings as prices, the language as a projection with
  modules, records, pools, and tests as worlds, the costing model, and the
  acceptance games.
- [Runtime and delivery](runtime-and-delivery.md): products and their
  manifests, the ROM ledger, compiled worlds, release pairs, the runtime's
  ownership boundaries, the presentation view and its two transports, and
  release evidence.
- [Machines and cartridges](machines-and-cartridges.md): named machine
  identity under retained screens, the settled hosting contracts, firmware and
  content policy, and the cartridge program model.
- [Play](play.md): the reference game's shape, creatures and scale, groups
  and matchmaking, and the agent surfaces.
- [Rendering](rendering.md): fixtures before features, attachment ownership,
  the hybrid resolve, the persistence boundary, distribution, and
  [how worlds reach the GPU](rendering.md#how-worlds-reach-the-gpu) — the pass
  interface and its generated declarations, one binding contract grouped by
  update frequency, HLSL through the pinned DXC, the state mirror at
  presentation time, residency from adapter properties, and tiers as variants.
- [Worlds and federation](../architecture/worlds.md): authority, admission,
  transfers, and the relationships between worlds.

A decision should state the problem, the chosen approach, and its consequences.
When it changes, update both the decision and the affected contract. Preserve
useful reasoning without making historical implementation details mandatory
reading for new contributors.
