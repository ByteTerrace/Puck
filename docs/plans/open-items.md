# Open plan items

This checklist gathers every open item from the plans in this directory into one
place. Each plan's own implementation status review holds the evidence and the
context behind an item; follow the section link before starting work. The list
reflects those reviews as of `ae6fb8a5d`, so recheck the code before scheduling
an item, and tick or remove an item in the same change that closes it.

Two plans have no open items and are not listed: [Documentation consistency](documentation-consistency.md)
and [Game physics and movement](game-physics-and-movement.md). [README consistency and branding](readme-and-branding.md)
closed its last two items in `ae6fb8a5d`.

## Cross-plan maintenance

- [x] Restore `puck bench world`: its harness supplies no machine catalog, so the shipped world's arcade engines are refused at admission before anything is measured.
- [x] Correct the `BenchRunner` comment that describes tens-of-seconds world construction, once the bench can measure it.
- [ ] Add the binding-destination escalation witness: a non-privileged principal holding `Mutate` on `section:bindings` authors an overlay naming an `Unbindable` verb and is refused, while the same mutation naming a `Bindable` verb applies. The check only runs with the real command registry installed through `WorldAffordances`, which `tests/Puck.World.Tests` never installs, so the witness needs either a non-console actor on the real executable or a scoped registry install that cannot leak into parallel tests. Until it exists the item stays security-open.

## [Abstract-machine costing](abstract-machine-costing.md)

- [ ] Carry `CellKind` into operation pricing; the legacy table ignores it.
- [ ] Charge per-rule setup, including `forEach` key snapshots; base rules report zero.
- [ ] Price interaction carrier gathering, the `L * R` distance scan, and nearest-neighbour insertion separately from selected evaluations.
- [ ] Replace the flat 4,096 HUD upsert and removal prices with derived costs.
- [ ] Add focused State kernels to `src/Puck.Cli/Bench`, capture reference disassembly, and derive kernel coefficients per evidence target.
- [ ] Produce the fixed memory-service profile and its coefficients.
- [ ] Record coefficients, formulas, and evidence in one owning manifest and set `CostModel.EvidenceDigest`.
- [ ] Give every reachable operation in scope a calibrated price or an explicit unmodeled result, enforced by a test that enumerates the vocabulary.
- [ ] Replace `leftover / judgeCost` with per-job cycle allowances and resumable, reserved search units.
- [ ] Make root and internal chance evaluation and tree playouts resumable under the same budget.
- [ ] Remove `RuleCapacity.MaxWorkUnitsPerTick` and the old fallbacks once reference-cycle admission is authoritative.
- [ ] Wire `WorldCostReport` into the validator, console, search plans, `BrowserExports`, and the portal worker, with source-path attribution.
- [ ] Pin the model identifier and schedule digest in compiled plans, engine identity, and replay identity where needed.
- [ ] Pass the required adversarial cases in section 8 and the exhaustive small-world trace comparison.
- [ ] Compare reference costs against independent workload measurements and synchronize the State and World Schema READMEs, CLI, browser protocol docs, and skills.

## [DSL and cartridge release hardening](dsl-release-hardening.md)

- [ ] D1: audit metadata still reconstructed in several consumers and give every admitted operation one explicit interpretation.
- [ ] D2: add CGB/AGB comparison at normalized authored state on matched frame boundaries, a `Puck.Transpiler` test project, and the interaction matrix cases.
- [ ] D3: measure lowering, structural deduplication, and cancellation under bounded large inputs on a current candidate.
- [ ] D4: verify one packaged release candidate through World, including authoring, insertion, persistence, restart, and refusal paths.
- [ ] D4: re-run the pre-first-tick cartridge insertion trigger and record its closure or a current reproducer.
- [ ] D4: record the candidate's commands and results in [Game development milestones](../development/game-milestones.md).
- [ ] D5: expand the language through the retail-scale plan with the same semantic, native, and release evidence.

## [Game development](game-development.md)

### Waves and owner-run work

- [ ] Move the audio mixer, overlay frame builder, mux determinism, and audio-device failure coverage out of `experimental/scripts` into law tests or `puck` verbs.
- [ ] Estimate utterance syllable counts from dialogue or caption text.
- [ ] Correlate babble with a live body position instead of listener placement.
- [ ] Reach the idle-tick target from the playthrough remainder.
- [ ] Author the `chance` level's hidden operands.
- [ ] Re-record the multi-authority four-corners canaries at the shards' 2.75× ring.
- [ ] Content wave: the studio prologue's acts, the arena crawl's spawners and bosses, the arcade hearth's seam content, and the retail basis deltas.
- [ ] Federation wave: provenance signing for carried state, bilateral attestation rows, a profile world at a shard's seam, and the silo-hosted hub owned by the public-content identity.
- [ ] Mint a real release-signing chain, add `puck publish --sign`, and implement upload.
- [ ] Run namespace normalization once, after the splits settle.
- [ ] Owner-run: the C-3/PL-2 live smoke against `Web.Functions`.
- [ ] Owner-run: the track-4 feel sitting.

### Federation remainder

- [ ] Per-viewport user- or group-scoped destination images.
- [ ] A destination-clock interpolation ease.
- [ ] Multi-authority replay with arrival-side taping and a defined `replay.verify` crossing meaning.
- [ ] Bounded queues, backpressure, and query redaction on the observation feed.
- [ ] Derived-band read-back and a long-run remainder-drift demonstration for authored per-world time.
- [ ] Destination and session resolution on the wire.
- [ ] An unembodied session authority.
- [ ] Optional body reservation and allocation.
- [ ] Issuer-qualified group and document claims.
- [ ] Entry reservations and idempotent handoff tokens fenced by epochs or leases, with durable commit records.
- [ ] Hydrate, suspend, and migrate persisted worlds without changing identity.
- [ ] Durable recovery when an authority dies mid-transaction.
- [ ] Cross-document write-back that survives a retry.
- [ ] Cloud-catalog discovery verified beyond hermetic tests.
- [ ] Latency equalization from a real round-trip-time source.
- [ ] Close local `Join`'s pre-allocation gap through enforceable admission semantics.

### Gated ladder

- [ ] Renderers and backends select through the extension registry.
- [ ] Extensions validate their own configuration; cartridges become pinned content; renderer ceilings leave the world document.
- [ ] First-class sinks, render extent on the sink, and one view/sink compositor.
- [ ] Collapse the screen row into a placement facet with string identity and an authored camera binding mode.
- [ ] World as a screen source at a target-selected tier, a specified client wire, and replication.
- [ ] Proximity co-location, occlusion-aware candidacy, transfer stability, co-location acceptance, junction headroom, contention facts, adjacency as scheduling affinity, and tick health as a fact.
- [ ] Contact-counterpart and region-occupant targets.
- [ ] Threat tables over a keyed-table primitive.
- [ ] Spatial partitioning for proximity, once measurement shows the scan dominates.
- [ ] Native AOT for the game.

### Unverified and undecided

- [ ] Verify session-lever routing (`world.volume`, the render levers, `world.save`).
- [ ] Verify a screen route's pad kit and channel masks.
- [ ] Verify whether fuel is still the only stop for a spinning guest.
- [ ] Decide the pre-allocation embodiment subject.
- [ ] Decide multi-world replay tape ownership.
- [ ] Decide ephemeral terminal policy.
- [ ] Decide federated group proof.
- [ ] Decide the admission-policy representation.
- [ ] Decide what `replay.verify` can claim about remote or unavailable targets.
- [ ] Decide in-flight state handling at transfer.

## [Game social systems and scale](game-social-systems-and-scale.md)

- [ ] Carry per-entity scale on the wire so remote adjacency contact is scale-consistent.
- [ ] Track 1: land the frames document shape and the angular-speed, minimum-feature-size, and mass-ratio envelope inputs, sized analytically.
- [ ] Track 2: restore canary coverage for the grant-door and guest-mutation claims that `addon-mutation-seam` held.
- [ ] Track 3: hoist neighbour-field derivation to delivery, tape per-tick neighbour records, and pin the delivered revision at tick start.
- [ ] Track 4: one seat-lifetime view state, the owner feel sitting, and the touch-triggered win slice, then navigation and equip facets.
- [ ] Track 5: lower authored `body:n` to `WorldEntityAddress` at compile or install time, and re-verify the reference-game findings before relying on them.
- [ ] Directed multi-dimensional relationships, hearsay de-duplication, and personality plasticity.
- [ ] Run the few-thousand-creature, 60 FPS acceptance workload on desktop and Steam Deck with its falsifiers.
- [ ] Widen `four-corners-sharded` to vertical and island handoffs, cross-host contact, autonomous travellers, retained dual-stick control, and derived diagonal peers.

## [Game state and rules](game-state-and-rules.md)

- [ ] A carry rule effect.
- [ ] An authored carry chord.

## [Tabletop games and composed worlds](game-tabletop-and-composed-worlds.md)

- [ ] Author `search` and board `enforcement` in the chess module for checkmate, stalemate, draws, and a CPU opponent.
- [ ] A vertex-configuration grower for the two snub tilings.
- [ ] Decide on a plane-native make/unmake for deeper search.
- [ ] Design a pair domain over rows so interactions and decisions can leave host evaluation.
- [ ] A reduction operand for permutations longer than sixteen elements.

## [Game topologies and board queries](game-topologies-and-board-queries.md)

- [ ] Decide whether the `$symmetry:` node lattice is an engine primitive or world-reshapeable content.

## [Groups and cooperative matchmaking](group-finder.md)

- [ ] Unit A: carry requested durability from ingress through persistence, receipt lookup, and typed completion, bound to canonical input and the verified actor.
- [ ] Unit B: a bodyless session principal and bounded session table with issuer-qualified group resolution.
- [ ] Unit C: invite, revoke, accept, decline, set-role, leadership-transfer, and administrative-removal transitions, with an effective roster that survives reload.
- [ ] Integrate A, B, and C through the command, wire, checkpoint, resolver, and external-operation paths.
- [ ] Settle the `OwnershipPolicy` and `SharedStateScope` contract when the relevant ownership door is implemented.
- [ ] Measure journal append, checkpoint cost, receipt-index growth, and external journal retention before choosing segmentation or retention.
- [ ] Add a bounded deletion path for `IObjectBlobStore` or use the provider lifecycle.
- [ ] Slice 3: parties and invitations through commands and UI.
- [ ] Slice 4: activity requests, the pure matcher with an exhaustive oracle, and atomic proposal claims.
- [ ] Slice 5: destination admission of a roster across several source authorities.
- [ ] Slice 6: the in-world finder experience, including backfill and play-again.
- [ ] Slice 7: release qualification against the provisional load targets.

## [Screens and machine extensions](machine-extensions.md)

- [ ] Priority 1: authored screen names with derived render slots, explicit multiport control routes, named display-free links, and held-input release on retarget.
- [ ] Priority 2: a generic machine-operation rule effect, direct predicates and addon watches on named instances, and complete work and resource admission.
- [ ] Priority 3: boot-anchored reproduction with neutral execution and content receipts and machine-state evidence.
- [ ] Priority 4: a reusable cabinet module and complete player and author UX across the corpus.
- [ ] Priority 5: a no-bricks distribution, removal of direct brick and forge references from `Puck.World`, and deletion of screen-owned lifecycle paths.
- [ ] Close the three worked-cabinet UX checks: no repetitive wiring, held control released on source switch, and distinguishable empty, failed, and stopped states.
- [ ] Qualify bundled firmware: independent image rebuilds, cold and fast handoff, AGB services, and the recorded `A.gba` render failure.
- [ ] Test server content policy across boot, insert, reload, restore, and replay, including raw ROM and relabeled-input rejection.
- [ ] Repeat the package-consumer proof against the final public contract.

## [MCP integration](mcp-integration.md)

- [ ] Milestone 2: participant composition with `puck_affordances`, `puck_observe`, and `puck_act`, binding lifecycle, and honest receipts.
- [ ] Milestone 3: `puck_doc` with live/save distinction, `puck_capture` recording and replay controls, and the `world.cost` console verb.
- [ ] Milestone 4: parity as an isolated job and approved device-ingress handles with revocation.
- [ ] Milestone 5a: verify live Entra consent, DNS/ACME, first-party and external sign-in, and deployment.
- [ ] Milestone 5b: participant authority bindings and durable delegated mutations with reauthentication and restart recovery.

## [Retail-scale cartridges](retail-scale-cartridges.md)

- [ ] Stage 1: the memory model with typed regions, records, and an indirect operand form.
- [ ] Stage 2: procedures and banked code with call-graph placement and trampolines.
- [ ] Stage 3: interrupt bodies with verified cycle budgets, replacing `raster` and the vblank queue.
- [ ] Stage 4: ROM residency, codecs, and the banked-RAM save.
- [ ] Stage 5: 16×16 multiply, divide, and 32-bit intermediates on both backends.
- [ ] Stage 6: `puck` verbs that ingest images, maps, and audio.
- [ ] Stage 7: the content library as `.puck` modules.
- [ ] Re-derive the frame-shaped ceilings in `CartridgeLimits` as each stage replaces them.
- [ ] A `for` production inside rule bodies.
- [ ] Formatter layout for bulk data.
- [ ] Diagnostics tested at depth through templates, imports, and `for`.
- [ ] Fix cubic `let`-array re-lowering and non-finite lowering of an array-valued `let` used as a vector property.

## [Shader pipelines and hybrid rendering](shader-pipeline-evolution.md)

- [ ] P1: fresh-checkout GPU regressions for feedback, reload, capture, resize, pause, step, and allocation failure on both backends.
- [ ] P2: real per-pass GPU timing behind `IPassTimingSource`, with a recorded measurement baseline.
- [ ] P3: explicit attachments, depth state, and indexed opaque geometry on both backends.
- [ ] P4: the shared opaque surface contract and a mesh-and-SDF-wall hybrid scene.
- [ ] P5: reproducible authoring and packaged dependencies.
- [ ] P6: animation, lighting, and representation experiments with measured costs.
- [ ] Release the pipeline foundation against a packaged candidate on both backends.

## [State addressing on the tick path](state-addressing.md)

- [ ] Stage 0: baseline numbers and operand distribution recorded.
- [ ] Stage 1: ordinal entrances, skip authored-cell scan without traits, pre-parsed literal keys, bound index reads.
- [ ] Stage 2: intern static cell addresses resolved per layout (profile-gated).
- [ ] Stage 3: reload only rows that changed (profile-gated).

## [State consolidation](state-consolidation.md)

- [ ] A common compiled value source for literals, operands, and expressions that keeps the recorded semantic distinctions.
- [ ] A shared effective-behavior view for slot and keyed-cell traits across readers, conversion, and rebase.
- [ ] Share the Boolean program traversal between body gates and world gates.
- [ ] Separate storage kind from participant `Counter` and `Timer` ownership in `StateValueKind`.
- [ ] One authored sort replacing the `sortZone` and `sortKeyed` frontends.
- [ ] Remove the field forwarding in `WorldStateRow` and `WorldRule`.
- [ ] Extend compilation reuse to boot, checkpoint restore, and separate hazard and budget requests.

## [World release management](world-release-management.md)

- [ ] Commit the retained release archive and the Azure release adapter and runtime driver now in progress.
- [ ] Write definition-edit preservation rules and admit the first tested edits.
- [ ] Add rollback and finalization operator commands.
- [ ] Add group-scoped restore with external-obligation refusal.
- [ ] Replace `AzureWorld`'s `world-rollback.json` fallback with the common operation workflow.
- [ ] Qualify a release pair with packaged images and record the operator exercise, including interrupted deployment and rollback.
- [ ] Publish the maintenance and recovery runbook in the deployment guide.

## [World runtime consolidation](world-runtime-consolidation.md)

- [ ] Split `WorldServer` into the `WorldDocument`, `WorldTick`, `WorldRuleHost`, `WorldExtensions`, and `WorldPersistence` facade objects, un-nesting the codec records.
- [ ] Move contribution tenure, placement deals, placement responses, and reflow review expiry onto `WorldDeadlineTable`.
- [ ] Add the comment-smell ledger.
- [ ] Lower the file-length ceiling once the facade splits the largest files.
- [ ] Pass the composed-game acceptance check: two chess instances, two solitaire tables, and poker with two participant identities.
