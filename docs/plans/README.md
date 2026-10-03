# Plans

This directory holds the work that remains to be scheduled. Current architecture lives in the owner documents under `docs/architecture/`, and the reasoning behind each choice lives in [the decision registers](../decisions/README.md); a plan here holds packages, each with what it owns, what it delivers, and the check that closes it. [Open items](open-items.md) is the checklist of those packages.

The work is six programmes. Each has one forcing artifact, and the artifacts overlap: the forcing world is built by the first programme, shipped by the second, filled by the third, played by the fourth, and laid out by hand in the sixth, so one build proves several plans. Each programme is one page and opens with an implementation status; treat it as evidence about the code it was written against, and recheck the code before scheduling work from it.

## [State and language](state-and-language.md)

The state system, its ceilings and costs, the authoring language, and the games that prove them, as one page of packages. Forcing artifact: the forcing world, Rulepush, and Go at 19×19. Its decisions are in [the register](../decisions/state-and-language.md).

- [State system rebuild: the landing](state-rebuild.md)—what is still in flight before the rebuilt substrate lands.
- [Abstract-machine costing](abstract-machine-costing.md)—the specification of the price schedule, its evidence, composition rules, and bounded search.

## [Runtime and delivery](runtime-and-delivery.md)

How a world is composed, compiled, packaged, released, and rolled back, and what a client may observe of it, as one page of packages: the product tree, the ROM ledger, compiled worlds, the presentation view, release pairs, and one evidence package. Forcing artifact: the quilt built as a product, shipped as compiled worlds inside it, deployed, rolled back, and restored. Its decisions are in [the register](../decisions/runtime-and-delivery.md).

## [Machines and cartridges](machines-and-cartridges.md)

Hosted machines under screens and the cartridges they run, as one page of packages: the cabinet module, cabinet authoring, optional distribution, firmware and content policy, the program model, asset ingestion, the content library. Forcing artifact: the arcade's cabinet module used twice, with a retail-scale cartridge in one. Its decisions are in [the register](../decisions/machines-and-cartridges.md).

- [Humble Gaming Deck](humble-gaming-deck.md)—the proposed NES and Famicom emulator core, the shared-layer changes it needs, and its accuracy target.

## [Play](play.md)

The reference game's remaining engineering, its creatures and scale, the finder that admits a party across authorities, and the MCP adapter, as one page of packages. Forcing artifact: the quilt played by a group the finder admitted, with an agent authoring through MCP. Its decisions are in [the register](../decisions/play.md).

## [Rendering](rendering.md)

Durable GPU fixtures, per-pass work counters, general graphics attachments, shared mesh-and-SDF visibility, reproducible shader authoring, how authored world data reaches the GPU, and the frame graph that replaces the SDF engine as the host of rendering: nested views, image sources, hit-to-source mapping, temporal reconstruction, HDR output, and assets derived from SDFs, as one page of packages. Its foundation packages share no code with the other programmes; the state mirror that carries bound rows to a pass reads the state substrate and the presentation view, so those packages are scheduled against both. Its decisions are in [the register](../decisions/rendering.md).

## [Editor](editor.md)

The running World as the editor and debugger a builder needs, as one page of packages: build mode with the grid and snapping, selection and picking, undo and redo, debug views in every view, the inspector, answers to "why is this dark or invisible", gizmos, the editor camera, per-object cost and a GPU timing readout, live reload with before-and-after, saving edits back to source, the shape gallery as a world, and carving. Forcing artifact: a district of the forcing world laid out, lit and debugged in build mode without typing a coordinate.

The current world architecture is owned by [Worlds and federation](../architecture/worlds.md). The [Reference game design](../game/design.md) defines the reference game requirements; its implementation work is in [Play](play.md).

S7's engine and language substrate is present, including generation-aware
single and pair pools, snapshots, lexical claim/release/iteration, and authored
round trips. Its World integration is present too: logical enum carriers on
bodies, pair interactions through the World host, identity-owned record transfer,
and the Paddleball and Arena migrations. It remains an open package until
remote home adoption of identity records closes
([records and pools](records-and-pools.md#remote-home-adoption)) and records
carry the timed row traits they lack today, which
[S7](state-and-language.md#s7--records-and-pools) names as the condition for
converting rows that use them. The evidence list stays in the
[records and pools contract](records-and-pools.md).
