# Capability channels — START HERE

**Read this before any other campaign document.** The design round records were
deleted on 2026-08-02: git holds how each conclusion was reached, and the
reasoning a reader still needs was moved to the code it governs. This file is
only what is true.

## THE MAINTENANCE RULE — this file's own contract

1. **Updated in the SAME COMMIT as any landing that changes its truth.** Not
   after. A landing that leaves this file disagreeing with the tree is an
   incomplete landing.
2. **No in-flight status, ever.** No "in progress", no "just launched", no agent
   state. That stales within minutes and belongs in the ledger. This file holds
   only slow-moving truth.
3. **If it grows past two screens it has failed** — cut it rather than continue
   it.

Without rule 1 this becomes the capability catalog `CLAUDE.md` warns about: a
document consulted precisely to decide what is safe, asserting agreement it has
no mechanism to keep. Verify the tip yourself (`git rev-parse HEAD`); never
trust one quoted in prose, including this file's.

## What the campaign is

One contribution model for input, commands, addons and UI. Authority travels as
handles resolved from grants, never as names. Vocabulary is itself a capability.
Quotas are properties of a grant row.

## DECIDED — owner rulings and ratified gates. Do not re-open.

The **Landed** column is the point: a decision can be ruled and only partly
built, and reading the two together as one is the mistake this table prevents.

| Decision | Landed? | Where the reasoning lives now |
|---|---|---|
| `ActionLanes` dissolves; `PlayerIntent` is ONE channel vector declared in world data | yes | `Protocol/PlayerIntent.cs` |
| Placement is REACH, never semantics — one fold primitive at every composition site | yes | below, under **Placement**; `Server/WorldServer.FoldChannelContributions`; `Client/SeatController.HeldChannels` |
| Fold: untrusted pooled around `h`, trusted added OUTSIDE the pool | yes | `Puck.Maths.FixedContributionFold`; `Server/WorldServer.FoldChannelContributions` |
| Trusted-by-authorship: classification keys on HOST LOCUS, not principal kind alone — a document-mounted (Simulation-lane) Addon is WORLD LOGIC, trusted, reach-gated (no consent needed); Peer and a future client-hosted addon stay pooled under Reach ∧ Consent | yes — owner ruling 2026-08-02 (headless P6b) | `Server/WorldServer.StageContribution`; `Server/WorldAddonRuntime.ContributionAccepted` |
| Fold arithmetic: raw `Int64`, ONE end clamp, never a saturating add | yes | `Puck.Maths.FixedContributionFold` remarks (bound, rounding pin); `Server/WorldServer` (World contributor bound) |
| Binary non-flip bound `c ≤ min(T−1, One−T)` raw | design only | `Puck.Maths.FixedContributionFold` remarks |
| Consent rows ARE grant rows, WITH the seat-issuing narrowing | yes — `c4ee338f`, both halves in ONE commit | `Protocol/WorldGrant.cs` (`Consent`/`Ceiling`), `Server/WorldGrants.cs` (`HoldsForAdministration`) |
| The pooled ceiling is ONE number the seat owns | yes — per `(seat, channel)`, on the seat's own Drive row; a contributor row carries REACH only, and a ceiling on one is refused by name | `Server/WorldGrants.cs` (`PoolCeilings`) |
| Reach, declared, held, and consent channel masks are distinct policy values; ceiling support is intrinsic | yes | `Protocol/ChannelPolicy.cs` |
| A ceiling authored in the WORLD DOCUMENT is withheld at boot; the row still applies | yes | `Server/WorldServer.cs` — document grants apply in the constructor under `Console`, which `HoldsForAdministration` exempts, so the seat narrowing alone closed only the live verb. An occupancy test at boot would be dead code: no seat is active yet, and the hazard is the human INHERITING the pool on sitting down, so the withholding is unconditional |
| A declared mount ceiling bounds the contribution set | **NO — sized by local seats instead** | `Protocol/PlayerIntent.cs` (`ChannelLimits`) |
| Occupancy is DEFINED (a local seat slot Active AND bound to that body, OR the body bound to an admitted Peer); a co-driving Drive grant on a remote-admitted body refuses at the grant door for any principal but that body's own Peer | yes — BOTH halves; the P7 socket phase landed the live Hello→admit→disconnect handshake, so `IsAdmittedPeer` is no longer permanently `false` | `Server/WorldPopulation.IsHumanOccupied`/`IsAdmittedPeer`/`TryAdmitRemotePeer`; `Server/WorldServer.Grant`/`TryAdmitPeerConnection`/`DisconnectPeerConnection`; `Server/WorldTcpHost` (owner ruling 2026-08-02, headless P6b) |
| Contributions are per-tick and expire; host state lives on the folded OUTPUT | yes | a missed publish must read NOTHING, not stale state, and host-held edge state cannot synthesize an absent contributor's value |
| Rings 64/63, relation provable from the constants | yes — `cadb16c9` | `Puck.Scripting/AddonAbi.MaxOutCells` |
| Phase 3 may build ONLY against the verified premise set | n/a | the plan's Phase 3 decision sheet, "VERIFIED PREMISES" |
| Phase 3 L3: HUD bindings are a CLOSED v1 vocabulary (`world.tick`/`world.fps`/`seat.<n>.position.*`/`population.active`/`state.<row>`/`state.<row>.<key>`), refused by name outside it | yes | `Puck.World.Data/WorldHud.cs` (`HudBindingVocabulary`) |
| Phase 3 L3: a HUD panel's band (under/over/replace) is a DOCUMENT property (`WorldHudLayer`), never a renderer-side concern | yes | `Puck.World.Data/WorldHud.cs`; `Puck.Overlays/UnifiedOverlayNode.cs` |
| Phase 3 L3: band draw order is PINNED — under → base (the five first-party writers, or every live `replace` panel) → over | yes | `Puck.Overlays/UnifiedOverlayNode.ProduceFrame` |
| Phase 3 L3: every `WorldMutation` kind carries a DECLARED explicit ordinal, 0..63, validated for uniqueness/range at boot — never inferred from file order | yes | `Puck.World.Data/Protocol/WorldMutationKindCatalog.cs` |
| Phase 3 L1/L4: the contributed-GPU-share ceiling is DISSOLVED (authors own the frame budget); the document-global CPU/grid-bound ceiling (16000) gates `WorldDefinitionValidator` at load, refusing a document whose animated-placement + inhabited-placement-body sum exceeds it, by name | yes | `Puck.World.Data/WorldDynamicGeometryCeilings.cs`; `Puck.World.Data/WorldDefinitionValidator.cs` (`ValidatePlacements`) |
| Phase 3 L8: player-scope HUD is edited through the EXISTING Edit door (`SetPlayerSection(Hud)` gated on Edit over the concrete `profile:<id>` subject) — no new grant capability, no new mutation kind | **SUPERSEDED twice** (`ad5935ae`, 2026-08-04: the player-document family this door and grant subject belonged to was deleted, not ported — "its command module and its grant subjects were deleted rather than ported"; then 2026-08-05: the door was REBUILT in the identity family as `identity.hud <panel-json> [player]` — ungated owner-side like `identity.motion`, an owned world is the actor's own document, so the original "Edit over profile:<id>" grant shape is retired with the family, not restored) | since-deleted: `SessionRequest.SetPlayerSection`/`WorldPlayerSection.Hud`, `Puck.World.Server/WorldProfiles.cs`. Live door: `Puck.World/IdentityCommandModule.cs` (`identity.hud` — validates the composed candidate through `WorldDefinitionValidator.TryValidate`, persists via `WorldOwnedWorlds.Save`; `identity.show` echoes the panel summary, the read-back) |
| Phase 3 L8: the seat-scope panel is ONE optional `WorldHudPanel` per profile (not a list), capped at `WorldHudCapacity.MaxElementsPerSeatPanel` (12, smaller than the world scope's 24) and refusing `WorldHudLayer.Replace` (no base slot to take over inside one seat's viewport); rendered via `HudWriter.EmitSeatPanels`, confined to the seat's viewport by one clip scope (the EditorHud precedent) | **yes again** (cap/refusal enforcement was LOST by `ad5935ae` — recorded here as "shape yes, cap/refusal NO" earlier on 2026-08-05 — and RESTORED the same day): `WorldDefinitionValidator.ValidateHudCore` now takes `isIdentityScope` (`definition.Identity is not null`) and applies `MaxElementsPerSeatPanel` (12) plus the `HudRefusal.SeatPanelReplaceRefused` throw to ANY document carrying an `Identity` section — boot load, sync pull, and `identity.hud` all refuse through the one validator, not just the verb. "One panel" remains authorial convention (`WorldIdentity.Hud` reads `document.Hud.Panels.FirstOrDefault()`), not a schema limit. Verified by running: 12-element control passes, 13-element and `Replace` panels refuse by name, and a sabotage run forcing the 24-cap made the 13-element refusal assertion fail (the check is load-bearing) | `Puck.World.Data/WorldIdentity.cs` (`Hud`), `WorldDefinitionValidator.cs` (`ValidateHud`/`ValidateHudCore`, the `isIdentityScope` branch), `HudValidation.cs` (`SeatPanelReplaceRefused`, now thrown), `WorldHud.cs` (`MaxElementsPerSeatPanel`); `Puck.Overlays/HudWriter.cs` (`EmitSeatPanels` — the reservation budget stays its own, separate check) |
| The PERCEPTION ANCHOR ("possession should mean possession", context-routes ruling 2026-08-02): each seat has ONE body index ALL its seat-relative presentation derives from — camera anchor pose, seat-join cue site, spatial-audio listener (via the seat's view-camera pose), crowd soft-shadow centers, `seat.<n>.position.*` HUD bindings — swapped in ONE place, never per-system retargets. Presentation-side only, derived, never sim state; `player.where` echoes it (`anchor=body:<n>`, 0-based) for local seats | yes — the swap is LIVE: a seat whose Control route targets a BODY with capture ON perceives from that body every tick (`WorldSeatContextSync.Publish`, the same loopback read the engagement context family already does); a mirror (capture off) or a screen route leaves the seat perceiving its own bound body. The body-index-band gates (footstep cue, seats-always-cast shadow) are DECIDED as a separate policy — a possessed body is NOT reclassified in v1, it keeps casting/cueing on its own raw index band regardless of who perceives from it | `Puck.World/Client/WorldPerceptionAnchor.cs` (consumers cross-referenced in its doc comment), `Puck.World/WorldSeatContextSync.cs` (the resolving read), `Puck.World/Client/WorldSceneEmitter.cs` (the index-band policy comments) |
| Context rows (2026-08-01 design, ratified as-is): `puck.bindings.v1` grew an optional `contexts` section (`{family, state, group}` rows, strict parse); a family is admitted ONLY as the output of a single per-seat single-valued state machine — `roster` (unjoined/claimed/pending/active) and `engagement` (engaged/none, a READ over the grant table's Control route, the CheckEngage loopback discipline), "editor-ness" explicitly rejected; derivation is context row → seat-requested group → profile default, across-family precedence is authored row order, shadowing reported; `player.bindings` leads with the derivation echo | yes — SEAT SIDE (2026-08-02); the `player.south`-dissolving default-document rows are the input-model arc's remaining half | `docs/reviews/2026-08-01-context-rows-design.md`; `Puck.Commands/BindingContextDefinition.cs`, `BindingProfile.cs`; `Puck.World/WorldContextFamilies.cs`, `WorldSeatBindings.cs`, `WorldSeatContextSync.cs` |
| Routes are channel-masked taps on the one intent wire `(target, capture, channelMask)` — a screen (today's machine engagement) OR a body (possession/co-drive) target, single-valued per principal at the grant table exactly as before; capture decides whether the source body idles (true, today's behavior) or mirrors (false — the source keeps integrating while the route also reaches its target) | yes — owner ruling 2026-08-02 (context-routes) | `Server/WorldGrants.cs` (route storage: `PrincipalGrants.m_routeMirror`/`m_routeChannelMask`), `Server/WorldEngagement.cs`, `Data/Protocol/WorldCommand.cs` (`Engage`) |
| A body-target route's per-tick channel passthrough is a co-drive CONTRIBUTION queued through the SAME `StageContribution` path an addon/co-driving seat already uses (possession = Drive-over-body + the route, never a second authority path); it is NEVER taped directly — RE-DERIVED at replay like an addon's driving, from the taped `Engage` command plus the source seat's own taped submissions | yes | `Server/WorldEngagement.cs` class remarks (replay visibility), `Server/WorldServer.Step` |
| A whole-document rebuild (`world.reset`/`world.load`/`world.reload`, owner ruling 2026-08-02) drops EVERY runtime grant and re-seeds the permissive local-play defaults plus the new document's own `Grants` section, exactly as boot does ("runtime grants otherwise drop; document grants re-apply as at boot"); admitted PEER CONNECTIONS are the one exception — their admission grant re-mints from the connection table afterward, same as admission itself mints it | yes | `Server/WorldGrants.Reset`; `Server/WorldServer.ApplyRebuild`/`RemintPeerAdmissionGrants` |
| Blank-slate lane B: the `state` section (score/rounds/inventory/flags) is genre-neutral DATA, never protocol shape — named typed rows (int/fixed/bool/text; never float) mutate through the SAME six-stage door every section rides (`Mutate`/`section:state`); `Edit` over the CONCRETE `state:<name>` subject (or `all`) is a SECOND, row-scoped check beneath that coarse hold — narrower authority than any other section, the campaign's "concrete rows" ruling | yes — owner-ratified blank-slate campaign, lane B | `Puck.World.Data/WorldState.cs`; `Puck.World.Server/WorldServer.cs` (`TryApplyMutation`'s second Edit check) |
| Blank-slate senses lane: world EVENTS ride the EXISTING Observation cell kind as data, never a new export/host import; family = `GrantSubject` KIND under `Observe` (`Body`→collision+route, `Region`→enter/exit, `Seat`→join/leave, `Screen`→machine-memory), gated by `requested ∧ granted ∧ EventBudget` (a SIBLING of `Budget`, stacking with the pre-existing untrusted-Observe budget requirement, never replacing it); overflow is ordered-prefix drop-newest with a per-mount saturating gap counter (`ObservationVerbs.EventGap`) — owner-ratified, not a design choice made here | yes | `Puck.Scripting/AddonAbi.ObservationVerbs`; `Server/WorldEventFeed.cs`; `Server/WorldAddonRuntime.EmitEvents`; `Data/Protocol/WorldGrant.cs` (`EventBudget`) |
| Blank-slate lane C1: sprint is a NEW held channel (`WorldMotionModel.Grounded.SprintChannel`/`SprintMultiplier`), never `dash` — `dash` already binds `ActionSpec.Dash` (an edge-triggered planar impulse) in every shipped kit that names it, so doubling it onto a continuous held read would leave two meanings on one channel | yes | `Puck.World.Data/WorldDefinition.cs` (`WorldMotionModel.Grounded`, `FixedWorldKit.SprintChannelOrdinal`); `Puck.World.Server/WorldBody.ComputePlanarTargetVelocity` |
| Blank-slate lane C1: camera-relative move resolves AT THE SEAT, client-side, before submission — `MotionMoveFrame.World` never puts a camera pose in the sim; the client rotates the raw stick sample by the seat's rendered chase-camera yaw into already-world-frame `MoveForward`/`MoveStrafe`, and `WorldBody` just stops rotating those axes by the body's own heading. `FacingSnap` is a body-side-only effect (`Atan2` of the commanded direction, no ramp) so it needs no client cooperation. `Heading` (tank controls) stays the per-kit default | yes | `Puck.World/Client/WorldClient.ComposeMoveFrame`; `Puck.World.Server/WorldBody.ResolveYawAttitudeAndPlanarFrame`/`SnapYawToPlanarIntent` |
| Blank-slate lane C1: `WorldScreenRoute.EngageChannel` is CONSUMED — a rising edge on a body within `EngageRadius` of an engageable, un-engaged screen naming that channel is intercepted SERVER-SIDE into an ordinary `WorldEngagement.Engage`, pre-empting the body's own action track the SAME tick (so the shared button never also fires a jump). Pure re-derivation from tick-local sim state (pre-move position, channel bits, document/grant state) — never a taped command, replay reproduces it by re-execution alone, the SAME shape `WorldEngagement`'s body-route contributions already established. **Eligibility signal updated (authoritative-machines campaign, 2026-08-03):** was the document-declared `WorldScreenSource.Machine` (a proxy — the server could not see a real boot); now `Server.WorldMachineHost.HasMachine`, the honest live signal, since the host boots and steps the machine server-side | yes | `Puck.World.Server/WorldServer.ResolveEngageProbes`/`Step`; `WorldBody.Advance`'s `engageProbeOrdinal` |
| **Machines are server-authoritative (owner ruling, 2026-08-03): a booted `IScreenMachine` is CORE state, not presentation-fed.** `Server.WorldMachineHost` (a peer DI singleton, constructed in EVERY boot shape) owns boot/step/link/reconfigure/memory-peek for every declared screen's machine; stepping happens inside `WorldServer.Step`, fed directly from `WorldEngagement.FoldTick`'s per-screen pads (no client/wire round-trip — the former `WorldSnapshot.EngagedPads` lane is deleted, `WorldScreenBinder` was its only reader). `screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink` submit a `WorldScreenOp` through the ordered submission domain (`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY (like `Command`/`Grant`/`Revoke`, never buffered) and tape-covered (`WorldReplayEntry.ScreenOp`, discriminant 7, magic re-keyed `PKRG → PKRX`). **CORRECTED (2026-08-03 adversarial review, 4 findings — see the ledger's dated entry for the full accounting; do not trust the ORIGINAL landing's own claims below this sentence over the code):** `Select` DOES need — and now carries — the identical CAS pin `Insert` does (a magazine entry's document-declared path is not immune to on-disk drift either); both thread a content SIGNATURE (a real `sha256-64` hash, or `WorldMachineHost.ContentAbsentSignature` when the file could not be read at all) onto the tape REGARDLESS of whether the op succeeded, so a FAILED insert/select reproduces the identical failure on replay, or refuses BY NAME (`ScreenOpContentMismatch`) the moment the file's on-disk state has since changed in EITHER direction (appeared, vanished, or changed) — the original landing gated the tape write on success, which let a failed insert replay as a silently-unpinned live retry. `ReconcileLinks` was DEAD CODE in the original landing (never called from anywhere); `WorldServer.Install` now calls it right after `ReconcileScreens` on every mutation AND rebuild, and the constructor calls it once at boot for a document-declared `links` row. `WorldMachineHost.ReconcileScreens` (and `WorldScreenBinder.ReconcileScreens`, its presentation-side mirror) now RECREATE a slot for a re-declared index instead of permanently forgetting one removed by an earlier mutation — a `RemoveScreen` followed by `world.reset` used to leave the definition claiming a cabinet the host could never rebuild. `replay.record`'s boot-anchored arm gate (`WorldServer.AnyMachineEverPumped`, mirroring `AnyAddonEverPumped` exactly) now refuses once ANY machine has stepped, not merely once an addon has pumped — offline replay's fresh `WorldMachineHost` can reconstruct a BOOT image but never a machine's accumulated core state, and the pose hash covers no machine state to catch that divergence after the fact (record-start rehydration, the real lift, stays on the settle docket; hash coverage stays poses-only, deliberately, per the review's own instruction not to add a machine digest in this pass). `WorldScreenBinder` is now a PURE READER of the host's outputs (framebuffer handle/light, `IScreenMachine.PublishFrame` — the one GPU call this project still makes on a machine's behalf) plus several read-only facades so existing callers (`PlayerCommandModule`, `WorldAudioDirector`, `WorldSessionCapture`'s `world.save` fold) needed no call-site churn. Camera/capture/window-capture/jumbotron-view/test-pattern screen sources are UNCHANGED — genuinely presentation, never the ruling's subject. Pose-hash coverage is UNCHANGED (machines move no bodies) AND STATED PLAINLY: a `replay.verify` MATCH proves the pose trajectory only — a screen op's own CAS refusal is reported independently, by name, never folded into the hash. **SECOND CORRECTION (2026-08-03, round-two adversarial re-review, 4 MORE findings — the ledger's second dated entry has the full accounting; the round-one correction's own `ReconcileLinks`/arm-gate claims above are now ALSO superseded by this paragraph, not just the ORIGINAL landing's):** `ReconcileLinks` established each declared row BEFORE sweeping stale ones, so a re-shape (e.g. `A=[0,1],B=[2,3]` → only `B=[0,1]`) tried to establish B's new membership while stale-but-not-yet-swept A still owned screens 0/1, silently failing (the result was discarded) and leaving B on its OLD members — now two-phase: every stale-or-changed declared link tears down FIRST, complete, before anything (re-)establishes, and a genuine establishment failure (two declared rows racing for one screen) prints loudly instead of being discarded. `TryBootMachine` resolved the engine BEFORE reading content, so an engine-resolution failure returned `ContentHash: null` — fully UNPINNED on replay (null reads as "the live path") — now content is read/signed FIRST, regardless of engine-resolution outcome, so that failure path is CAS-pinned exactly like every other one. The boot-anchored arm gate covered addon pumps and machine steps but not a screen op itself: `screen.insert` immediately followed by `replay.record`, with zero ticks elapsed, used to arm clean (nothing had STEPPED yet) even though the insert already changed live `WorldMachineHost` state the record-start definition snapshot cannot capture (a screen op is not a document mutation) — `WorldServer.AnyScreenOpEverApplied` (latched the instant any op changes host state, mirroring `AnyEverPumped`'s own shape) closes this, refusing to arm once ANY screen op has applied this session, independent of whether anything has stepped. `WorldScreenBinder`'s FINDING-4 slot-recreate fix (round one) wrote a NEW `slot.Handle`/`slot.Light` delegate into `m_sources`/`m_lights` on every re-declare, but `SdfEngineNode` copies those two dictionaries' delegates ONCE, at construction, and never reads this binder's dictionaries again — a post-boot delegate was therefore invisible to the renderer, which kept polling the OLD, disposed slot forever (rendering the procedural fallback after any remove+reset, permanently). Fixed with one more layer of indirection: `ScreenSourceCell.ResolveHandle`/`ResolveLight` are the STABLE, never-replaced delegate identities registered at boot; only the cell's own `Slot` field is reassigned on recreate, which the renderer's one-time-copied delegate observes on its very next poll with no renderer-side change at all. | yes | `Puck.World.Server/WorldMachineHost.cs` (`ReconcileLinks`, `TryBootMachine`'s read-before-resolve order), `WorldServer.cs` (`AnyScreenOpEverApplied`, `TryApplyScreenOp`'s latch), `WorldReplayTape.cs` (the third arm-gate check), `WorldReplaySnapshot.cs` (doc correction only); `Puck.World/WorldScreenBinder.cs` (`ScreenSourceCell`, `m_sourceCells`) |
| **ONE engine EDGE vocabulary (owner ruling, 2026-08-05):** `ActionTriggerMode` (`Level`/`Edge`) serves BOTH per-body fact triggers and world rules — "fires while the condition holds" vs. "fires once on the crossing" is the same distinction at both scopes, so it is the same enum, never two spellings. `ActionFactTrigger` also gained a `Gate` (it was the one trigger channel in the engine that could not narrow). `ActionTrigger.LatchSeconds` 0 now means THIS TICK ONLY as it always documented (the fire condition demanded a strictly positive latch, which made 0 structurally dead), and a non-zero value on `onRelease` is REFUSED by name rather than parsed and discarded | yes | `Puck.World.Data/WorldDefinition.cs` (`ActionTriggerMode`, `ActionFactTrigger`, `ActionTrigger.LatchSeconds`); `Puck.World.Server/WorldBody.ProcessLaneActions` (`LaneActionRuntime.FactHeld`) |
| **The GENERATOR is a state row, not a section (owner ruling, 2026-08-05):** a `WorldStateRow` may declare a `WorldGenerator` — weighted alternatives per context, each naming the context it moves INTO (which is what makes it a Markov process rather than independent draws), an emission bound that refuses by name rather than truncating, and an authored deck mode (`withReplacement`/`withoutReplacement`/`reshuffleOnExhaustion`). Cursor and deck state are DOCUMENT CELLS on that row (`$cursor`, `$deck<n>`), so journal/undo/save cover a draw for free and `world.undo` rewinds a generator's position bit-identically. ONE new mutation kind (`Generate`, ordinal 51, section `State`); authority is the EXISTING pair — `Mutate`/`section:state` plus row-scoped `Edit`/`state:<row>` over the row it WRITES — with the existing `MutationKindMask` supplying fire-without-redefine (`verbs:Generate` fires but cannot re-author). NO new grant vocabulary, NO new subject kind, NO required section. Sampling is `Puck.Maths.WeightedSampler` alias tables over `Pcg32XshRr` with SMALL CONSECUTIVE stream ids derived from generator-row declaration order (never a hash-derived 63-bit id); an authored `seed` moves the starting state instead. The retired `WorldDraw`/`WorldSet`/`WorldDrawRuntime`/`world.draw` mechanism is DELETED — a flat weighted draw is a degenerate one-context generator | **SUPERSEDED 2026-08-06 by the row below** — the source family, the seed ladder, the cursor's home, and the `world.generate` grammar all moved; what survives is the shape of the idea (a Markov table with an authored deck, cursor state in the document, ordinal 51, no new grant vocabulary). Read the next row, not this one, for what the code does | `Puck.World.Data/WorldState.cs`; `Puck.World.Server/WorldServer.TryComposeGenerate` |
| **Authored randomness is ONE primitive: SOURCE x SITE x MOMENT (owner ruling, 2026-08-06).** A `WorldGenerator` is a stochastic SOURCE — `markov` (a weighted transition walk over contexts, the only shape that writes TEXT and the only one that deals), `uniformRange`, `weightedNumeric`, `streamDraw` — and nothing else in the document produces randomness. A `WorldDraw` is a SITE facet declaring that a value is drawn; it either NAMES a source declared in the new optional `generators` section (`"source": "<name>"`) or INLINES one (`"generator": {...}`), the two spellings compiling to the identical record so nothing is reachable one way and not the other. `WorldDrawTiming` (`boot`/`tickPeriod`/`event`) is the MOMENT, and it rides the EXISTING `Generate` mutation (ordinal 51) and boot resolution — ZERO new mutation ordinals, the catalog stays 64/64. Three sites today: a `WorldStateRow.Draw`, `population.capacityDraw`, and `host.backendDraw`. **A source holds no position — the SITE does** (`WorldStateRow.DrawCursor`/`DrawDecks`), which is what lets two sites reference one table and draw INDEPENDENT sequences; that independence is what makes references safe. The seed ladder is FOUR rungs, each length-delimited before its bytes: engine constant, `generation.worldSeed`, running INSTANCE identity, SITE DESCRIPTOR — an identity, never a positional ordinal, because the live site set moves under ordinary operation (a settled facet clears, `world.row.remove state` retires a row, `UpsertStateRow` adds one) and a positional stream would silently re-point a live site while its cursor kept counting. **The engine SEEKS, it does not replay**: every source costs a fixed number of generator advances per sample (the uniform range is a multiply-high map, deliberately uniform-to-within-`n/2^32` rather than rejection-sampled), so resuming at cursor `n` is one `Advance(n * cost)` and there is NO per-tick cadence ceiling. Boot-only sites SETTLE AND CLEAR into their ordinary literal field and NARRATE the settlement on stderr (settling erases the only evidence the value was random); state sites persist facet + cursor, so save/reload resumes rather than re-rolls. The host backend draws BY NAME from a weighted TEXT source over the backend tokens, parsed at settle — never an unnamed ordinal, which would re-point itself the day an enum member is inserted. Draw domains are narrowed STATICALLY against the site's own envelope (and every reachable backend token is checked), so a roll can never decide whether the world boots. `WorldStateRow.Generator` and the `$cursor`/`$deck<n>` reserved CELLS are DELETED — bookkeeping is typed row fields at the site. **Competition disposition:** two prototypes (branches `draw-a`, `draw-b`) were built, refuted, and held; NEITHER ships. This is their directed union — `draw-a`'s seekable engine and widened source family, `draw-b`'s site facet, site-identity-derived streams, envelope-aware domains, settle-and-clear narration and per-site cursors. Both branches stay preserved | yes — verified by RUNNING the game (54-case refusal matrix with an admitting control, dual-boot identity, world-seed and instance-rung divergence reproducible across process re-runs, live-mutation stream stability, save/reload resume, undo rewind, 240 Hz per-tick redraw at cursor 1,000,000) | `Puck.World.Data/WorldGeneratorEngine.cs` (the one sampling core + seed ladder + reference resolution), `WorldDraw.cs` (facet, timing, site vocabulary), `WorldState.cs` (`WorldGeneratorSource`, `WorldGenerator`, `WorldGeneratorRow`, `WorldStateRow.Draw`/`DrawCursor`/`DrawDecks`), `WorldDefinitionValidator.cs` (`ValidateGenerators`/`ValidateSource`/`ValidateDrawSite`/`ValidateDraw`); `Puck.World/WorldDrawBootResolver.cs`; `Puck.World.Server/WorldServer.TryComposeGenerate` |
| **Reserved `$` names are ENGINE-MINTED ONLY — the KEY *and* the VALUE (amended 2026-08-05):** an authored state ROW name carrying the `$` prefix is refused outright (nothing mints a row — which is also what keeps `$tick`/`$population`/`$region:` from ever being shadowed), and so is a `$`-prefixed RULE name. A `$`-prefixed CELL key is refused unless it is exactly the key that row's shape mints (`$value` on a slot, `$cursor`/`$deck<n>` on a generator) **carrying a value the engine could have minted there**: `$cursor` is a non-negative sample count (a negative one seeks to a stream position no draw could reach — and refusing it is NOT blessing a seek verb; there is none), a `$deck<n>` names a declared context, exists only under a non-`withReplacement` mode, and deals no bit past its context's alternative count. Key-only was the original rule and it let three mint-impossible values through under keys the engine does mint. The rule is stated ONCE in `WorldGeneratorCells.TryValidateReservedCell` and called from BOTH the validator (boot, every live mutation, every undo-replay entry) and the `UpsertStateCell` compose arm, so the verb names the refusal at the verb and the two can never drift | yes | `Puck.World.Data/WorldState.cs` (`WorldGeneratorCells`), `WorldDefinitionValidator.cs` (`ValidateState`), `Puck.World.Server/WorldServer.cs` (`TryCompose`'s `UpsertStateCell` arm) |
| **A `document:` principal holds NO live grant row (2026-08-05):** its capability is real but it is resolved by reading the OWNER IDENTITY's own document (`WorldOwnedWorlds.Decide`/`TryReadDurableState`), never the runtime table and never the visited world's own `grants`. Both document-`grants` replays (the constructor's and the rebuild's) therefore SKIP a `document:` row, and the grant door refuses one by name — a live row would be budget-less, mask-less, and read by nothing, which is the phantom shape the table's key discipline exists to prevent. `world.grants` echoes the document-authored rows in their own `[world.grants.document: …]` group, so the skip is not a disappearance | yes | `Puck.World.Server/WorldServer.cs` (`IsDocumentChannelRow`), `WorldGrants.Conflicts` rule (-1b), `Puck.World/WorldGrantCommandModule.cs` (`DescribeDocumentRows`) |
| **UNTRUSTED principals are REFUSED `Mutate`/`section:rules` (OWNER RULING, 2026-08-05 — a named narrowing):** a rule's EFFECTS act as `WorldPrincipal.World`, which `TryAdmitMutation` admits STRUCTURALLY, before the table is consulted and with no budget charged. So an untrusted rules-authoring row launders every budget and verb mask it carries through ONE gated act, and a verb mask cannot close it (the mask bounds what the ROW dispatches; the rule's effects are not dispatched by the row). Refused at the grant door beside the maskless-untrusted refusal, with that reason in the refusal text. Trusted principals (Console/Seat) are unaffected — they hold Mutate over every section at seed and could grant themselves anything regardless | yes | `Puck.World.Server/WorldGrants.cs` (`Conflicts` rule (0c-3b)) |
| **World RULES reuse the per-body action primitive, and `WorldPrincipal.World` is a real principal (owner ruling, 2026-08-05):** the optional `rules` section carries `WorldRule(Name, Gate, Effects, Mode)` over the SAME `ActionPredicate`/`ActionEffect`/`ActionTriggerMode` types a kit uses, with the world-inadmissible subset refused BY NAME at compile (`now`/`recently`/`timerElapsed` read per-body facts; the velocity/impulse/designate/timer effects address a body). Effects address a (row, KEY) pair — `WorldStateRow.IsKeyed` is the discriminator for an omitted key, never "declares no capacity" and never `!IsSlot` (a row with no cells at all is slot-addressable: the first write mints its slot cell) — so rules reach keyed rows. That predicate was hand-written at four sites and is now stated once on the row (2026-08-06), beside a single shared read seam: `WorldDefinitionRows.FindStateRow` (the one row-find) and `WorldStateReader.TryRead(definition, rowName, key, tick, out row, out rawValue, out text)` (the one (row, key) → raw-value read), which the rule gate/copy operand, the rule effect's read-modify-write, the `world.state` read-backs, the HUD `state.<row>[.<key>]` binding and the `UpsertStateCell` Add compose arm all route through. Its `tick` parameter WAS carried unconsumed as a hook; the `WorldStateAdvance` trait landed on it 2026-08-06 (its own row below) and it is now what an advancing row's value is computed at, which is also why the reader returns a raw value rather than a `WorldStateCell`. The whole-document validator (name-keyed map) and the durable identity-document reads (`WorldIdentity`/`WorldOwnedWorlds`, which have no server tick) deliberately stay off the seam. Reserved comparison channels: `$tick`, `$population`, `$region:<placementId>`. A rule's effects, AND a kit's own `generate` effect, act as `WorldPrincipal.World`, which `TryAdmitMutation` exempts STRUCTURALLY on the principal kind (there is no bypass parameter; nothing else may exempt) — the same standing a per-body `ActionEffect` always had, now named so `world.why world …` can answer for it. The World principal holds no grants: the grant door refuses a row for it by name, the DOCUMENT VALIDATOR refuses an authored one (amended 2026-08-05 — it used to admit a row the table then refused on every boot, so a document validated against itself), and the wire refuses it both as an actor and as a nested grant row, with a message per side rather than one that claimed an off-process submitter either way. A rule's `Name` is a `WorldCellName` (dot-free, reserved-character-free, `$` refused at compile) rather than a bare string — `$weird` was accepted, evaluated and persisted. Rules evaluate in DOCUMENT ORDER and their effects apply IMMEDIATELY, so a LATER rule's gate reads an EARLIER rule's same-tick write (deterministic in document order; measured as a permanent one-count separation between a rule declared before the writer and one declared after it) — and, since 2026-08-06, so does a later effect's LIVE COPY OPERAND, which reads through the identical walk (measured on the discriminating pair: the copier declared before the writer reads the pre-write value, after it the post-write value, identically across repeated runs). BOTH SIDES OF A RULE NOW CARRY THE SAME ONE-SPELLING-OR-THE-OTHER DUALITY, and one operand walk serves them: a `compareState` comparand is `value` XOR `comparandState`/`comparandKey` (`2442b160`), and a `setState`/`addState` write source is `value` XOR `fromState`/`fromKey` (2026-08-06) — never both, never neither, kinds must match, refused `ComparandAmbiguous`/`ComparandKindMismatch` and `EffectSourceAmbiguous`/`EffectSourceKindMismatch`. Omitting `value` used to default it to 0 and install a wrong rule silently; it now refuses. A live copy is read fresh EVERY firing and converted to the destination row's own encoding by exact shift (Int/Bool) or verbatim raw bits (Fixed) — never a float round-trip, so it carries values the LITERAL spelling cannot (a `float` `value` of 16777217 compiles to 16777216, and the copy operand is how an author writes the exact number). This is what a decoupled shadow row needs: a rule reacting to a counter someone else advances resyncs with `setState roundReflect fromState=round`, where a standing `+= 1` silently desyncs and wedges the gate open the first time the counter moves by anything else (measured both ways). Both spellings are refused at BODY scope. An INT state cell is now refused outside FixedQ4816's integer band at the document validator: every engine read of one lifts it to fixed point, and an out-of-band cell used to kill the process with an unhandled exception the first tick any rule read the row. `world.rules`' latch column reads `latch=held\|open`, the key its values were always describing. Ordinals 52/53 (`UpsertWorldRule`/`RemoveWorldRule`, section `Rules`); untrusted principals are refused `Mutate`/`section:rules` outright (its own row above) | yes | `Puck.World.Data/WorldRules.cs`; `Puck.World.Data/Protocol/WorldPrincipal.cs` (`World`); `Puck.World.Data/WorldDefinitionValidator.cs` (`ValidateGrants`); `Puck.World.Server/WorldServer.EvaluateWorldRules`/`TryAdmitMutation` |
| **World rules gain a HUD/placement effect and a machine-memory fact (owner-authorized build-as-you-go, 2026-08-05 — the arcade addon port):** `ActionEffect` gains four world-scope-only cases — `upsertHudPanel`/`removeHudPanel`/`upsertPlacement`/`removePlacement` — each admitting an EXISTING `WorldMutation` kind (`UpsertHudPanel` 41, `RemoveHudPanel` 42, `UpsertPlacement` 19, `RemovePlacement` 20) into the rule effect set through the SAME seam `generate` proved: the compiled effect submits the mutation under `WorldPrincipal.World`, so `TryAdmitMutation` admits it structurally and the row's own content (capacity, unresolved `creationId`, unknown binding) is the ordinary whole-document revalidation every submission of that kind already passes through, console/addon/rule alike. Refused at BODY scope by name (a per-body action has no world document row of its own). A FOURTH reserved `CompareState` channel, `$machine:<screen>:<address>`, reads ONE live byte off a declared screen's booted machine via the SAME `IWorldMachineMemoryPeek.TryPeek` primitive `WorldAddonMemoryWatch` already rides (`WorldServer.Machines`, called directly from `RuleGateOpen` instead of accumulated as a change event) — no machine booted reads as 0, never a hard refusal. Discovered while porting: a `$region:<placementId>`/`$machine:` gate resolves against the document at EVERY `Install` (not just authoring time), so a rule can only sense a region that ALREADY exists, and that region's placement can never be removed while any rule (including itself) still names it — the retired `arcade.world.json`'s `prize-pickup` rule sensed a permanent `prize-pad` placement rather than the spawned/removed `prize` token itself, for exactly this reason (no shipped world authors `rules` today; the four-world charter, 2026-08-06, retired `arcade` with no replacement worked example). No new mutation kind, no new admission door, no new principal | yes | `Puck.World.Data/WorldDefinition.cs` (`ActionEffect.UpsertHudPanel`/`RemoveHudPanel`/`UpsertPlacement`/`RemovePlacement`); `Puck.World.Data/WorldRules.cs` (`WorldRuleFacts.MachinePrefix`, `WorldRuleFactKind.MachineMemory`, `WorldRuleCompiler`); `Puck.World.Server/WorldServer.RuleGateOpen`/`FireWorldRuleEffect` |
| Blank-slate lane C1: `WorldScreenRoute.CycleChannel` stays UNCONSUMED — closing the shared jump/engage button was this lane's scope; the cabinet cycle UX is lane C2's decision | deliberately not landed | `Puck.World.Data/WorldDefinition.cs` (`WorldScreenRoute.CycleChannel`) |
| A placement's ATTACH facet (`WorldPlacementAttach`) binds the row's pose to a live population body — 0-based `BodyIndex` (the `WorldAnchor.Entity`/`body:<n>` indexing, never the 1-based `player.*` seat number) plus a local offset rotated into the body's own frame. It derives TWICE off the one authored facet, and that split is the design: the AUTHORITATIVE pose is fixed point (`body.FixedPosition + body.FixedOrientation.Rotate(localOffset)`, `WorldPlacementAttachment.TryResolve`, called on demand by `world.attachments` — its only caller), while the RENDERED pose is presentation float over the client's INTERPOLATED body pose, packed every frame by the reserved stamp pool, so an attached row is as smooth as the body it rides. An attached row draws through that pool and NEVER as a static stamp (`WorldPlacementStamper.IsStaticStamp` is the one fork, so it cannot double-draw), charges `MaxStampRegistrations` alongside animated rows, and its authored `Position`/`YawDegrees` go inert. `Region`, `Solid` (under the analytic contact provider), and `Emission` no longer refuse alongside `Attach` — each was taught to read the resolved DYNAMIC pose instead of the row's static transform (`WorldEventFeed.CollectRegions`, `WorldColliderSet.RefreshAttached` [once per tick, before any body advances], `WorldStampPool.TryShapePosition`/`RootPose`), so an equipped item's aura/hitbox/voice tracks its carrier, and an inactive carrier makes the facet contribute/sense/sound nothing rather than at a stale point. `Distribution`/`Mirror` (static-stamp-only) and `Inhabit` (a row cannot both spawn its own bodies and ride another's) still refuse BY NAME rather than define a blend, and `Solid` still refuses under the FIELD contact provider (`collision.requirements` non-empty — it compiles every solid row's geometry once into one SDF program, never rebuilt per tick); `FaceSources` (a content selector) rides an attached row freely, as it always did. An out-of-range `BodyIndex` refuses at author time; a valid but inactive/despawned body makes the row contribute nothing at runtime (`world.attachments` echoes the reason, the stamp parks below the floor), never a refusal | yes | `Puck.World.Data/WorldDefinition.cs` (`WorldPlacementAttach`, `WorldPlacement.Attach`); `WorldDefinitionValidator.cs` (`ValidatePlacements`); `Puck.World.Server/WorldPlacementAttachment.cs` (`TryResolve`); `Puck.World.Server/WorldEventFeed.cs` (`CollectRegions`); `Puck.World.Server/WorldColliderSet.cs` (`RefreshAttached`); `Puck.World.Server/WorldPopulation.cs` (`AdvanceSimulated`); `Puck.World.Server/WorldAddonMutationDecoder.cs` (`DecodeAttach`); `Puck.World/Client/WorldStampPool.cs` + `WorldPlacementStamper.cs` (the render root); `Puck.World/WorldPlacementCommandModule.cs` (`world.attachments`) |
| **HUD templating rides `WorldHudElement`, not a new mutation kind (2026-08-06):** a `Text` element's new `Template` string interleaves literal text with `{token}` placeholders — the SAME closed `HudBindingVocabulary` a `Binding` speaks, resolved through the SAME `IHudBindingResolver` (the `2442b160` one-operand-path discipline: a token never means two things across a reader). `Binding`+`Template` together refuse (`hud.TemplateBindingConflict`); a malformed `{{`/`}}` brace/escape sequence refuses (`hud.MalformedTemplate`); an unknown placeholder — outside the vocabulary, or a `state.*` token naming an undeclared row/cell — refuses BY NAME (`hud.UnknownTemplatePlaceholder`), the SAME existence check a plain `Binding` already got. Rides the EXISTING `UpsertHudPanel`/`UpsertHudElement` mutations (an optional nested field, null-default, no seven-world sweep, no capacity-constant change — a templated element still costs exactly `TextElementCost`/`TextWordCost`). `world.hud.template <text...>` (new Immediate verb) resolves an AD HOC template against the live document with no document row at all, validated the same way before anything resolves. A keyed `state` text row is the reused table primitive underneath both a generator's target and a template's `state.*` reads — no separate "table" schema exists or was added; one was retired once already (`GrantSubjectKind.Table`) and a second would repeat it. `HudTemplate.TryParse` is the ONE parser: `Puck.Overlays` cannot reference `Puck.World.Data`, so `WorldHudFeed` parses on the structure rebuild and the writer receives PRE-PARSED runs (`OverlayHudTemplateSegment`) — the `OverlayChannelLeases` precedent mirrors CONSTANTS, which drift visibly, and was deliberately NOT extended to a grammar. Removing a `state` row a live template names REFUSES under whole-document revalidation, matching the plain-`Binding` behavior. `TextWordCost`'s render-side twin is now ENFORCED (`HudWriter.TextRunChars` as a `WriteText` `maxChars` clamp, which `OverlayChannelLeases.HudTextWordCost` reads rather than restates): it was assumed before, and a measured 13 elements × 1024 resolved chars over-ran the 9216-word Hud reservation and dropped element records | yes | `Puck.World.Data/WorldHud.cs` (`WorldHudElement.Template`, `HudTemplate`); `HudValidation.cs`; `Puck.Overlays/HudStore.cs`/`HudWriter.cs`/`OverlayChannels.cs`; `Puck.World/WorldHudFeed.cs`, `WorldHudCommandModule.cs` |
| **Two owner rulings ratified (2026-08-06):** (1) **a rule READ operand refuses an undeclared cell at compile** (`WorldRuleRefusal.StateCellUndeclared`) — a gate subject, `comparandState`, or `fromState` addressing a cell its declared row does not carry would read 0 forever with no refusal anywhere (silently broken gating), so it refuses when the rule installs; the mint-later pattern declares the cell first (an authored 0 is fine). WRITE destinations (`setState`/`addState` targets, `generate` destinations) mint their cells and stay exempt. Rules recompile under whole-document revalidation, so removing a cell a rule reads refuses the removal, naming the rule — the cell-grain sibling of the row-removal refusal. (2) **Envelope duality:** a COMPUTED/read-side value CLAMPS to its envelope (it has no submitter to refuse and no candidate to reject — an accumulating value that fills to `Max` stops), while an EXPLICIT WRITE REFUSES by name (it has an author to tell — a resource spend that would go negative refuses rather than clamping). The duality is doctrine: the next continuous-value mechanism inherits it settled rather than re-litigating. WORKED EXAMPLE + CONSIDERED-AND-REFUSED (combat forcing function, owner ruling 2026-08-06): HP is driven by EXPLICIT WRITES (`addState` damage), landing on the REFUSE side, not the clamp side — a `NonNegative` HP would REFUSE an overkill killing blow rather than saturate to zero, silently failing a normal mechanic. The ratified idiom is therefore SIGNED (unclamped) state gated at a THRESHOLD (`hp<=0` = death), NOT a `NonNegative` value. A SATURATE-on-write floor (a write that stores AT the floor instead of refusing) was CONSIDERED and REFUSED: it adds no expressive power — signed state already DERIVES saturation (a display clamp, or an authored `hp<0` → `setState 0` Level rule, itself visible/journaled/refusable) — while costing a third, silent write semantics that can only narrow expression. "Our whole unification and collapse philosophy prefers to leave ergonomics-only features out" (owner) — the ergonomics gate: projection is derivable from information, never the reverse, and a saturated store can never recover the overkill magnitude signed HP keeps. **The write side no longer SUBMITS an inert write (2026-08-06, no change to the duality itself):** a rule's `setState`/`addState` was submitted whenever its resolved value differed arithmetically from the cell, so a `Level` rule pointed at a row already sitting ON its declared bound composed a candidate the whole-document validator refused ONCE PER TICK, forever — measured on the default world at 200 applied writes draining a `nonNegative` row to its floor, then 2679 `[world.mutation rejected: … value -5 is negative]` lines over the remaining 2679 ticks of a 12-second boot, exactly 1:1 with ticks (there is no multiplier; the flood rate IS the tick rate). `WorldServer.FireWorldRuleEffect`'s existing could-this-move skip now asks the destination row's own envelope (`WorldStateRow.ClampToEnvelope`, the SAME projection `WorldStateAdvance.ComputeCurrentValue` already applied for the read clamp, now stated once) instead of arithmetic alone. This is NOT the refused saturate-on-write: nothing is stored at the floor, the submitted mutation still carries the rule's own unclamped operand, and a write that genuinely CROSSES a bound (a cell at 3 taking `-5`) is still submitted and still refused BY NAME (`value -2 is negative`, measured). Journal, trajectory and the console `world.state.cell.set` path are bit-unchanged (measured: same `895` read-back, same 200 applied writes, dual-run byte-identical over 5004 mutation lines) | yes | `Puck.World.Data/WorldRules.cs` (`ResolveOperand` cell check, `WorldRuleRefusal.StateCellUndeclared`); `Puck.World.Data/WorldState.cs` (`WorldStateRow.ClampToEnvelope`); `Puck.World.Server/WorldServer.cs` (`FireWorldRuleEffect`'s Write arm) |
| **A `WorldStateRow` may declare `WorldStateAdvance` — CONTINUOUS accumulation, the complement to rules' discrete periodicity/cooldown vocabulary (2026-08-06, the winner of an owner-ruled design competition, adapted onto the read seam that landed while it was branched):** the row's stored slot cell is a BASE; the read value is `base + rate*(currentTick-epochTick)`, computed LAZILY (no per-tick write, no journal entry) via `Puck.Maths.DiscreteMeasure`'s exact rational allocation — legitimate only on an int/fixed SCALAR row, never beside `generator`/`capacity`/a non-empty `cells` array. The rate is in the row's own DISPLAYED unit (a `fixed` row's `1/1` is `1.0`/tick, scaled by 2^16 before allocation, so a rate far slower than one raw Q48.16 tick still accumulates exactly: `1/240` reads `0.17498779296875` at 42 ticks elapsed); a NEGATIVE rate floors its MAGNITUDE and negates, mirroring the positive rate of equal magnitude rather than flooring the signed quantity (`-1/3` over 43 ticks subtracts 14, not 15), so decay and regen stay symmetric. An explicit write re-bases (base=written value, epoch=this tick, overwriting any authored epoch — which is why the validator's negative-epoch refusal is reachable only on a BOOT document), including inside `world.undo`'s per-entry replay keyed off each journal entry's own tick, so undo restores `(base, epoch)` bit-exactly (measured: a saved document after undo carries the pinned `"value": 1000` and `"epochTick": 142`). A declared min/max/nonNegative envelope CLAMPS the computed value every read, never rewriting the stored base — the read side of the envelope duality row above, inherited settled rather than re-litigated. **The trait's application lives at exactly ONE site, `WorldStateReader.TryRead`** — the (row, key) seam that landed 2026-08-06 carrying an unconsumed `tick` for precisely this — so every read-back, every rule gate, every HUD binding AND the `UpsertStateCell` Add compose arm resolve the same number. Two consequences worth naming. (1) The compose arm was DELIBERATELY off the seam when the seam landed ("a read-modify-write must accumulate onto the base"); that reasoning is REVERSED here by the competition's refute pass and the arm is now on it — an `add` against an advancing row composes against the LIVE value and re-bases (live 41, `add -10` → base 31, still advancing), because composing onto the base silently discarded everything accumulated since the epoch, and `FireWorldRuleEffect`'s no-op skip comparing against the base made a rule unable to reset the row it gated on. A plain row's `add` is unchanged (stored value IS the live value). (2) **BEHAVIOR CHANGE vs the competing branch: a HUD gauge/text binding now reads LIVE**, because the resolver already passes `WorldClient.Tick` — the last delivered SNAPSHOT's tick, which is itself a server tick and therefore comparable to an epoch — so the "gauge draws the frozen base" gap the branch recorded as an open seam question closed for free (measured: a gauge bound to an advancing row moved `value='45' fraction=0.188` → `value='165' fraction=0.688` across two fenced reads, while a plain-row control gauge held at `0.125`). Cross-document/identity reads (`WorldIdentity`/`WorldOwnedWorlds`) stay off the seam and therefore FROZEN at the stored base — no server, no tick, and what instant an identity document reads as of is unruled. A row declared with no value carries no slot cell, so a rule reading it refuses `StateCellUndeclared` until the first write (the interaction with the ruling above, verified both ways). KEYED-CELL ADVANCE (a per-cell rate inside a table) is CHARTERED TO THE COMBAT WAVE, not absent by oversight; a WRAP/modulo mode is an open question, not built. **`world.save` SETTLES advancing state, in the serialized projection only (owner ruling, 2026-08-06):** `epochTick` is SESSION-relative, so writing it verbatim left a reloaded document reading FROZEN at its stored base until the new session's tick counter climbed back past the old epoch. `WorldSessionCapture.Capture` (the `world.save` fold, same class that already folds render levers/population/screens) now writes every advancing row's slot cell AND every advancing keyed cell's own base as its LIVE value at the server's completed tick, projecting `epochTick: 0` — never mutating the live document (measured: the live session's own echo right after a save still shows the ORIGINAL epoch; a fresh boot from the saved file at a fresh tick-0 reads the settled value and keeps advancing immediately, where the unsettled write read back at the row's ORIGINAL authored base — a full session's accumulation silently discarded) | yes | `Puck.World.Data/WorldState.cs` (`WorldStateAdvance`, `WorldStateRow.Advance`/`IsAdvancing`); `WorldStateReader.cs` (`TryRead`, the one application site); `WorldDefinitionSerialization.cs` (`WorldStateRowJsonConverter`); `WorldDefinitionValidator.cs` (`ValidateAdvance`); `Puck.World.Server/WorldServer.cs` (`RebaseAdvanceEpoch`, `TryCompose`'s `tick`, the `UpsertStateCell` Add arm, `ApplyUndo`); `Puck.World/WorldSessionCapture.cs` (`Capture`'s `State` fold, `CaptureState`); `Puck.World/WorldMutationCommandModule.cs` (`world.save`'s completed-tick thread) |
| **Composition-core: ownership-consult and CC/death gating are ONE mechanism reading TWO deciding facts beyond the static grant table (2026-08-06):** `WorldGrants.Allows` gains a THIRD fallback, `OwnershipHold` — checked after the pre-existing group-membership fallback (`GroupHold`), before `NoHold` — resolving a document-authored `WorldOwnership` binding (`Puck.World.WorldOwnership`; subject today only ever `OwnershipSubjectKind.Group`) the SAME way `GroupHold` resolves membership: the owner (a bare principal, or every CURRENT member of an owning GROUP, one level, never recursive) reaches whatever the owned group's own rows hold. Ownership is NEVER spelled as a grant — `WorldGrants` mints no row for it; the door only READS it (`m_ownedGroups`, resynced alongside the group substrate's own indices in the widened `SyncGroups`). Safe to fold into EVERY `Allows` caller because it only ever ADDS reach, never removes one. A NEW `WorldStateRow.GatesDrive` bool separately opts an ordinary (engine-never-interprets-the-name) KEYED state row into a DRIVE-ADMISSION GATE: a nonzero cell keyed by a body's 0-based entity index (the entity-addressing convention `ArgExtremum` already parses a keyed cell key against) refuses that body's drive/action intents regardless of any Drive hold, including an exclusive reservation, until the cell reads zero again — checked fresh every tick, never latched. Precomputed (`WorldGrants.SyncState`/`TryGetDriveGate`, resolved through `WorldStateReader.TryRead` per candidate cell — the ONE read seam, never a bespoke scan) and consulted at BOTH Drive-admission doors — `WorldServer.ApplyIntentSubmission` (the per-tick channel submission) AND `WorldServer.ApplyCommand`'s generic Drive gate (`EnqueueSegment`/`SnapPose`/etc., via the shared `TryDriveGateVerdict`) — so a scripted tape segment (`player.fly`) is refused by the SAME fact a raw device press is; verified live (a tape segment applied THROUGH an undefended `ApplyCommand` before this door was wired, the break-once RED this landing closed). Scoped to those two doors alone, NEVER folded into `Allows` itself (unlike ownership): a status effect must not answer "may this principal ever drive this body" for session join/leave or an administrator's own lookup, which also query `Allows(Drive, body:<n>)` for an unrelated reason. `world.why` special-cases the identical Seam-A check before falling through to `Allows`, so the read-back can never disagree with either enforcement door | yes | `Puck.World.Server/WorldGrants.cs` (`OwnershipHold` fallback, `SyncState`/`TryGetDriveGate`, widened `SyncGroups`); `Puck.World.Server/WorldServer.cs` (`TryDriveGateVerdict`, `ApplyIntentSubmission`, `ApplyCommand`); `Puck.World.Data/WorldState.cs` (`WorldStateRow.GatesDrive`); `Puck.World.Data/Protocol/WorldGrant.cs` (`GrantRule.OwnershipHold`/`DriveGated`, `GrantVerdict.GateRow`); `Puck.World/WorldGrantCommandModule.cs` (`world.why`'s Seam-A special case) |
| **`WorldStateRow.Evicts` — a KEYED state row can opt into FIFO drop-oldest-on-overflow instead of refuse-on-overflow (2026-08-06):** before this, a bounded append-only log (chat, an activity feed) was inexpressible at any cost — the rule effect vocabulary has no `removeStateCell` effect, so a table only ever grew until `Capacity` refused every further write. `Evicts` requires a declared `Capacity` (refused by name without one, which also covers a slot row, since a slot never declares one). Eviction lives in exactly ONE seam, the `UpsertStateCell` arm of `WorldServer.TryCompose` — a pure function of the post-Upsert cells and whether THIS write minted a brand-new key — so a live apply and every `world.undo` journal re-composition can never disagree about the victim. FIFO is by INSERTION POSITION, never recency of touch: `Upsert` appends a new key to the end and replaces an existing key in place (no move-to-back), so index 0 is always the oldest surviving cell; re-writing an existing key never grows `Cells.Count` and so can never itself trigger eviction. The dropped key is named on the mutation's own `[world.mutation: ...]` apply echo (`"(evicted '<key>')"`) — never a silent drop | yes | `Puck.World.Data/WorldState.cs` (`WorldStateRow.Evicts`); `WorldDefinitionValidator.cs` (`ValidateState`'s evicts-without-capacity refusal); `WorldDefinitionSerialization.cs` (`WorldStateRowJsonConverter`); `Puck.World.Server/WorldServer.cs` (`TryCompose`'s `evictedKey` out param, the `TryApplyMutation` echo suffix); `Puck.World.Data/WorldStateCellWriter.cs` (`ApplyEviction`, `ContainsKey` — cross-project, public); `Puck.World/WorldStateCommandModule.cs` (`world.state`'s `evicts=true` read-back, `world.row.set state`'s doc string) |
| **`ActionEffect.Save` — a rule can now FIRE a save, closing the manual-save gap (2026-08-06):** rules could already gate any cadence over `$tick`/state, but had nothing to compose that persisted it, so a crashed server always rewound to the last human `world.save`. `Save` is the ONE effect with NO `WorldMutation` ordinal — the `KindMask` stays 64/64 full — because it is not a document write: it composes no candidate, journals nothing, and the sim state after a tick that fires it is bit-identical to a tick that does not (a replay hash cannot see it). It rides `WorldServer.FireWorldRuleEffect` directly rather than `TryApplyMutation`, calling a NEW `WorldServer.SaveEffectTap` (mirroring `EchoTap`) that the composition root wires to the IDENTICAL settle-at-save fold `world.save` itself runs (`WorldSessionCapture.Capture`), because `Puck.World.Server` cannot reach the render levers/screen binder/audio director/pacing control that fold needs. No authored path: it always targets `WorldDefinitionSource.SourcePath`, the SAME resolution the console's no-argument `world.save` uses — every boot shape resolves a file-backed home (`--world` or the shipped default), so there is no homeless-world case to refuse. No throttle beyond the ordinary `Level`/`Edge` vocabulary: a `Level` gate fires it every tick held (240 saves/sec), the SAME footgun `WorldRule.Mode` already documents for a level-triggered `addState` — Edge is what an autosave cadence wants. A write failure (unwritable target, missing directory) is caught at the tap and narrated on stderr by name; the firing tick is not rolled back. Refused at BODY scope by name, same terms as the HUD/placement effects | yes | `Puck.World.Data/WorldDefinition.cs` (`ActionEffect.Save`); `Puck.World.Data/WorldRules.cs` (`WorldRuleEffectKind.Save`, `WorldRuleCompiler.CompileEffect`); `Puck.World.Server/WorldServer.cs` (`SaveEffectTap`, `FireWorldRuleEffect`); `Puck.World/WorldPostBuildWiring.cs` (the tap wiring) |
| **Replay = faithful re-execution from a boot-anchored snapshot, and replay VERIFICATION is side-effect-free (owner ruling, 2026-08-06).** `replay.verify`/the post-persist half of `replay.stop` re-drive the tape's captured commands/intents/session stream against a fresh shadow world reconstructed from the record-start snapshot; boot-time draws are settled INTO that snapshot and never re-resolved (evidence: cross-process MATCH — the shadow never re-rolls a boot draw). Out-of-band console mutations sit outside the tape by accepted boundary (`WorldMutation` carries no replay entry kind at all — see `replay.md`'s pose-hash remarks). A rule-fired `ActionEffect.Save` re-derives deterministically like any other rule effect, but its LIVE tap is engine I/O (writes the world's own loaded file); during a replay drive it is SUPPRESSED with a narration line instead of reaching disk, wired explicitly at the shadow server's own construction site rather than left an accident of an unwired tap — proven by a fresh-process `replay.verify` leaving the source file's mtime untouched while the pose hash still MATCHes | yes | `Puck.World.Server/WorldReplaySnapshot.cs` (`Drive`'s `SaveEffectTap` wiring); `Puck.World.Server/WorldServer.cs` (`SaveEffectTap` remarks) |
| **Reconnect park-with-grace: a disconnected body PARKS, not disappears (reconnect-primitives wave, 2026-08-06).** `WorldPopulation.Entry` gains `Parked`/`ParkedUntilTick`: `DeactivateSeat`/`ApplyPeerDisconnected` defer the body/occupancy teardown by `population.reconnectGraceTicks` ticks (0 keeps the pre-park immediate-teardown behavior) instead of nulling the body on the spot — the retained body stays in the sim/collider set, and `IsHumanOccupied` reads unchanged (`true`) through the window BY CONSTRUCTION (`Active`/`IsRemoteHuman` are exactly what a park leaves untouched, so no parked-aware branch exists there). `ReclaimExpiredParks`, swept every tick beside `ReclaimExpiredEscrows`, tears a body down once its deadline passes with no reconnect. A reserved `CompareState` channel, `$parked:<bodyRef>` (the SAME `body:<n>`/`argmax:<row>`/`argmin:<row>` single-body-reference grammar `$distance:`/`$los:` use, so it composes with both directly), reads the named body's remaining grace ticks — 0 when unparked or the reference resolves to no live body. A re-Join to a still-parked LOCAL SEAT resumes the retained body (pose and durable state intact, no fresh spawn) when the incoming identity's profile id matches the parked body's OWN retained `Profile.Id` (both null counts as a match — an anonymous seat reconnecting anonymously); a mismatch refuses by name and leaves the parked body untouched, so a later, correctly-identified re-Join can still recover it. Grant revocation on a PEER disconnect is deliberately NOT deferred — it still fires immediately, unchanged; only the peer's body/occupancy teardown defers, since deferring the grant side too would reshape `WorldServerEvent.PeerDisconnected`'s ordered-domain/replay contract, out of this wave's scope. Peer body-RESUME is NOT implemented and is a named gap, not an oversight: the TCP Hello door carries no persistent identity a reconnecting peer could be matched against a parked slot by | yes | `Puck.World.Server/WorldPopulation.cs` (`Entry.Parked`/`ParkedUntilTick`, `IsSeatParked`/`TryResumeParkedSeat`/`ReclaimExpiredParks`/`ParkedRemainingTicks`/`IsParked`); `Puck.World.Server/WorldServer.cs` (`ApplySession`'s Join/Leave cases, `ReadWorldFact`'s `Parked` arm, the `Step` sweep beside `ReclaimExpiredEscrows`); `Puck.World.Data/WorldRules.cs` (`WorldRuleFacts.ParkedPrefix`, `WorldRuleFactKind.Parked`); `Puck.World.Data/WorldDefinition.cs` (`WorldPopulationDefaults.ReconnectGraceTicks`); `Puck.World/WorldPopulationCommandModule.cs` (`world.parked`) |
| **Console row/kind pre-checks on `UpsertStateCell` retired — the compose arm is the ONE door (2026-08-06, door-not-type fix):** `world.state.cell.set`/`.text` used to re-resolve their target row against the LIVE definition at submit time (existence, and for `.set`, the row's `Kind`, to parse `<value>` and to refuse a bool `add`) — a race against the SAME batch's own `world.row.set state`/`.remove` composing later at the tick boundary: the owner's own unfenced repro (declare `hpx`, `world.state.cell.set hpx a -5 add`, read back) landed 95 on some runs and refused "no such row" on others, deterministic only behind an explicit `world.wait`. Both pre-checks are DELETED; `WorldServer.TryCompose`'s `UpsertStateCell` arm is now the sole door for row existence (the refusal carries the "declare it first with world.row.set state <json>" remedy) AND for kind (a numeric write against a `Text` row, or `.text` against a non-`Text` row, refuses by name against the CANDIDATE — `WorldStateCellWriter.TryComposeTextCell`'s own kind check covers the second direction). The payload-encoding fix this forced: `<value>`'s grammar (decimal for `Fixed`, `true`/`false` for `Bool`, integer otherwise) is a property of a `Kind` the verb cannot know before compose, so `UpsertStateCell` gained `RawToken` — the un-interpreted wire token, parsed at compose against the CANDIDATE row's `Kind` (`WorldStateCellWriter.TryParseNumericToken`); a caller that already knows the kind (the rule-effect engine, which reads its destination row before submitting) still carries a resolved `Value` directly, `RawToken` null. A same-batch declare-then-write now composes deterministically (the declare lands first, the cell write composes against the candidate that already has the row) and a same-batch remove-then-write still refuses (composes against a candidate that no longer has it) — FIFO honesty proven both directions. The SAME shape was found and fixed at `editor.place` (`EditorSelectionCommandModule`), which pre-resolved a placement's `creationId` against the client-mirrored definition though `WorldDefinitionValidator` already refuses a dangling reference by name; every OTHER live-definition read across the console modules was audited and is a genuine read-modify-write needing the row's OTHER fields to build a whole-row upsert (`world.row.set kits`/`.tune`, `world.row.set placements`/`.face`, `editor.move`/`.nudge`), which has no compose-arm counterpart to duplicate and was left alone. **SUPERSEDED 2026-08-07 by the console-verb reduction wave:** five of those six read-modify-write verbs were RETIRED rather than left alone — `world.row.set kits`/`.tune` and `world.row.set placements`/`.face` fold into `world.row.set <path> <json>` (the whole-row door they were reading a row to synthesize), and `editor.move` folds into `editor.move`, now absolute-only, which retires this stale-read race class at the verb surface instead of tolerating it; only `editor.move` survives of that pair. This entry's own subject verb `world.state.cell.set`'s text grammar also folded, into a widened `world.state.cell.set` dispatching on the target row's declared kind — the compose-arm door and the `RawToken` encoding established here are unchanged and still the sole door | yes — reproduced RED (a reinstated verb pre-check misfired on 4/20 unfenced runs), GREEN after the fix (12+ consecutive deterministic runs for both the int/bool and text families), refusals proven by name for an undeclared row, a kind mismatch each direction, and a same-batch remove-then-write | `Puck.World.Data/Protocol/WorldMutation.cs` (`UpsertStateCell.RawToken`); `Puck.World.Data/WorldStateCellWriter.cs` (`TryParseNumericToken`); `Puck.World.Server/WorldServer.cs` (`TryCompose`'s `UpsertStateCell` arm); `Puck.World/WorldStateCommandModule.cs` (`HandleCellSet`/`HandleCellText`); `Puck.World/EditorSelectionCommandModule.cs` (`PlaceCreation`) |

**Placement.** One primitive, `fold(base, contributions, pool, consent)`, at every
composition site: at a client seat the base is the device image and the result is
the submitted intent; at the authoritative body the base is the submitted `h` and
the result is simulation state. So `h` means "whatever the previous site
produced," never "raw human," and pools STACK across sites — total raw-human-to-
world deviation can reach the sum of the per-site ceilings, each consented at its
own site. That is honest, not a bypass: the server physically cannot see the
device image, so no single number can bound the whole path. Recorded here because
it reads like a hole and will be re-filed as one otherwise.

## BUILT — prototype-grade; the rigor pass RAN and its findings are in OPEN below

Phases 0, 1, 2 and 4 are CLOSED. Phase 3 has its front door only.

Unit 6 complete · the ABI wire re-key · the channel dissolution · the co-driving
fold · Phase 3's `puck.sdf.v1` front door (first-party only, addon door shut) ·
Phase 4's reload/enable verbs, drive metering, fuel cost surface, and
degradation under contention · `world.refusals`, the declared refusal catalog ·
`Puck.Maths.FixedContributionFold`, the fold's arithmetic as a law-tested
primitive used by both the authoritative World fold and the client held-device image.

**A dropped answer is now countable, not merely narrated.** The answer ring's
per-group squeeze was already correct; its irreducible residual — a group the
ring has no cell left for at all — is `world.addons`' lifetime
`answers-dropped-total`. It cannot become a per-item verdict: ABI ordinals have
no reserved value, so an aggregate "N dropped" cell would have to lie about
which ordinal it answers, and reserving a backstop cell nets zero guest-visible
capacity.

**ONE admission predicate decides every mutation (2026-08-05).**
`WorldServer.TryAdmitMutation(principal, section, kindOrdinal,
rowScopedEditSubject, meter, out admission)` owns the WHOLE authority decision
for a document write — the section hold, the Mutate/section kind mask, the
row-scoped Edit hold and ITS mask, and the untrusted per-tick dispatch budget
(one `WorldMutationBudgetMeter`, keyed `(principal, section)`, reset at the top
of `Step`). This CLOSED a live hole: the masks were only ever consulted by
`WorldAddonRuntime.ResolveMutations`, which REIMPLEMENTED the rules, so the
ordered domain — and with it the TCP peer door — applied mutations unmasked and
unmetered while the grant door accepted `verbs:`/`budget:` and `world.grants`
echoed them as if enforced. Two call sites now, one rule: `TryApplyMutation` for
the ordered domain, and the addon seam's own pre-flight (which keeps its earlier
position — refuse before decode so a guest cannot probe the decoder — as a CALL,
never a copy).

An ABSENT kind mask is FULL REACH at the predicate, and that is forced: Console's
boot seed holds maskless `Mutate/section:<s>` rows for every section. Untrusted
strictness therefore moved to the GRANT door, which now REFUSES a maskless
`Mutate`/`section:<name>` row for an untrusted principal — a deliberate
NARROWING of what an operator may author, and what makes "absent means full
reach" safe rather than a hole. The addon door's refuse-all-on-unmasked branch
became unreachable and was deleted; `world.why`'s `verbs:` diagnosis is now
correct as already written. Relatedly, Mutate is metered on its DISPATCH lane
only — `budget:` on a `Mutate`/`state:<name>` row (the cross-document write-back
channel, which has no dispatch door) is now refused by name instead of demanded
and ignored.

`WorldOwnedWorlds.Decide` now reads `WorldStateRow.NonNegative` on BOTH numeric
arms instead of hardcoding an Int-only `value < 0`: a `Fixed`+`NonNegative` slot
used to accept a negative that the persisted document then refused at its own
next boot. `world.grant.set` gained the full token grammar (`verbs:`/`writes:`)
and accepts a `document:<id>` principal, so the cross-document write mask is
authorable IN SESSION for the first time — it was previously reachable only by
hand-editing owned-world JSON outside the process, a unification-contract
violation. The LIVE `world.grant` refusal for a Document principal stays and now
names `world.grant.set`.

**CAVEAT, confirmed 2026-08-06 (H-XDOC-TEXT scouting):** `world.grant.set`
reaches only the RUNNING world's OWN `Grants` section
(`WorldMutation.UpsertGrant` composes `current with { Grants = ... }` against
`m_definition`). `WorldOwnedWorlds.Decide` checks the OWNER's grant — and an
owner is always a catalog entry from `WorldOwnedWorlds` (an owned identity
document), never the running world document, which is not itself a catalog
member. So the grant the door actually reads — including for the pre-existing
NUMERIC write-back path (`WorldServer.Step`'s per-tick `DurableStateOutputs`,
where the OWNER is always a player's identity) — still has NO in-session
authoring door. `world.grant.set` closes the gap only for a hypothetical future
where the running world document is itself an admissible owner; today, an
identity's OWN `Grants`/`State` rows (beyond the narrow `identity.motion`/
`identity.hud` doors) are authorable only by hand-editing its JSON outside the
process — the SAME violation this paragraph describes as fixed. Confirmed by
running the engine: `identity.create` seeds an owned identity with `"grants":
[]` and no in-session verb populates it.

**H-XDOC-TEXT (2026-08-06):** `WorldDocumentSubmission` gained a `Text`
operand alongside the existing numeric one — the SAME door
(`WorldOwnedWorlds.Decide`), never a sibling. Text is Set-only (Add refuses by
name at the door regardless of what the write mask admits) and capped at
`WorldStateCapacity.MaxTextValueLength`. A dev/test submitter,
`identity.deliver`, exercises it pending the real whisper verb; `identity.state`
reads an owned identity's own row back by id. See `WorldDocumentSubmission`'s
and `WorldOwnedWorlds.Decide`'s remarks for the submitter-agnostic contract
split this revealed.

**CLOSED, 2026-08-06 (C-CHAT core lane):** the gap the two entries above name —
"today, an identity's OWN `Grants`/`State` rows ... are authorable only by
hand-editing its JSON outside the process" — no longer holds. `Puck.World`'s
new `ChatCommandModule` (`chat.inbox`/`chat.allow`/`chat.block`) is the
in-session authoring door: it composes a candidate document with an added
state row or grant row (the SAME `document with { ... }` + whole-document
`WorldDefinitionValidator.TryValidate` pattern `identity.hud` already used for
its Hud section), gated OWNER-ONLY — `context.ActingPrincipal()` must hold
`WorldCapability.Drive` over the target player's body, the SAME primitive
`player.identity`'s own authorization already checks
(`WorldServer`'s `SessionRequest.SetIdentity` arm). `chat.whisper` is the real
whisper verb H-XDOC-TEXT's own note said was pending — it derives its source id
from the ACTING player's own identity (never a caller-supplied string, unlike
the `identity.deliver` dev harness) and submits through the identical
`WorldOwnedWorlds.Submit`/`Decide` pair.

**Widened in the same lane:** `WorldOwnedWorlds.Decide`'s text arm previously
admitted only a SLOT-shaped text row (`IsSlot`, one overwritable value) — a
bounded, evicting KEYED row (`WorldStateRow.Evicts` + `Capacity`, e.g. a chat
inbox) was refused "wrong storage kind" even with a valid grant, since
`IsSlot` is false whenever `Capacity` is declared. The door now branches on
shape: a slot still overwrites; an evicting keyed row instead APPENDS, through
a new shared primitive (`WorldIdentity.TryAppendEvictingText`, minting the
cell's key from the row's own derived `<row>-seq` monotonic counter — never a
tick or wall-clock value) that a self-authored `chat.log` write and a
cross-document `chat.whisper` delivery both call, so the two can never
disagree about eviction order or key uniqueness. The underlying
upsert-or-append-plus-eviction composition itself (`WorldStateCellWriter`, new
in `Puck.World.Data`) is the SAME pure function `Server.WorldServer`'s own
`UpsertStateCell` text arm now delegates to as well — one composition, two
callers, never two readings of the eviction rule.

**Phase 3 L6 landed: the addon mutation seam.** A guest asks a Mutate handle
over a document SECTION subject (`AddonSubjectKind.Section`, the wire-reserved
slot admitted alongside `Body`; the Mutate capability bit was already reserved
at `AddonCapabilityMask.Mutate`) and acts through it with
`RequestVerbs.SubmitMutation` (ABI prefix growth, `Count` 1→2 — an older guest
declaring fewer verbs mounts unchanged) carrying a declared mutation-kind
ordinal, an unsigned guest-memory pointer, and an unsigned byte length. The
timing contract (I1-I5): decoded and dispatch-gated (stages 1-5 of six) at
`TickAddons` decode time, the SAME Step as the acts that triggered it;
enqueued as a `PendingOp.Mutate` carrying `(sourceAddonIndex, actOrdinal)`;
applied at `DrainPendingOps`, before intents, through the IDENTICAL
compose→revalidate→swap path a console mutation runs; the decided outcome
(`AddonVerdict.Applied`/new refusals `MalformedPayload`/`PayloadTooLarge`/
`Rejected`) staged at `ResolveReads` into the guest's NEXT batch. The answer
cell is RESERVED at whole-batch decode time, before `EmitDisclosures`/
`MergeAnswers` ever see the remaining budget — the ABI handshake's
`outCap <= inCap-1` relation proves the reservation cannot fail. `MutationKindMask`
(a `WorldGrant.KindMask` nullable field, legal on a concrete-section
Mutate row and on a concrete-state-row Edit row) narrows a hold to specific
kind ordinals — refused at the grant
door for a bit outside the target's own declared kind set, or an effective
mask of zero; a null-mask RE-GRANT of the same row clears a prior mask
(the one field that does NOT follow budget/reach's "write only when carried"
rule). Its SIBLING `DocumentWriteMask` (`WorldGrant.WriteMask`) carries the
cross-document write-back channel's own `Set`/`Add` vocabulary on a
`Mutate`/`state:<name>` row — a separate TYPE, because one `ulong` read
under whichever vocabulary the subject kind implied made bit 0 mean
`UpsertKit` on one row and `Set` on another. Mutate joined the metered positive list (untrusted Mutate over a
concrete `section:<name>` requires `budget:<n>` AND, since 2026-08-05,
`verbs:<name,...>` — `IsLegitimateSubject` already refused the untrusted
wildcard, and a `state:<name>` row takes neither, being the cross-document
lane). Boot-anchored replay arming (Phase-3 plan AXIS 1):
`MountedAddon.HasEverPumped` latches the first admitted execution attempt;
`WorldServer.AnyAddonEverPumped` refuses `replay.record`'s arm; addon
lifecycle verbs now REFUSE (not merely warn) while a recording is armed;
`WorldReplaySnapshot.VerifyMountedAddons` compares the mounted SEQUENCE
index-by-index, not by name lookup. P4-lean retired L6's interim live-verb-mask
arm gate: the shared grant/revoke leaf now carries `VerbMask` on tape under
the `PKRL` magic, so a masked row no longer makes arming dishonest.
Verified live against `wasm/puck-addon-hudbuilder` (a real compiled WASM
guest): asks a Mutate/section:hud handle, submits `UpsertHudPanel`, and —
ONLY after reading back `Applied` — submits a chained `UpsertHudElement`,
then goes quiet; `world.hud` shows both elements. Two pre-existing
`puck-stdlib` bugs surfaced and were fixed in the same change: the Request
channel's declared `VerbCount` was hardcoded to the old single-verb count
(`abi.rs`, now derived from the generated `REQUEST_VERB_COUNT`), and the
hand-written wire→enum `decode_verdict` match had no arms for the four new
verdicts (silently read them as `None`) — both would have hidden ANY future
verb/verdict addition identically, not just this one. `puck-addon-default`
and `puck-addon-hudbuilder` rebuilt and re-pinned in both homes
(`WorldDefinition.cs`, `default.world.json` — the latter retired under the
2026-08-06 four-world charter; no shipped world mounts either addon today);
`puck-addon-channelwalk`/
`queryspam`/`stalekind`'s COMMITTED `dist/` artifacts are now STALE relative
to `puck-stdlib` source (same ripple L5's ledger entry hit) — not rebuilt in
this landing, flagged for a follow-up sweep. Scope note (STALE as of lane C2's
landing — see below): the decoder (`WorldAddonMutationDecoder`) wired the 5 HUD
kinds only at THIS landing; it now ALSO wires `UpsertPlacement`/
`RemovePlacement`/`UpsertStateRow`/`RemoveStateRow` — the remaining 40 declared
kinds refuse by name (`kindOrdinal has no decoder wired`), additive to wire in
later. The guest
crate ships `main` only; `spam`/`badkind`/`badjson`/`hugepayload` (the
refusal-path battery variants) are NOT yet implemented — the grant-door and
dispatch-door refusal PATHS they would exercise are implemented and were
spot-verified via console-issued grants/asks in this session, but a
committed guest-driven battery for them is a follow-up. See the ledger's
2026-08-02 L6 entry.

**Headless P6b landed: the trusted-by-authorship re-cut** (owner ruling
2026-08-02). The fold's contributor classification keys on HOST LOCUS, not
principal kind by coincidence of vocabulary: a document-mounted (Simulation-lane)
addon is WORLD LOGIC — its contribution joins the TRUSTED class (added outside
the pool, under the World contributor bound), gated by its OWN declared Reach
only, never a seat-authored ceiling (consent does not apply to world logic — a
world doesn't ask permission to apply wind). This is a real, live behavior
change: `channelwalk` (a document-mounted addon that composes only under
seat-authored consent before this landing) now composes as trusted, with no
consent grant of any kind — verified live (`world.grant addon:channelwalk drive
body:0 budget:60 channels:strafe`, no ceiling, then `player.channels 1` shows
`strafe ... folded=1(65536) composed=1(65536) trusted=[addon:channelwalk]
untrusted=[]`). Fuel/budget remain robustness bounds on an addon regardless — an
untrusted-for-ADMINISTRATION principal (Addon/Peer) still requires an explicit
`budget:<n>` on its Drive/Observe rows, is still handle-mediated, and still
cannot hold a wildcard subject; only the FOLD's trust classification moved. The
untrusted-pooled-under-consent treatment is unchanged for a genuinely untrusted
principal (a Peer today; a future client-hosted addon would join that branch —
`PrincipalKind` carries no host-locus field, so this is a NAMED addition, not a
predicate change, and needs its own kind the day a client-hosted addon exists).
Occupancy also widened (`WorldPopulation.IsHumanOccupied` now ORs an admitted
Peer) and a co-driving Drive grant against a remote-admitted body now refuses at
the grant door for any principal but that body's own Peer — both LATENT, since
no live Peer admission exists before the P7 socket phase; landed now so neither
needs a second change the day admission exists. `ChannelReachMask.Meet` now
routes through `Puck.Maths.MeetMask64`.

**Context routes landed (lane R1, widenings 1-4 of the owner's converged
framing).** The route's target widened from `Screen(int)` to a `GrantSubject`
union (screen or body), stored beside its capture policy and channel mask in
the grant table's per-principal `Control` row (`WorldGrants.SetControlRoute`/
`ControlRoute`/`RouteCapture`/`RouteChannelMask`); `IsLegitimateSubject` admits
`Control` over a `Body` subject, bounded by the population like Drive/Observe.
`player.engage <screen>|body:<n> [capture:on|off] [player]` — the bare-integer
form still means a screen (unchanged UX); `capture:off` is the MIRROR: the
source body keeps integrating while its channel-masked intent ALSO reaches the
target every tick (`WorldBody.EngagedIntent` is now captured on every
`Advance`, not only while captured). A screen target's translation is now
AUTHORED data (`WorldScreenRoute.Translation`, channel name → `WorldPadElement`)
compiled once in `WorldEngagement`'s constructor, defaulting to the exact map
`Translate` hard-wired before this change; `WorldScreenRoute.Channels` is the
route's channel mask (default every ordinal). A body target's contribution is
channel passthrough queued through the ordinary co-drive `StageContribution`
path (see the Decided table's replay-visibility row). `WorldEngagement`'s
latch/route consistency repair gained a THIRD discriminator
(`PrincipalGrants.m_routeMirror`) so a deliberately-mirrored route's ordinary
disengage is never misreported as the route-without-latch REPAIR case — only a
route that was never established through `Engage` (a bare `world.grant`) still
reads that way. `screen.state`/`world.screens`/`player.channels` echo the
widened truth (target kind, capture, mask). Verified live: classic screen
engage/disengage unchanged (`verification/authority/run.ps1` and
`docs/verification/engagement-dissolution/run.ps1` — BOTH QUARANTINED
2026-08-06, see each stub — both green at the time, after updating their
asserted wording to the new `Describe()`/echo text and the `PKRN` magic);
possession (route a seat to another body, capture on — the source freezes, the
target moves under the source's channels, `replay.record`/`replay.stop`/
`replay.verify` all report MATCH); mirror (capture:off on a screen — the pad
drives the machine AND the source body keeps moving, confirmed via
`screen.state`'s `engaged=p1(mirror)` and a strictly-increasing `frames=`).

**The fold's known gaps are in the Decided table above** — the mount cap is
RULED but NOT LANDED. Also: a trusted press still bypasses the fold, and
multi-seat-on-one-body is untouched. The replay tape now carries a grant row's
reach and ceiling (magic re-keyed to `PKRJ`; older tapes refuse loudly), and a
non-owner's contribution carries its held/composition channels through the fold,
not only its intent.

**P5 landed: addon lifecycle joins the ordered domain.** `world.addon.mount`/
`.unmount` are a NEW 11th `WorldSubmissionPayload` kind (`AddonLifecycle`,
wrapping `Protocol.WorldAddonLifecycle.Mount`/`.Unmount`) with its own leaf
codec in `WorldSubmissionCodec`, routed `CommandRouting.Simulation` and
BUFFERED to the tick boundary through the SAME door a document mutation
drains through (`WorldServer.EnqueueAddonLifecycle` → `DrainPendingOps` →
`TryApplyAddonLifecycle`, gated on `Mutate`/`section:addons` before the
runtime is touched). `WorldAddonRuntime.Mount`/`.Unmount` are the runtime
half: `Mount` mirrors the boot-time per-row mount sequence (lazy host,
compile-under-hash, Response-channel gate, disclosure, admit) and refuses a
name already mounted (use `.reload` for that); `Unmount` is STRONGER than
`.disable` — the guest leaves `Receipts`/`MountedCount` entirely. Both are
RUNTIME-only, like `.reload`/`.enable`/`.disable`; the DOCUMENT-only
`world.row.set addons`/`world.row.remove addons` mutations are untouched and still never
touch the runtime — unifying the two is not this landing's scope. Captured on
the replay tape via `LoopbackTransport.AddonLifecycleTap` and
`WorldReplaySnapshot.WorldReplayEntry.AddonLifecycle` (magic re-keyed
`PKRL → PKRM`, discriminant 5); `Drive`'s re-run applies a recorded entry
through the identical `EnqueueAddonLifecycle` door, so replay RE-EXECUTES a
mount/unmount rather than replaying its effect. Because they now ride the
tape, `world.addon.mount`/`.unmount` are NOT refused while a recording is
armed (`WorldAddonCommandModule.RefuseIfArmed` still gates only
`.reload`/`.enable`/`.disable`, which stay outside the ordered domain).
**P7 landed: a real TCP transport, so the two LATENT halves above are live.**
`Server/WorldTcpHost` binds `host.listen`/`--listen` (a document field per the
unification contract) and admits remote connections onto the SAME ordered
domain a local script drives: the raw Hello handshake
(`WorldHelloDoor.TryAccept`) runs off the tick thread; admission
(`WorldServer.TryAdmitPeerConnection` →
`WorldPopulation.TryAdmitRemotePeer`), every subsequent leaf-codec submission,
and disconnect (`WorldServer.DisconnectPeerConnection`) are marshaled onto the
tick thread and drained at the top of every fixed step
(`WorldServerStepShell.Step`, before `WorldServer.Step` — the design's §1.5
"deterministic fair merge" window, kept to its simplest correct shape: one
global FIFO, no per-connection quotas). `WorldPopulation.IsAdmittedPeer` is no
longer permanently `false` — a remote-admitted body carries a new
`Entry.IsRemoteHuman` marker (`world.population` now skips it, so a census
edit can never silently reassign a connected human's body), and admission
shares the `networkPlayers` cap with the census exactly as ruled. `--connect
<host:port>` is a MINIMAL, self-contained client (`Puck.World.WorldRemoteClient`)
— not a second composition graph — that speaks Hello then a small stdin verb
grammar (`player.where <n>`, and the harness's own `peer.*` tokens —
`peer.hud.panel.remove`, `peer.hud.element.remove`, `peer.sleep`, `peer.quit`)
through the SAME
`WorldSubmissionCodec` leaf codecs the loopback always uses. v1's downstream
(server→client) wire carries exactly one lane — Completion (Ack/Session/Query),
strictly request-then-response per connection, so no correlation id travels on
the wire (`WorldTcpWireFormat`); streamed snapshots/definitions/compositions/
levers are NOT carried in v1. `world.peers` (`WorldNetworkCommandModule`)
echoes the connection table — the read-back the admission/disconnect decision
needed. See the ledger's P7 entry for the smoke that proved admission, a
round-tripped query, `world.peers`, and disconnect-driven grant revocation.

**Blank-slate lane B landed: the `state` section.** `WorldStateRow` (`$type`
int/fixed/bool/text — fixed rides raw `FixedQ4816` bits, the addon-wire
convention, never a decimal/double encoding) upserts/removes through
`WorldMutation.UpsertStateRow`/`RemoveStateRow` (ordinals 46/47), journaled/
undoable/`world.save`-persisted for free by riding the mutation substrate.
`HudBindingVocabulary` grew `state.<name>` — WORLD-SCOPE panels only; a
seat-scope profile panel is authored independent of any particular world and
can never verify a row exists, so it refuses every `state.<name>` token by
construction. A gauge's range is BOTH-OR-NEITHER `min`/`max` on the row
itself; absent (or a bool/text row), it draws EMPTY, the existing "unbound
gauge draws empty" precedent. `GrantSubjectKind.State` (`state:<name>`)
widens `Edit`'s domain; the domain-seeded `Edit/all` every seat and Console
already holds reaches every row until narrowed. AT THE TIME THIS LANE
LANDED the WASM guest ABI carried no mutation verb at all, so an
addon-submitted state mutation was not implementable without ABI growth —
**STALE as of Phase 3 L6** (see this file's L6 entry below): the ABI now
carries `RequestVerbs.SubmitMutation`, but `WorldAddonMutationDecoder`'s
stage-6 decode still wires only the 5 HUD kinds, so `UpsertStateRow`/
`RemoveStateRow` remain unreachable from a guest today for a DIFFERENT
reason (a decoder gap, not an ABI one) — see lane C2's entry. This lane's
own verification was a console-principal grant-narrowing proof (revoke
`edit all` → deny; grant the
concrete `state:<name>` → that row succeeds while a DIFFERENT row still
denies → re-grant `all` → control succeeds), the same per-instance-narrowing
shape `edit profile:<id>` already proves. Verified live end to end: HUD
text+gauge tracking a mutated row, `world.undo` reverting it, `world.save` +
`world.reset` round-tripping the persisted value, and `replay.record`/
`replay.verify` MATCH spanning a state mutation. See the ledger's dated entry.
**SUPERSEDED — the keyed-table primitive collapsed into one CELL substrate (maths-excursion worktree, prototype
"one substrate beneath both"): a slot is a table with one key.** The paragraph this replaces recorded a SEPARATE
`WorldStateRow.TableRow` kind, a separate `GrantSubjectKind.Table` subject, and a separate
`UpsertTableEntry`/`RemoveTableEntry` mutation pair — three concepts doubled that a scalar row already had. `state`
is now ONE `WorldStateRow` type over a typed-value cell (`WorldStateCell`, `CellKind` — `int`/`fixed`/`bool`/`text`,
the same four tokens a slot's `$type` always spoke; `counter`/`timer` were never a fifth/sixth kind, only `fixed`
and `int`+`NonNegative` spelled differently). A SLOT (one cell keyed the reserved `WorldStateRow.SlotKey`) and a
KEYED row (author-keyed cells) are authored through ONE JSON shape (`WorldStateRowJsonConverter`) —
`{"name":.., "kind":"int"|"fixed"|"bool"|"text", …}` carrying EITHER a bare `value` (sugar for the one cell keyed
`SlotKey`) OR a `cells` array, two optional fields rather than two `$type` discriminators; both, or a `value`
beside a `capacity`, refuse by name, and there is no `$type`/`rows` member left to author. Every mechanism beneath
is likewise single: ONE grant subject
(`state:<name>`, `GrantSubjectKind.Table`/wire value 8 retired, left unassigned), ONE whole-row mutation pair
(`UpsertStateRow`/`RemoveStateRow`, unchanged), ONE per-cell mutation pair (`UpsertStateCell`/`RemoveStateCell`,
the SAME ordinals 49/50 `UpsertTableEntry`/`RemoveTableEntry` held, renamed+widened rather than retired — it is the
identical per-cell write, now legal on any row's cell, not only an author-declared table). ONE console verb family
too: `world.row.set state`/`world.row.remove state` (whole row), `world.state.cell.set` (one cell of ANY declared
kind — numeric, bool or text, dispatching on the row's own kind, riding `UpsertStateCell`'s optional `Text` payload
beside the numeric `Value`; same mutation kind, same mint-if-absent, same reserved-`$`-prefix rule),
`world.state.cell.remove` (either shape), and `world.state [row] [key]` reading all three grains back — one family
over one substrate, because a slot IS a row with one cell. Fixed-kind values speak DECIMAL
everywhere a human
reads or writes them (document JSON, console verb arguments, addon mutation payloads, validator refusal text,
read-backs) through `FixedQ4816.TryParse`/`ToString` — the prior raw-Q48.16-bits convention this lane's own doc
comment defended is retired; only the per-cell mutation payload and the addon ABI channel wire stay raw. The
`Edit`/`state:<name>` hold additionally admits a `MutationKindMask`, recovering verb-scoped authority beneath the
one subject: `verbs:UpsertStateCell,RemoveStateCell` grants "bump the score" without "redefine the score", while an
UNMASKED row keeps full reach (opt-in narrowing, no seeded-grant churn). Verified live: the same
declare/set/remove/refusal/undo/grant-narrowing shape the original lane proved, replayed against the collapsed
model, plus the mask pair with a sabotage control — see the ledger's dated entry. Still NOT run, and STRUCTURALLY not runnable: `replay.record`/`replay.verify`
over a per-cell mutation. `WorldReplayEntry` (`Server/WorldReplaySnapshot.cs`)
declares ten cases — Command, Grant, Revoke, Session, Designation,
PeerAdmitted, PeerDisconnected, AddonLifecycle, Rebuild, ScreenOp — and no
`Mutation` arm, so `WorldMutation` is outside the tape's capture scope and a
MATCH over a mutating session observes nothing about the mutation. A
mutation-path determinism claim is made with two independent fresh-boot runs of
the identical stdin script, diffed; never with a tape. Richer key kinds than string,
and per-body tables are still open questions, not built (a retention/eviction policy for a row's cells landed 2026-08-06 — see `WorldStateRow.Evicts` above).
**SUPERSEDED — a validated identifier type replaces every hand `IsSafe` check, and the HUD binding grammar widens to
cell grain (maths-excursion worktree, "a slot is a table with one key" second half).** `WorldSafeName`/`WorldCellName`
(`Puck.World.Data/WorldSafeName.cs`) are readonly record structs that CANNOT HOLD an unsafe value — construction/JSON
parse refuses by name, naming the offending character. `WorldSafeName` (the reserved-character kernel, plus a bare
`"."`/`".."` refusal) types every world/owned-world id (`WorldIdentityDefinition.Id`, `WorldIdentitySeed.Id`) and every
`world.instance.start` name; `WorldCellName` (the kernel plus a "no dot anywhere" rule) types `WorldStateRow.Name` and
`WorldStateCell.Key` — the dot-free rule is what makes `state.<row>.<key>` parse unambiguously, since a row/cell name
can never itself contain the grammar's separator. `WorldOwnedWorldFileName.For` now takes a `WorldSafeName` and no
longer escapes/collapses characters (the mapping is injective by construction), so every hand `IsSafe`-plus-collision
check across `WorldOwnedWorlds.Create`/`ReplaceFromSync`, `WorldOwnedWorldSync.KeyRefusal`, and
`WorldInstanceHost.IsSafeSegment` is deleted — the type is the one refusal path left. `HudBindingVocabulary` widens
`state.<row>` (unchanged: the row's own slot cell) with `state.<row>.<key>` (a named cell in ANY row shape; the gauge
fraction still reads the ROW's own declared `min`/`max` envelope, cells carry no envelope of their own) — validated at
both row AND key existence (`WorldDefinitionValidator.ValidateState` now returns rows-by-name, not just names, so
`ValidateHud` can refuse a cell-form binding naming a real row but no such cell, by the same `UnknownBinding` reason).
**Blank-slate senses lane landed (lane A, 2026-08-02): observation-cell event delivery, four world-scoped families
plus one addon-scoped.** `WorldEventFeed` (new, `Server/`) collects collision-pair PROXIMITY edges (a flat threshold
test — this engine has no body-vs-body physical resolver to reuse the shape from), region enter/exit (a placement's
new `WorldPlacementRegion` facet — a NAMED VOLUME, addressed by the carrying placement's own `Id`, never a second
name), seat join/leave, and route engaged/disengaged (pushed by `WorldServer.ApplyCommand`'s Engage/Disengage arms)
once per `Step`, after the population settles; `WorldAddonRuntime.EmitEvents` filters the flat edge list per addon
by `(GateA ∨ GateB)` and stages Observation cells into the SAME batch `EmitDisclosures` already builds, within
whatever ring room remains — the ratified overflow doctrine (ordered prefix, drop-newest, per-mount gap counter)
implemented exactly, no per-subject numeric throttle beyond it. The FIFTH family, machine-memory watches
(`WorldAddonRow.MemoryWatches`), is ADDON-scoped (each row declares its own `(screen, address, length)` rows) and
reads `Server.WorldMachineHost` DIRECTLY (`WorldMachineHost` implements `IWorldMachineMemoryPeek` itself, reached
through `WorldServer.Machines`) — **CAVEAT FLIPPED (authoritative-machines campaign, 2026-08-03): the old
"nothing calls `WorldScreenBinder.AdvanceMachines` outside the windowed `WorldSimulation`, so a headless boot's
screens never boot a machine" reading no longer holds.** Machines boot and step inside `WorldServer.Step` in EVERY
boot shape now (`Server.WorldMachineHost.Advance`, fed directly from `WorldEngagement.FoldTick`'s pads, no
client/wire round-trip), so this family publishes in a headless boot exactly as it does windowed; the former
settable `WorldServer.MachineMemoryPeek`/`IWorldMachineMemoryPeek`-registered-by-presentation seam is GONE. New grant
vocabulary: `GrantSubjectKind.Region`/`Seat` (wire values 6/7, beside `State`'s 5 — `WorldSubmissionCodec` re-keyed the tape magic
`PKRQ → PKRG`, since `WorldGrant` gained the `EventBudget` field on the SAME leaf), `events:<n>` grant-row token.
Verified live (region family, the smoke-tested one): a compiled guest (`wasm/puck-addon-eventwatch`) reacts to a
region enter/exit by writing a visible HUD row (`world.hud`'s element echo gained a `text='...'` field for an
UNBOUND literal — the same read-back gap `player.channels`/`world.why` exist to close, for `WorldHudElement.Text`);
`replay.record`→walk in/out→`replay.stop`/`replay.verify` both report MATCH at the identical pose hash. Collision,
seat, and route families share the identical delivery mechanism but were verified by code reading and the grant
door's own refusal paths, not a second compiled guest — a follow-up battery, not a gap in the mechanism itself.

**Blank-slate lane C1 landed: the movement platform, plus the `blank` template
world.** Four gaps closed, all opt-in per kit (every shipped kit stays
`Heading`/no-sprint/no-smoothing, byte-identical): held-channel sprint
(`SprintMultiplier`×the commanded speed while `SprintChannel` reads held, the
response table's ramp untouched — a sprinting turn-around still rides the
same accel curve through zero); camera-relative move + instant facing snap
(`MotionMoveFrame.World`/`FacingSnap`); authored seat-chase smoothing
(`WorldRig.Chase.SmoothRate`, the low-pass shape `WorldAnchor.Group` already
uses, `0` = off); and the `WorldScreenRoute.EngageChannel` interception (see
the Decided table). Verified by RUNNING `blank.world.json`: sprint measured
6.32u vs 3.97u over an identical 1s push (ratio 1.59, target 1.6);
reversing a push flips `player.where`'s yaw from 0° to 180° within ONE
simulation tick (no ramp); pressing `jump` within radius of a
`gaming-brick`-sourced, engageable screen naming `engageChannel:"jump"`
engages (`screen.state` reads `engaged=p1`, `[world.engage: seat1
auto-engaged screen:0 — context button]` on stderr) with NO jump
(`world.contacts` stays `grounded=true`); the identical press far from any
screen jumps (`grounded=false`); `replay.record`/`replay.stop`/
`replay.verify` all report `MATCH` across the whole sequence — the
interception needs no tape entry of its own. The kit's reused
Demo-matched tuning (the shipped kit's authored numbers, already landed
before this lane) derives a time-to-top-speed of `4/45 ≈ 0.089s` and an
apex time of `5.5/14 ≈ 0.393s` — the Demo research targets, exactly,
because the platform already carried them.

(`blank.world.json` and `arcade.world.json`, named throughout this and the
next paragraph, were both retired under the 2026-08-06 four-world charter —
`Puck.World/Assets/worlds` now ships only `play`/`dive`/`kart`/`jump`. The
mechanisms this lane proved — held-channel sprint, camera-relative move,
seat-chase smoothing, screen-engage interception, and the `rules`-driven
reaction ladder below — are unchanged, live code; no shipped world currently
exercises the `rules` half of it, or the `gaming-brick`-cart engage demo.)

**Blank-slate lane C is COMPLETE, and the arcade example carries NO addon —
the whole reaction ladder is authored `rules` data (owner-authorized
BUILD-AS-YOU-GO, 2026-08-05; see this file's world-rules entry above and the
ledger's "the arcade addon dogfoods the rules goal" entry).** `arcade.world.json`
(derived from `blank.world.json`): one `gaming-brick` screen sourced from a
hand-authored SM83 cart (`Puck.Forge.Games.ArcadeQuestGame`/`ArcadeQuestRom`,
built ROM committed at `src/Puck.World/Assets/roms/arcade-quest.gbc`) with
`engageChannel:"jump"`/`engageRadius:1` (the Demo's 1.8u halved and rounded);
a `cabinet-marker` placement co-located with the screen, carrying a `Region`
facet (`radius:1`) as the prompt sensor; a boot-declared, PERMANENT `prize-pad`
placement (reusing the `ground` creation) carrying the region the pickup rule
senses; a `prize-token` creation referenced only by a `prize` placement a rule
spawns/removes LIVE, never authored at boot. `addons` and `grants` are BOTH
empty — no document-mounted guest, no addon grant rows. Four `rules` rows drive
the whole ladder: `cabinet-prompt-show`/`cabinet-prompt-hide` (an edge pair on
`$region:cabinet`, gated by a `cabinet-prompt-shown` bookkeeping row so the
`hide` rule's naturally-true-at-rest gate does not misfire at boot) upsert/
remove the accent-styled `arcade-prompt` HUD row; `cabinet-win` (edge on
`$machine:0:49665 != 0`, the NEW reserved fact reading the cart's win-flag byte
live off `WorldServer.Machines.TryPeek` — no addon memory-watch row involved)
fires `setState win=true`, `upsertPlacement prize`, and `upsertHudPanel
arcade-win` as one ordered effect list; `prize-pickup` (edge on
`$region:prize-pad >= 1 and win == 1`) fires `removePlacement prize`,
`addState score`, and `upsertHudPanel arcade-score` (its text element BINDS to
`state.score`, resolved live through the same `IHudBindingResolver` the
renderer uses). Verified live end to end, headless, by RUNNING the game and
actually winning the cart (`player.pose`/`player.engage`/`player.press
strafe`×3/`screen.peek` confirming `0xC201` flip 0x00→0x01): region enter →
`arcade-prompt`; 3 RIGHT presses → win latches → `arcade-win` + `win` state row
+ `prize` placement (`world.status` placements 3→4); entering the prize's
region → placement removed (4→3), `score` state row (int, 1), `arcade-score`
panel. Two independent fresh-boot headless runs of the identical script
produced state-identical output (mod the documented cross-process pacing
caveat on absolute tick numbers). **The discriminating case this landing
found:** a `$region:<placementId>`/`$machine:` gate recompiles against the
WHOLE document on every mutation (`RecompileRules` runs inside every
`Install`), so a rule can only sense a region that already exists, and cannot
be authored against one a later effect will spawn — which is why `prize-pickup`
senses the PERMANENT `prize-pad`, never the spawned/removed `prize` token
itself. `wasm/puck-addon-arcade` and its compiled asset are DELETED, not
quarantined — the port is complete, so the compiled guest is dead code, not a
kept fallback. The decoder capability `Server/WorldAddonMutationDecoder
.TryDecode` gained for placement/state mutations (`UpsertPlacement`/
`RemovePlacement`/`UpsertStateRow`/`RemoveStateRow`) remains general-purpose
addon infrastructure — see [addons.md](../.claude/skills/puck-world/references/addons.md)
— it is simply no longer exercised BY the arcade example.

## OPEN

- **Context routes (lane R1) landed widenings 1-4; widenings 5 and 6 have both
  landed since, in follow-up lanes — see STATE.md's DECIDED table for both
  rows.** The PERCEPTION ANCHOR (widening 5) now swaps LIVE: R2 built the
  one-body-index-per-seat indirection, and the follow-up (ANCHOR-SWAP) wired it
  to resolve through the widened route — a seat possessing a body (Control
  route targeting that body, capture on) perceives its camera, audio listener,
  and `seat.<n>.position.*` HUD bindings from THAT body every tick, over the
  same per-tick loopback read the `engagement` context family already used; a
  mirror route (capture off) or a screen route leaves the seat perceiving its
  own bound body, and a possessed body's own presentation-side classifications
  (footstep cue, seats-always-cast shadow) are DECIDED as unaffected — they
  stay keyed on the body's raw index band, not the anchor. Seat-side context
  rows (widening 6, `(family, state) → binding group`) landed with lane R3.
  Still open: authored per-route translation/channel-mask (widenings 3-4) are
  document-only today (`WorldScreenRoute.Channels`/`Translation`); a body
  target always reaches every ordinal (no per-target document row exists to
  author a narrower one from) and there is no `player.engage` verb override
  for the mask — only for capture.
- **The principal-selection defects across the player-facing command surface are
  CLOSED, after TWO adversarial-review passes each caught the previous one
  leaving real gaps open — read the ledger's two correction entries before
  trusting this bullet alone.** `player.join`/`leave`/`setProfile`/`confirm`/
  `assign`/`claim`/`cycle` all consume a MANDATORY acting principal now (never
  a defaulted/nullable parameter): a HANDLER never constructs one — it reads
  `context.ActingPrincipal()`, the caller's ingress-stamped identity
  (`Puck.Commands.CommandContext.Principal`'s own documented rule) — and the
  one place a WorldPrincipal IS constructed, `Client/PlayerRoster.cs`'s
  `SelfProvisioned(slot)`, is reserved for the genuine bootstrap case (an
  UNBOUND device's first touch, which has no source seat to speak of); an
  ALREADY-BOUND device relocating (`player.claim`, pad-cycle) authorizes as
  ITS OWN stamped source-seat principal, never a handler-constructed
  `Seat(target)`. Every check runs BEFORE mutating local state.
  `AssignDevice`'s cascade — dissolving an orphaned SOURCE participant when its
  last device relocates away — now authorizes BOTH the source and target body
  under the SAME real actor before touching either, closing a hole where a
  principal holding Drive over only the destination could delete an unrelated
  source body through a fabricated `SelfProvisioned(source)` the dissolution
  used to check instead. `AssignDevice`'s previously-unchecked "join an
  already-occupied team" path is gated too, and the whole device-map mutation
  stays PROVISIONAL (unmapped from source, unmapped onto target) until every
  affected body's check clears. `InputRouter`'s `CommitSlot` precommit (a
  device→slot routing annotation written BEFORE the join it enables is even
  attempted) no longer strands a device on a denial: `JoinPending` rolls the
  stale reservation back, and `player.south`'s typed echo reports a real
  denial instead of a hardcoded "joined pending". `Confirm`/`Activate` compute
  the final candidate profile WITHOUT installing it, submit, and only mutate on
  acceptance (`ConfirmOutcome.Denied`). A null-deref in `RouteMove` (a denied
  device-driven join followed by an unguarded `!`) is fixed. `player.join`'s
  and `player.assign`'s echoes distinguish denied/full/occupied (`JoinResult`,
  `AssignOutcome.Denied`) rather than reporting every failure as "roster is
  full". `player.disengage` checks Control over the CURRENTLY engaged screen
  before touching anything, and separately decides what happens when the latch
  (`WorldBody.Engaged`) and the route (the grant table's Control/screen row)
  disagree: the STUCK-LATCH direction (latch set, no route) self-heals
  unconditionally (pure body state, nothing in the grant table to protect);
  the ROUTE-WITHOUT-LATCH direction — which can exist through a perfectly
  legitimate `world.grant` with no `Engage` behind it yet — now REQUIRES
  Control over that screen before clearing it, because that clear mutates the
  SAME `Control` subject set an ordinary `world.revoke` administers, and the
  first pass's unconditional self-heal let any principal strip any other
  principal's legitimately-granted row. `world.population`/`world.control`'s
  peer-source lever now report a denial by name instead of misreading a flat
  refusal as "clamped to -1" or silently discarding the reply.
  `world.addon.reload`/`enable`/`disable` gate on `Mutate`/`section:addons`.
  (`profile.create`'s `Edit`/`all` gate was part of this closure at the time;
  the profile catalog was later DELETED, not ported — an identity is now an
  owned world the actor mints for itself, ungated by design, and the gated
  successor ingress is seating it, `SessionRequest.SetIdentity` under `Drive` —
  so the battery's case 07 re-pointed to that pair.) None of these joined the
  `world.refusals` catalog —
  console-tier text refusals, the same shape the affordance/bind/press/mutation
  gates already use outside it, so this does not widen that documented gap.
  Verified by RUNNING `Puck.World` over stdin, including a REAL `player.engage`
  (a synthetic-but-genuine Game Boy ROM built from `Puck.Forge.Tune.TuneRom`,
  not a synthetic grant), all four disengage latch/route combinations, and an
  attack case that revokes the actor's Control BEFORE attempting the
  route-without-latch repair (the first pass's proof restored the grant first,
  so that exact attack went untested). Two round-2 fixes — claim/cycle's
  ingress-stamped authorization and the CommitSlot rollback — are fixes to the
  PHYSICAL/bound-signal ingress path that a headless stdin battery structurally
  cannot drive (every stdin line stamps Console and never touches
  `CommitSlot`); they are verified by code reading and a clean build, NOT by a
  committed script, and `verification/authority/README.md` says so plainly.
  The scripts WERE committed and re-runnable — `verification/authority/`
  (README + `run.ps1`, builds once, exits nonzero on any miss OR crash) —
  because a transcript in a chat is not durable; the battery is QUARANTINED as
  of 2026-08-06 (its cases assumed the retired `default` world's screen/addon
  furniture), successor `tests/Puck.World.Tests`. See the ledger's three
  2026-08-02 entries (the original pass, and the two adversarial-review
  corrections) for the full defect list and evidence. The OPEN items below
  this one are UNTOUCHED by this work — do not read this bullet as closing
  them.
- **The replay tape captures eight of thirteen submission payload kinds, plus
  intents and peer lifecycle server events (counts current as of the
  authoritative-machines campaign and the reconnect-primitives wave, both
  2026-08-03/2026-08-06 — see DECIDED above).** `LoopbackTransport` taps
  intent, command, designation, grant, revoke, session, and (P5)
  addon-lifecycle (`world.addon.mount`/`.unmount`, via the shared
  `WorldAddonLifecycle` leaf codec, magic re-keyed `PKRL → PKRM`);
  `WorldServer.ServerEventTap` records `PeerAdmitted`/`PeerDisconnected`.
  **CAS-REPLAY (2026-08-02) added Rebuild** (`world.reset`/`world.load`/
  `world.reload`), taped via `WorldServer.RebuildTap` — apply-time, not
  submission-time, because Reset's CAS content hash (the base's own canonical
  bytes) is only knowable once `ApplyRebuild` reads its own `m_base`. The trio
  no longer refuses while a recording is armed; a re-drive instead re-resolves
  the candidate (Reset: its own base; Load/Reload: a FRESH re-read of the
  tape's path hint, since the tape never embeds the document) and refuses BY
  NAME on a `sha256-64` content-hash mismatch. Magic re-keyed `PKRP → PKRQ`.
  The authoritative-machines campaign (2026-08-03) added ScreenOp
  (`WorldServer.ScreenOpTap`); Session and Designation are ALSO now captured
  (`LoopbackTransport.SessionTap`/`DesignationTap`) — neither remains a bare
  passthrough. What is left uncaptured: mutation, undo, composition, lever,
  and query submissions remain bare passthroughs, so a mid-recording undo or
  a live lever write is still uncaptured. `replay.verify` proves
  POSE, not the authority regime that produced it. An unmerged partial fix for
  the session kind is the branch `lane/tapefidelity-partial`; read the ledger
  before merging it — its four-byte header taint bitset (which tracked addon
  reload/enable/disable across a recording) never reached this tree, so P5's
  ordering of `world.addon.mount`/`.unmount` supersedes that design without
  there being a bitset here to delete; `world.addon.reload`/`.enable`/
  `.disable` still apply synchronously outside the ordered domain and stay
  REFUSED (not bitset-tracked, and NOT part of CAS-REPLAY's scope) while a
  recording is armed.
- Phase 3 builds C (lease/ledger). Phase 3 build B (`Present` enforcement) is
  DISSOLVED, not deferred: `WorldCapability.Present`, the `geometry`/`overlay`
  channel kinds, and the whole `AddonLane` axis were DELETED (owner ruling,
  2026-08-02, the L5 landing) rather than built — there is no draw path left to
  gate and none is planned. The `AddonCapabilityMask` bit `Present` used is now
  a permanently reserved hole.
- No epoch interruption and no wall-clock deadline exist, so fuel is the only
  stop for a spinning guest. *(Verified at the code, not inherited from a
  census.)*
- The admit sequence's factoring, so a reload path and a boot path cannot drift.
- The should-fix tail from the review sweep — see the ledger.
- **What is NOT algebra, ruled by review and not to be re-attempted.** The
  authority decision is not a lattice: valid states are not closed under union
  (two individually-legal rows can conflict), adding an exclusive row REDUCES
  another principal's authority, grant transitions do not commute, and a
  concrete hold and a wildcard hold authorize identically while reporting
  different rules — so any quotient storing only "allowed" destroys state
  `world.why` and revocation need. Binding layer composition is not associative
  either (the deep page merge). What IS algebra: the fold (a closed form, now
  extracted) and reach∧ceiling (a meet-semilattice action, NOT a semiring
  module — Boolean OR does not distribute over integer addition on overlapping
  masks). The standing rule: **mathy is valuable here because it exposes laws,
  not because it removes `if`** — a branch-free expression that is harder to
  audit and carries no whole-domain proof is worse than the branch it replaced.

## TWO LANDMINES

*(The seat-narrowing landmine is DISCHARGED as of `a43765c5`. `c4ee338f` closed
the live verb; the boot door stayed open because document grants apply under
`Console`, which the administration check exempts. It is now closed the other
way: an authored ceiling is WITHHELD at boot while the row itself still applies,
so a document can pre-wire an addon's reach and consent stays a thing only a
seated human authors. The withholding is unconditional, and that is not a
shortcut — an occupancy test at boot would be dead code, because document grants
apply in the server's constructor before any seat is active. The hazard was never
a seat present at boot; it was the human INHERITING a pool on sitting down.)*

1. **The tape and the guest ABI are TWO re-key boundaries.** Never one key for
   both. First real test passed at `a43765c5`: the tape re-keyed `PKRV → PKRJ`
   while no ABI constant, wasm artifact, or module hash pin moved. P4-lean
   repeated the proof by re-keying `PKRE → PKRL` for shared leaves, VerbMask,
   peer generations, and lifecycle events without moving an ABI pin. Context
   routes repeated it again, `PKRL → PKRN`, for `Engage`'s widened shape
   (`ScreenIndex` → a `GrantSubject` target union, plus a `Capture` bool) — no
   ABI pin moved. CAS-REPLAY repeated it once more, `PKRP → PKRQ`, for the
   rebuild trio joining the tape as its own authority entry kind — again no
   ABI pin moved.
2. The trusted-press collapse (today's max-duration timer vs the ruled sum) is a
   real behavior change and must be STATED when press enters the fold.

## How we work

Census before design — every unit has had a premise overturned by one. Verify by
RUNNING, in the tree that MERGED rather than the tree that wrote it; a result you
did not run is reported, not held. One worktree per lane. Content search is
`puck search`, never grep.

**No new decision surface lands without its read-back verb, in the same change.**
`world.why` for authority, `player.channels` for the fold and held-image join: a decision nothing can
echo can only be asserted through downstream inference, which cannot separate the
decision from what consumed it. A verb also compares facts a reader never will —
the grant that named a channel its holder never emits was two known halves and no
comparison. And a command can be witnessed from more than one angle — the stdout
console, the in-game console, a recorded tape, a screen capture — so choose the
angle that observes the decision itself rather than its consequences.

**The one lesson worth memorizing**: saturating discriminators, unverified
compositions, and mislabeled inputs are one disease — *the check nobody ran was
one level up from the checks everybody ran*. Each survives a careful review of
its own layer, because the error is not in that layer. So evaluate BOTH rules on
any case chosen to discriminate them, and attack claimed passes as hard as gaps.
