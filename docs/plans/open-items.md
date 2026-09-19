# Open plan items

One checkbox per package, grouped by [programme](README.md); the programme page
holds each package's owner, deliverables, and check, so follow the link before
starting work, and tick or remove an item in the same change that closes it.

## Standalone

These items came from a retired plan and are not owned by any single surviving plan.

- [ ] One authored sort replacing the `sortZone` and `sortKeyed` frontends (see [Keep definitions, storage, and reads distinct](../reference/state/data-model.md#keep-definitions-storage-and-reads-distinct)).
- [x] Separate storage kind from the participant role's `Counter` and `Timer` ownership, landed as `StateParticipantRole` (see [Choose behavior deliberately](../reference/state/data-model.md#choose-behavior-deliberately)).
- [ ] Extend compilation-receipt reuse to boot, checkpoint restore, and separate hazard/budget requests; catalog shape alone is not a safe cache key (see [compiled worlds](runtime-and-delivery.md#compiled-worlds)).
- [ ] A rigid body spawned exactly touching a static surface never establishes contact: it free-falls while reporting grounded. Found authoring `games/pong.puck`, which places its ball above the floor to avoid it.
- [ ] A sphere-against-box rigid pair rests much further apart than its half-extents predict, so a Distance interaction authored at the geometric contact range never latches. `games/pong.puck` authors its paddle range at 1 for that reason.

## Cross-plan maintenance

- [x] Restore `puck bench world`: its harness supplies no machine catalog, so the shipped world's arcade engines are refused at admission before anything is measured.
- [x] Correct the `BenchRunner` comment that describes tens-of-seconds world construction, once the bench can measure it.
- [ ] Add the binding-destination escalation witness, owned by [Play](play.md)'s population package in track 2: a non-privileged principal holding `Mutate` on `section:bindings` authors an overlay naming an `Unbindable` verb and is refused, while the same mutation naming a `Bindable` verb applies. The check only runs with the real command registry installed through `WorldAffordances`, which `tests/Puck.World.Tests` never installs, so the witness needs either a non-console actor on the real executable or a scoped registry install that cannot leak into parallel tests. Until it exists the item stays security-open.

## [State and language](state-and-language.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] [The landing](state-rebuild.md): WP11c, WP11d, WP11e, the sweep, the relocation, WP13, WP14.
- [x] Embeddings.
- [ ] C1 ceilings as prices, with the reference schedule and activation.
- [x] S1 the projection law; S2 the printing tree.
- [ ] S7a pools in state, S8's runner.
- [ ] C2 to C6; S4 the construct table; S5 `puck migrate`; the costing correction.
- [ ] S3 operands as grammar; S7b records and pools; S8's construct; S6's expander; G4 Tetris, G1 Go, G16 Baba Is You.
- [ ] S6's remainder: the forcing world, and the island, districts, and shards onto its modules.
- [ ] Deferred as a block: Monopoly, Wordle, Match-3, Catan, tower defense, Ribbon.

## [Runtime and delivery](runtime-and-delivery.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] The product tree (startable today).
- [ ] The ledger (startable today).
- [ ] The facade split and its constraints, in the rebuild's WP11e.
- [ ] Compiled worlds: one validated load; the container; simulation chunks; everything else.
- [ ] The presentation view: the projection's mechanism defects and the conformance law (startable today); the static sections as disclosure decisions; target registers and the authoring envelope; bound state as per-recipient observations; the primary client onto the view.
- [ ] Release pairs: group-scoped restore, definition-edit preservation, a qualified pair with the operator exercise.
- [ ] The evidence package, against one named candidate.

## [Machines and cartridges](machines-and-cartridges.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] Asset ingestion (startable today).
- [ ] Firmware and content policy (startable today).
- [ ] The cabinet module: display identity, hardware consumers, boot-anchored reproduction.
- [ ] Cabinet authoring and player experience, after the language's modules.
- [ ] The program model: memory model, procedures and banks, interrupts, ROM residency and the save, arithmetic.
- [ ] The content library; optional distribution.

## [Play](play.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] The finder's foundation (startable today).
- [ ] MCP's local surface (startable today).
- [ ] The population: the canary widening (startable today); frames; the neighbour tape and ghosts.
- [ ] The playthrough substrate remainder; composed play.
- [ ] The content wave and the One World re-authoring; the seat; parties and matching.
- [ ] The federation wave; admission and release; MCP's remote surface; the gated ladder.
- [ ] The federation remainder; `Puck.Audio`.
- [ ] Housekeeping, last: the client seam, the signing chain, namespace normalization.
- [ ] Owner-run: the live smoke against `Web.Functions`, the feel sitting.

## [Rendering](rendering.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] P1a durable functional fixtures (startable today).
- [ ] P2 per-pass timing and the measurement collector.
- [ ] P1b foundation qualification against the candidate that ships the forcing world.
- [ ] P3 attachments and indexed geometry.
- [ ] P4 shared opaque visibility.
- [ ] P5 reproducible authoring and packaged dependencies.
- [ ] P6 representation experiments.
- [ ] P7 the binding contract and the adapter memory profile, with the one-day spike as its gate.
- [ ] P8 the shader package, the pass interface and its generated declarations, the echo pass, and HLSL as the one source language.
- [ ] P9 the state mirror and presentation time, against [the presentation view's](runtime-and-delivery.md#the-presentation-view) state interface.
- [ ] P10 bound rows reaching a pass: the `parameter` and array statements, the deterministic tick, the capture's tick verdict, pricing, and tiers.
