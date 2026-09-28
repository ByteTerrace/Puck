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

### [Humble Gaming Deck](humble-gaming-deck.md)

- [x] The shared-layer rename to `Puck.Machines`.
- [x] Neutral contracts: rational clock rates and multi-port input.
- [x] The CPU and the bus.
- [x] A complete NTSC machine.
- [ ] NTSC accuracy (in progress: AccuracyCoin runs against a recorded ledger).
- [ ] Common boards; regional hardware; long-tail boards; expansion audio.
- [ ] The Famicom Disk System; peripherals; World cabinets.

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
- [x] P4 shared opaque visibility (every build step, P4-0 to P4-2e and the visibility record's names, landed; the mesh canaries hold every scene the check names, the full-size resize included, and no reader of the retired layouts remains; the measured cost is held with P14's counted-cost ceilings).
- [x] P5 reproducible authoring and packaged dependencies.
- [ ] P6 representation experiments.
- [x] P7 the binding contract and the adapter memory profile, with the one-day spike as its gate (every P7b step landed; the gate's Linux bytecode leg is deferred to the end).
- [x] P8 the shader package, the pass interface and its generated declarations, the echo pass, and HLSL as the one source language.
- [x] P9 the state mirror and presentation time, against [the presentation view's](runtime-and-delivery.md#the-presentation-view) state interface.
- [x] P10 bound rows reaching a pass: the `parameter` and array statements, the deterministic tick, the capture's tick verdict, pricing, and tiers.
- [x] P11 the frame graph document (`puck.render.graph.v1`), views as graph instances scheduled by demand, and self-reference through the previous frame.
- [x] P12 image sources: uploaded, imported, and rendered transports, content classes, producer registration, shared conversion passes, and every `WorldScreenSource` kind migrated (every P12b step landed, and the `uploaded-sources` canary checks the unbound glass, a session screen, an opened capture and a `text` screen's drawn text; P12b-4's recorded camera run is deferred to the end).
- [ ] P13 hit-to-source mapping published as data, with simulation, host passthrough, and presentation destinations, and passthrough only for local-user sources (P13b steps 1, 2, 3, 5 and 6 landed, step 2 with a machine's light gun reading the mapped pointer, step 3 with shared per-instance GPU identity and one-pixel readback plus both-backend hovered-pane outline captures, step 5 with the GPU drawing every screen from its mapping, and step 6 with a pick through a portal's window reaching the destination's surface, checked by the `portal-window` canary; the [editor plan's E2](editor.md#e2--selection-picking-and-highlight) uses that same picker. Only step 4's owner-recorded Windows click and focus return remain).
- [x] P14 the SDF engine as a pass package: the capability matrix, the generated frame block, the HLSL module tree, staged shading, float working targets, and the retirements (steps 1 to 13 landed: every SDF view is an `sdf.world` instance of the render graph whose passes the planner orders and barriers, over one generated pass block, a layered kernel tree, one kernel table and the pass-pipeline cache; the capability matrix law, the ISA handshake and the field-per-kernel kernel record are gone).
  - [x] P14 counted-cost ceilings, built as P15-1: march steps and texels written are `GpuWork` kinds every pass's shaders count, and `puck counters --check` holds per-pass ceilings and required zeros over `puck counters`' pinned workload (`puck.counters.ceilings.v1`), recorded on the RTX 2060 at the floor tier.
  - [x] Checks for the capability rows the matrix law held without one, each failing with its capability removed:
    - the live program report: `SdfWorldResidencyReportLawTests.TheLiveProgramReportIsTheResidentProgramsAndTheFramesVolumes`;
    - render scale: `WorldViewPlacementLawTests.ALoneViewAtEachRenderScaleTierIsScheduledAtThatTiersExtent`, and the `portal-window` and `view-screens` canaries' pane lines at three-quarter scale;
    - decals: `SdfWorldTablesUploadLawTests.AScreensGlyphDecalReachesTheDecalTableAndAnUnchangedDecalOwesNothing`, and the `uploaded-sources` canary's text screen;
    - the glyph atlas: the `uploaded-sources` canary's `the-text-screen-draws-its-glyphs`, which samples it through a text screen's decal; the glyph shape's read of it has no check;
    - volumes: `SdfWorldTablesUploadLawTests.AFramesBoundedVolumesReachTheVolumeTableRowForRow`; the volume shading stage's read has no check;
    - the shading levers: `WorldRenderLeverFrameLawTests` (settings to frame), `SdfFrameBlockLawTests.EachShadingLeverWritesItsOwnMemberAlone` (frame to pass block) and `SdfWorldPassesLawTests.TheAmbientAndShadowPartsRunExactlyWhenTheirLeversTurnThemOn` (the stages a frame runs);
    - debug views: `SdfWorldResidencyReportLawTests.TheDebugViewModeReachesEveryPassBlockWhenSetBeforeTheTablesOrAfter`, and the `visibility` mode's pixels in the `sdf-visibility-fresh`, `sdf-mesh-visibility`, `sdf-mesh-motion` and `sdf-bake-switch` canaries;
    - brick baking's live half, a height field's host-baked brick: `SdfWorldTablesUploadLawTests.AHostBakedBrickLandsInItsPoolSlotAndTheSlotTurnsReady`.
  - [ ] Unwired, wired by the [editor plan](editor.md) (E4 debug views and levers): the slice view's world-axis plane (`DebugSliceAxis`, `DebugSliceOffset`), and the levers `DisableShadowCull`, `DisableScreenLights`, `EnableShadowProxy` and `UseFiniteDifferenceNormals`. Each reaches the pass block and the kernels, and no World setting or verb sets it. The grid overlay is wired by E1's build mode.
  - [ ] The GPU brick bake (`RequestBrickBake`, the bake kernel and `SdfCarveBakePlanner`) gets its live producer in the [editor plan](editor.md)'s carve brush (E13); a live `sdf.bricks` instance goes with it.
  - [x] P14-8 follow-up: a render node records one command list per instance per frame slot.
  - [x] P14-8 follow-up: the mesh pass is a conditional package pass that records nothing on a meshless frame.
  - [x] The camera-view export no longer aliases the image SDF screens sample: an exported view renders its own per-slot outputs and copies each released frame into the export.
  - [x] P14-8 follow-up: the world tables bind through the group-1 World set that also holds the bake atlases, owned by the tables per ring slot and written once.
  - [x] P14-11a: one visibility record and one body per hit stage; only primary reads the mesh target, and a textured mesh's atlas albedo, material and emission reach the views pass.
  - [x] P14-11b: every light, the environment's and each bound screen's, answers through one interface.
  - [x] P14-11c: the shadow stage as a pass of its own, skipped with the ambient pass when the tier turns them off.
  - [x] P14-8 follow-up: an `sdf.world` recorder retargets to ready tables in place when the counts, pipeline layouts and mesh render pass agree, including on capture frames (`SdfWorldPasses.CanFollow`; a change it cannot follow still holds the departed image, `RenderGraphRuntimeLawTests.ResidencySwitchHeldFrames`).
  - [x] P14-8 follow-up: quality is each view's (`SdfViewSnapshot.Quality`), and an endpoint's scene takes window views beside its seats' through `WorldFramePresenter.AttachWindow`, rendered from the scene's one residency.
  - [x] P14-8 follow-up: a portal window's session view attaches to its destination endpoint's scene and reads the endpoint's mirror, so a routed seat view and the window of one destination share one residency, while its session discloses everything (`WorldSessionWindowRoute`, `WorldRoutedPresentationLawTests.SeatsAndFullyDisclosedWindowsShareOneSceneAndARestrictedWindowNeverJoinsIt`); the routed node's counters row reports its residencies and table bytes (`WorldRoutedResidencyCounts`).
  - [ ] P15-4 follow-up: a view instance that authors a pixel resolution renders exactly that many texels, with its camera's aspect from that extent (rendering plan P15-4's open item).
  - [ ] Bound the SDF residencies' aperture footprint on floor hardware. Camera views share the world's tables and brick pool; session and routed residencies carry their own tables. Measure the live residency count, `gpu.memory.device-local.*` and `gpu.memory.host-visible-device-local.*` rows and the Vulkan heap report on the RTX 2060. Share tables where worlds read the same mirror, or stage large and rarely written tables where the aperture budget requires it (rendering plan P14-8).
  - [x] Camera views render as views of the world's own residency: the shipped world runs one residency where it ran six, and its aperture peak on the RTX 2060 falls from 180.2 MB to 41.6 MB (rendering plan P14-8).
- [ ] P15 temporal reconstruction: jitter, motion vectors, the temporal upscaler, per-instance history, dynamic resolution, and march seeding, in eight steps, each landing alone (P15-1 counted work, P15-2 period-eight jitter, history epochs and frozen converging captures, and P15-3 visibility-derived motion landed; temporal canaries and reprojection device laws pass on both backends, and parity passes with reconstruction off; P15-4 through P15-8 remain).
  - [x] P15-1: counted march steps and texels written, the brick writes' upload bytes attributed to a pass, and the ceilings file of per-pass ceilings and required zeros that `puck counters --check` holds at the floor tier on the RTX 2060.
  - [x] P15-2: jitter from a Halton sequence of period eight indexed by each instance's history, history epochs that reset on a view change, a follow or portal crossing, a cut, an extent change or a view shown again, and the `converge` capture row over a frozen presentation snapshot in which only the jitter index advances.
  - [x] P15-3: motion derived from the visibility record, with the previous view in the pass block and device-local previous dynamic-transform and compact mesh-matrix tables each residency's upload keeps. Transform host upload bytes stay unchanged; successful device-buffer copy bytes are counted, and settled still frames copy no previous poses.
  - [ ] P15-4: each view renders a render extent inside its output extent, and a spatial `resolve` pass replaces `place`'s upsample of a view, writing a resolved surface (nearest-sample depth, coverage in the color's alpha) in both modes.
  - [ ] P15-5: the temporal resolve, with history per instance validated by identity and depth, a reactivity image of its own (coverage keeps the color's alpha), `place`'s sharpen, and `converge` stations in the parity world.
  - [ ] P15-6: dynamic resolution inside the render extent's ceiling, never reallocating, driven by present timing through an injectable timing source, with a counted march-step budget where present timing is unavailable.
  - [ ] P15-7: march seeding from the previous frame's depth, taken only where a Lipschitz ball test proves the skipped segment empty.
  - [ ] P15-8: the floor tier's defaults for reconstruction, dynamic resolution and seeding, the lead's call from the counted rows.
- [ ] P16 minimal HDR display output: the display-transform node, an HDR swapchain on Windows, paper white for UI, and one HDR source (the Direct3D 12 heap fold, HDR swapchain selection and the paper-white setting landed; the display-transform node, requesting HDR, the HUD at paper white and the HDR source remain).
- [ ] P17 assets derived from SDFs: the baker, the texture pipeline, and the content-addressed cache shipped in compiled worlds and filled on the device on a miss (the baker, its codecs, the block-compressed upload, the one pixel-format vocabulary and a ready bake's mesh drawn in place of its field, with normals, texture coordinates and triangle materials, landed; a bake's textures and impostor and the parity world's shipped bakes remain).
- [ ] P18 sky and atmosphere: typed bodies, an open layer stack and an atmosphere keyed on clocks from the tick, evaluated once where they are seen, lighting derived from the same sky, any number of shadowed lights, a cadence per pass, and cost counted per layer and per shadowed light, in fourteen steps, each landing alone.
  - [ ] P18-1: the sky parity station, discriminating sky canaries and four counted sky workloads have landed and pass on both backends; the RTX 2060 floor recording remains owner-assisted.
  - [x] P18-2: one clock family on the presented engine tick, exact and unsigned 64-bit, with bounded media, twinkle and cloud wind on it and each routed or session scene on its own endpoint's clock.
  - [ ] P18-3: keys on clocks for every bindable presentation value, blended by the field's type, with one resolver replacing `render.cycle`.
  - [ ] P18-4: the sky block and the lights table as regions with generated decoders, and the environment out of every pass block.
  - [ ] P18-5: the sky evaluated once, only where a pixel is uncovered, and a composite pass for the sky, fog and bounded media; the pinned sky branch deleted and parity re-recorded.
  - [ ] P18-6: a cadence per pass, so a sky-only change runs only the sky and the composite.
  - [ ] P18-7: celestial bodies with light binding and illumination, and up to four shadowed lights chosen by slot and tier, a slot changing hands by a counted crossfade.
  - [ ] P18-8: the open, ordered layer stack with one module per kind, the sky frame, per-layer tiers, one noise module and the `skies.puck` presets.
  - [ ] P18-9: ambient and reflection derived from the same sky through a shared environment map, replacing the hemisphere light, the horizon and the softboxes.
  - [ ] P18-10: the atmosphere: fog, height fog, haze, a medium, and the bounded media under it.
  - [ ] P18-11: infinity views, a sky or a body that shows another world or far SDF geometry, following S27 and S28, with a per-world cap on infinity views refused by name.
  - [ ] P18-12: the sky in the editor's inspector, reload, compare and save, with clock levers and sky debug views.
  - [ ] P18-13: temporal amortization of secondary shadows, following P15-5.
  - [ ] P18-14: the floor tier's sky defaults, the lead's call from the counted rows.
- [ ] Later display work, unscheduled: HDR calibration, per-display metadata, and HDR on the Steam Deck OLED under Linux.
- [ ] Later source work, unscheduled: Linux producers for PipeWire DMA-BUF capture and V4L2 cameras.

## [Editor](editor.md)

Every package below carries its own check on the programme page; tick it there and here in the same change.

- [x] E1 build mode, the grid and snapping, checked by the `editor-grid` canary.
- [ ] E2 selection, a visibility identity per drawn instance, GPU and CPU picking, and highlight.
- [ ] E3 undo, redo, duplicate, delete and measure.
- [ ] E4 debug views everywhere (startable today).
- [ ] E5 the inspector.
- [ ] E6 why is this dark or invisible.
- [ ] E7 gizmos and pointer dragging.
- [ ] E8 editor camera.
- [ ] E9 cost per object and GPU pass timing (startable today).
- [ ] E10 live reload and before-and-after: watched source and import changes use ordinary authenticated reloads, preserve editor state and report refusals; before-and-after presentation remains open, with its image-difference metric shared with the canaries.
- [ ] E11 source-preserving save for authored rows has landed, with named generated-row refusals; E3's live duplicate and rename workflow remains.
- [ ] E12 the shape gallery as a world, retiring `Puck.SdfVm.Debug` once the gallery reaches parity with it.
- [ ] E13 carving and the brick bake: a carve brush whose dabs are document rows.
