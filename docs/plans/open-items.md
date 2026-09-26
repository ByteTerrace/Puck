# Open plan items

One checkbox per package, grouped by [programme](README.md); the programme page
holds each package's owner, deliverables, and check, so follow the link before
starting work, and tick or remove an item in the same change that closes it.

## Standalone

These items belong to no single programme.

- [x] Honor launcher help/version actions before host setup. Native help (including `-h`) and standalone version probes exit before world/config loading and state-directory creation; invalid options still refuse. Version combined with other arguments follows the parser's refusal.
- [x] One authored `sort` with explicit numeric keys. Compilation selects own-value or token-attribute storage paths; stable ties, per-key direction, row-shape refusal, domain checks and allocation ceilings are covered (see [State transforms](../reference/state/transforms.md)).
- [x] Separate storage kind from the participant role's `Counter` and `Timer` ownership, landed as `StateParticipantRole` (see [Participant and identity slots](../reference/state/data-model.md#participant-and-identity-slots)).
- [x] Complete compilation-receipt reuse across boot admission. Every loader settles draws, document bindings, host overrides and state rows before one full admission, so the document a receipt names is the document that installs. Hazard/budget requests share installed programs and analysis; file/DSL boot, local instance preparation, checkpoint restore and the hosted asynchronous read each carry their receipt into server construction, machine preparation and post-build wiring, which rechecks environment-dependent sections without recompiling rules. A desktop boot, an instance start and a checkpoint restore each validate once and compile rules once (see [compiled worlds](runtime-and-delivery.md#compiled-worlds)).
- [x] Bind `puck format` named arguments against the actual project build closure. Both semantic passes bind against the reference set MSBuild reports for an explicit `--configuration`, so a closure cannot mix configurations or reach a stale `bin`. A configuration with no output, a named assembly that is not on disk, and a project MSBuild cannot evaluate each skip the project and say so. A named, reordered call is re-bound before it is emitted and dropped unless it resolves to the same method (see [`puck format` safety](../reference/cli.md#safety)).
- [x] Prove all outcomes of boot row sources against their consuming fields. `GeneratorEngine.TryEnumerateEmissions`/`TryEnumerateOutcomes` give a source's whole outcome set independently of seed, instance identity, cursor and masks. `host.backendRow` refuses any emission naming no backend; `bodies.capacityRow` settles each census into a candidate and puts it through the whole document validator, so the seat floor, `networkPlayers` and every authored body index are proved per outcome. An unbounded source, or one wider than `WorldBodiesLimits.MaxDrawnCensusOutcomes`, refuses by name. The settle-time reads keep only the non-drawn path — a literal, a checkpoint, or a live write, whose single outcome no source check can see.
- [x] Investigate Paddleball's exact-touch spawn and stale grounded facts. Exact-touch contact works; rigid integration now publishes its current grounded/rising/falling facts. Laws cover both exact-touch and elevated spawns, and Paddleball starts on the floor.
- [x] Replace sphere/box bounding-box contact with shared closest-point geometry for static and dynamic queries, retaining shallow compound corrections. Laws cover faces, edges, corners, interior centers, rotation and allocation. A Distance interaction still measures body roots, not collider surfaces; Paddleball's interaction range is a gameplay trigger.

## Cross-plan maintenance

- [x] Restore `puck bench world`: its harness supplies no machine catalog, so the shipped world's arcade engines are refused at admission before anything is measured.
- [x] Correct the `BenchRunner` comment that describes tens-of-seconds world construction, once the bench can measure it.
- [ ] Add the binding-destination escalation witness, owned by [Play](play.md)'s population package in track 2: a non-privileged principal holding `Mutate` on `section:bindings` authors an overlay naming an `Unbindable` verb and is refused, while the same mutation naming a `Bindable` verb applies. The check only runs with the real command registry installed through `WorldAffordances`, which `tests/Puck.World.Tests` never installs, so the witness needs either a non-console actor on the real executable or a scoped registry install that cannot leak into parallel tests. Until it exists the item stays security-open.
- [ ] Close the remaining hidden-value paths through transforms. `TransformWidensAudience` refuses a transform that writes a row a wider audience reads than a row it reads, judged from the declared row and cell policies. Two paths stay open: rows a transform reads through a dynamic key (a transfer's key, a `setRay` or `clearEnclosed` origin, a `writeSet`'s `setKey`) or a live zone end are not among its `StateTransform.Subjects()`, so a rule can still choose by a hidden cell's value and write into a public row; and visibility a cell acquires at run time, from its zone or a later write, is not considered. Security-open until both are refused or disclosed through `WorldStateDisclosure.Disclose`.
- [ ] Make Wordspy's recorded baseline play a round. Its state export stays at stage 1 with an empty clue log, so the baseline never exercises the clue-word copy the [baselines README](../../tests/Puck.World.Tests/ShippedWorldStateBaselines/README.md) describes.
- [ ] Namespace the remaining module declarations. A module used under an alias namespaces its rows, rules, placements, prototypes and look sources as `alias$name`, but spawn points, kits, look names, cameras, navigation domains and property names stay flat, so two uses of one module declaring the same spawn point declare it twice. A JSON import with `as` has no dotted `alias.name` form in `.puck`; a hand-written JSON composition can make an aliased pool's rows collide with a host pool named after the alias, which only the `.puck` door refuses (PUCK117); and the export check does not look inside a deal's variant map.
- [x] Add the schema and transpiler assemblies to the inputs of the compiled-world step in `build/WorldAssets.targets`. It lists only the `.puck` sources and `Puck.Cli.dll`, so a change to `Puck.World.Schema` that leaves the CLI assembly unchanged leaves stale compiled worlds in the build output.
- [ ] Cut the boot's remaining one-time work, measured through the `world.boot` counter source and `AllocationWindow`. A warm island boot still spends about a third of its main-thread allocation composing modules and about a third deriving curve splines. Caching composed images needs `CompileInputs` below `Puck.World.Schema` (in `Puck.Assets`) and a composition entry in `WorldCompileCache`; caching splines needs a public binary form of `CompiledCurvatureSpline` with a round-trip law in `tests/Puck.Maths.Tests`. Smaller items: creation and cartridge documents still resolve JSON metadata by reflection (`DocumentJsonOptions.Shared`); `WorldStateDocumentValues` walks values reflectively; a cold compile lowers `test` blocks the boot never runs; `WorldCompileCache.WritePersisted` copies each entry through a `MemoryStream`; and `WorldNameRegistry.Walk` reads the serializer's metadata instead of `WorldModelShape`.
- [x] Prove the World's composition without a device. `WorldBootComposition` is public and `tests/Puck.World.Tests` links `Puck.World`: `WorldBootCompositionLawTests` builds each presentation shape through `WorldBootComposition.AddWorldBoot`, the method the boot calls, and checks that the offscreen shape answers every verb the `puck counters` script sends and that both presentation shapes register the persistent pipeline cache (see [P1b](rendering.md#p1b--foundation-qualification)).
- [ ] Finish the canary runner's listener work. A single companion authority still receives a pre-picked UDP port, because its document names its own endpoint as `host.authority` before it boots; the World would need to adopt its bound `--listen` endpoint as the authority identity when `host.authority` is absent. The stub-manifest branch still builds `Puck.Launcher.Stub` in place.
- [ ] Give `world release deploy` and `rollback` the qualification exit codes. `world release qualify` exits 1 for a failed check and 2 for a refusal, but `AzureCommand.DeployWorldReleaseAsync` still throws on a refused start, so both verbs exit 2 for a failed check. `src/Puck.Azure.Resources/pointToSiteVpn.bicep` also hardcodes a subscription id, misspells `puplicIpAddress`, and lacks the section banners the Azure resources README requires.
- [ ] Let Snake grow past six segments. `history(row, age)` takes an expression age, and an age outside the ring reads the empty value (`HistoryAgeLawTests`), but [`snake.puck`](../../src/Puck.World/Assets/worlds/games/snake.puck) still reads four fixed ages, clears its tail through a branch per length, and caps `snakeLength` at six.

## [State and language](state-and-language.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [x] [The landing](state-rebuild.md): WP11c, WP11d, WP11e, the sweep, the relocation, WP13, WP14.
- [x] Embeddings.
- [ ] C1 ceilings as prices: the reference schedule's kernel, helper and memory-service prices; synchronous edit-cycle bounds; and reference-cycle admission. Every reachable operation is registered with its size parameters and an explicit unmodeled reason, and `puck bench state-evidence` reports that coverage, so the blocking set is a command's output. `puck bench state-evidence --capture <dir>` is that capture: it compiles both pinned Native AOT lowerings, walks each declared kernel's maximum permitted path under its declared loop bounds, renders the manifest sections it establishes and names every difference from what is pinned. The unary, binary, bit-field and bit-insert expression operations are priced. The function kernel needs its remaining loop bounds and its nested dispatch tables read; the evaluator, effect, transform, generator and search vocabularies need focused reference kernels with declared cold-path exclusions, because a whole-closure walk of the evaluator names thousands of unbounded runtime loops; and the memory profile needs published measurements.
- [x] S1 the projection law; S2 the printing tree.
- [ ] S7 pools: records, generation-aware handles, pair storage, snapshots, typed field references, advancing fields, logical body carriers, identity transfer, and Arena/Paddleball migrations are implemented, and the rulepush scenario is met by the [rulepush package](../../worlds/rulepush/README.md). The remaining acceptance gates are listed in [records and pools](records-and-pools.md).
- [x] C3 match positions; C4 selective token push; C5 token rewrite through pool iteration; C6 retained turn undo.
- [x] C2; S4 the construct table; S5 `puck migrate`; the costing correction.
- [x] S3 structural operand grammar.
- [x] S3 shipped-source migration: a call argument has one spelling and no source spells a colon channel outside an interpolated string.
- [x] S3 remainder: an interpolated string computes a name or text and never program text, so an expression is always written bare.
- [x] The operand tree: sugar operands carry parsed trees, generic members project structurally into them, atoms remain opaque, and module aliases reach bare binding words. Structural binding replaces the emitter's text-rewriting passes; laws cover these paths and source round trips.
- [x] S8's runner and its `test` construct for a world; S6's expander; S6's multi-world sources, `border`, `door`, and the asset lock.
- [x] S8's module tests: `with module(arguments)`, and a module's own tests running once per distinct use.
- [x] S8's composition-level distributed tests: a run arms several composed worlds, exports each at one host step and judges each world's own verdicts; a `test` at a composition root lowers to such a set; and a body crossing a `border` is asserted on the far side by the destination world's own rule.
- [x] G16 Rulepush.
- [x] G4 Tetromino.
- [x] G1 Go.
- [ ] S6's remainder: the forcing world, and the island, districts, and shards onto its modules.
- [ ] Deferred as a block: Landlord, Wordguess, Match-3, Hexcolony, tower defense, Ribbon.

## [Runtime and delivery](runtime-and-delivery.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [ ] The product tree (startable today).
- [ ] The ledger (startable today).
- [x] The facade split, in the rebuild's WP11e.
- [x] The facade's remaining constraints: checkpoint records out of the server classes, the comment-smell ledger, a lower file-length ceiling.
- [ ] Compiled worlds: the container; simulation chunks; everything else. One validated load is landed.
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

- [ ] P1a durable functional fixtures (four slices landed; the memory budget is in).
- [x] P2 per-pass work counters and the collector.
- [ ] P1b foundation qualification against the candidate that ships the forcing world (`puck qualify` and the release profile are in; the first runs on the reference GPUs are not).
- [x] P3 attachments and indexed geometry.
- [ ] P4 shared opaque visibility.
- [x] P5 reproducible authoring and packaged dependencies.
- [ ] P6 representation experiments.
- [ ] P7 the binding contract and the adapter memory profile, with the one-day spike as its gate.
- [ ] P8 the shader package, the pass interface and its generated declarations, the echo pass, and HLSL as the one source language.
- [x] P9 the state mirror and presentation time, against [the presentation view's](runtime-and-delivery.md#the-presentation-view) state interface.
- [ ] P10 bound rows reaching a pass: the `parameter` and array statements, the deterministic tick, the capture's tick verdict, pricing, and tiers.
- [ ] P11 the frame graph document (`puck.render.graph.v1`), views as graph instances scheduled by demand, and self-reference through the previous frame.
- [ ] P12 image sources: uploaded, imported, and rendered transports, content classes, producer registration, shared conversion passes, and every `WorldScreenSource` kind migrated.
- [ ] P13 hit-to-source mapping published as data, with simulation, host passthrough, and presentation destinations, and passthrough only for local-user sources.
- [ ] P14 the SDF engine as a pass package: the capability matrix, the generated frame block, the HLSL module tree, staged shading, float working targets, and the retirements.
- [ ] P15 temporal reconstruction: jitter, motion vectors, the temporal upscaler, per-instance history, dynamic resolution, and march seeding.
- [ ] P16 minimal HDR display output: the display-transform node, an HDR swapchain on Windows, paper white for UI, and one HDR source.
- [ ] P17 assets derived from SDFs: the baker, the texture pipeline, and the content-addressed cache shipped in compiled worlds and filled on the device on a miss.
- [ ] Later display work, unscheduled: HDR calibration, per-display metadata, and HDR on the Steam Deck OLED under Linux.
- [ ] Later source work, unscheduled: Linux producers for PipeWire DMA-BUF capture and V4L2 cameras.
