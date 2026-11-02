# Milestone 1 acceptance laws

A unit law checks one system against its own contract. An acceptance law checks
that several systems agree when one user-visible event passes through all of
them: a world is rewound while someone watches it, a traveller crosses to
another authority and the trip is replayed, a machine is restored under the
bindings that read it. Each system can pass its own laws while the composition
still fails. These six laws are the proof that Milestone 1, the pipeline
foundation, holds together on the integration head, and they are written to
fail if any one system drifts from the others.

This page specifies the laws whose variants or arrangements are still unbuilt:
the claim, the exact scenario, the observable that decides it, the systems it
crosses, what it adds over the laws that already exist, where it lives, and
whether it needs a GPU. Law 1 (a rewind reaches an existing viewer) and the
repairs the design found (G1 to G5 and G7) are built, each held by laws of its
own in the test projects. Test requirements are suspended for the refactoring
campaign (AGENTS.md), so this page is dormant until the owner restores them; its
[open items](#open-items) are the inventory.

## How each law is stated

Every law below gives the same seven things, so it can be implemented without
re-deriving the design:

| Field | Meaning |
|---|---|
| Claim | The sentence the law proves, in the milestone's words. |
| Scenario | The fixture, the steps and the ticks. |
| Decided by | The observable that passes or fails it: a hash, a field, a console line, a refusal code. |
| Crosses | The systems the event passes through, with their entry points. |
| Not a duplicate of | The existing laws that cover a part, and what this law adds. |
| Lives in | The test project. |
| GPU | Whether a device is needed. None of the six is; each runs headless on the fake device the World tests already use. |

## Law 2: a crossing replays to the same traveller



**Claim.** A crossing, then record and replay: identity and arrival state match.

**Scenario.** Two rows under one instance host, with a mapped portal turned a
quarter turn between them, as the portal walk-through laws build. Both rows
record a replay tape, with the companion set recorded.

1. Ticks 0 to 8: seats 0 and 1 join row A, and seat 0, carrying a profile,
   walks into the door with nonzero planar velocity.
2. Tick 9: the transfer is minted, drained, committed and landed mapped.
3. Ticks 10 to 20: the traveller is driven on row B.
4. Recording stops.

A federated variant runs the same walk across the in-process federation harness.
A rollback variant refuses the commit, so the traveller stays and the source seat
is restored. A shared-identity variant seats seats 0 and 1 under one identity and
walks both into the door in one transfer: the contract is that a shared
identity is one detached binding, not one per seat.

**Decided by.**

- The tape set verifies, with its crossing verified.
- The replayed authoritative hash trace has no first divergence from the
  recorded one.
- After an isolated reland, the traveller's incarnation, epoch and
  `DepartedFrom`, its generation, its profile id and identity projection, its
  pose, yaw, and planar and vertical velocity, its travel turn, its catalog rig
  and the census all equal the live run's.
- Shared-identity variant: the source detaches one binding for the identity,
  the destination seats both travellers under it, and the replay matches the
  live run on both.
- Red leg: strip the arrival entry from the tape, and the replay diverges at the
  arrival tick.

**Crosses.**

- The crossing: `WorldInstanceHost.ApplyTransfer` (reservation, detach through
  `WorldPopulation.TryDetachSeatForTransfer`, `MapArrival`, commit or
  rollback through `RestoreDetachedSeat`) and the escrow landing in
  `WorldTransferEscrow`.
- The tape: `WorldReplayEntry.Arrival` at the destination, and the
  `Departure` entry (its `Restored` flag marks a rollback) the tape records
  where the authority decides it.
- Replay: `WorldReplaySnapshot`'s reland and re-driven departure, and the
  authoritative hash composition.

**Not a duplicate of.** The crossing replay laws verify a local crossing taped at
both authorities and a federated arrival replayed from the destination tape alone,
the crossing tape order laws replay a refused, aborted or rolled-back crossing
tick for tick, and the federation transfer replay laws compare pose, rig, turn,
grants and census across an arrival and a rollback. No law asserts the mobility
credential or the velocity after replay. This law adds them, on a walked, mapped
crossing with a profiled traveller, through both tapes.

**Lives in** `tests/Puck.World.Server.Tests/CrossingReplayTravellerLawTests.cs`.
**GPU:** none.

**As implemented.** The recording starts at the rows' first tick, so each tape
starts from its row's boot image. Row A's document authors seat 0's spawn a
short walk in front of its door. A recording armed later would start from a
checkpoint of its row instead (`ReplayArmingLawTests`). The isolated reland is the companion tape's own
re-drive: at every recorded tick the replayed destination is read through
`DriveTraces`' tick observer and compared with the live destination, field for
field. A reservation that mints the credential is not red on this walk, because
its reservation and detach run in one drain; G3's own laws witness it
(`CrossingTapeOrderLawTests.Reservation.cs`).

Two identity sequences join the law in `CrossingIdentityPrivacyLawTests`, both
on the rule that an id names one live object:

- A pull of the owned identity between two home arrivals. Outside a recording a
  pull replaces the identity in place, so bound seats follow it
  (`OwnedWorldPullLawTests`). While a tape records, the pull refuses by name
  (`PullWhileRecording`), because a tape never carries an owned document, and
  both seats keep the one identity the re-drive binds them to.
- A fork's seat switch, then a home arrival under the same id. Every switch and
  home adoption a re-drive binds under one id shares the escrow's one detached
  identity for it, as the live seats share the owned object.

**The contract it proves:** the reservation reads the mobility credential
without minting it, and only the departure's detach mints it, which the replay's
re-driven departure also calls (G3, `CrossingTapeOrderLawTests`).
The authoritative hash folds the credential every recorded tick, so any
divergence shows in the hash trace.

## Law 3: a restored machine runs on as if never restored

**Claim.** A machine checkpoint restore, then unchanged onChange bindings:
restored execution equals uninterrupted execution.

**Scenario.** The named machine memory laws' document, with a test runtime that
also implements `IMachineCheckpointRuntime`. Two servers, A and B, boot from it.

1. A steps once, and its Write binding writes 99 to the guest.
2. The guest sets its own value to 7, and A steps once more.
3. A's checkpoint is captured, encoded, decoded and restored into B.
4. Both step three ticks.

A Read leg runs the same steps with a Read binding. A collision leg runs the Write
steps over two machines with two bindings each, named so that every key part is
shared with another binding: the same binding name on both machines, and the
same ordinal in both. Each guest edits each bound value differently, so a memo
restored under the wrong key rewrites the wrong value. A single binding cannot
see that. A fourth leg seeks back with
`world.history` over a machine with bindings and compares the result with an
uninterrupted run. A seek's own verdict leaves machine cores outside its match
proof and says so (`MachineCoresOutsideProof`), so this leg compares the guest
state itself.

**Decided by.**

- Write leg: the guest value (7 in both), the runtime's write count, the
  runtime state bytes and the document bytes are equal in A and B.
- Read leg: A and B journal the same number of mutations, and their
  authoritative hashes are equal.
- Collision leg: every guest value, per machine and binding, is equal in A and
  B, and so are both runtimes' write counts.

**Crosses.**

- The binding memo: `WorldServer.SyncNamedMachineMemory` and its per-binding
  observations, which skip an onChange write whose value was already seen.
- The machine checkpoint: `WorldMachineHost` capture and restore, over
  `IMachineCheckpointRuntime`.
- The world checkpoint: `WorldPersistence` capture and restore, and the
  mutation journal.

The contract it proves:

- The server checkpoint carries the binding observations
  (`WorldMachineBindingEntry` per machine and binding, only those the next sync
  would keep, in ordinal order).
- A restore validates every entry against the restored document before any
  change, then replaces the memo, never merging it.
- This holds on every restore door: in-place and load-door history seeks,
  `FromCheckpoint` (a silo activation, the history shadow), and `replay.drive`,
  whose unstepped shadow starts with an empty memo
  (G5, `MachineBindingCheckpointLawTests`).

**Not a duplicate of.** The cartridge laws prove that a world checkpoint
preserves a stepped machine and its continuation, but in a world with no memory
bindings and without comparing the journal. The named machine memory laws cover
write policy and replacement, but not a checkpoint.
`MachineBindingCheckpointLawTests` crosses the binding memo with a checkpoint
for Write bindings: a history seek over a running machine and a server restored
through `FromCheckpoint` leave the byte the guest's program rewrote alone, as the
uninterrupted run does, and the checkpoint carries the memo in binding order.
This law adds the Read leg, the collision leg over shared names and ordinals,
and the comparison of the journal and the document bytes.

**Lives in** `tests/Puck.World.Server.Tests`. **GPU:** none.

**As implemented.** One theory runs the write and read legs, and both carry the
collision: two machines, `left` and `right`, each bind `value` and `other` at the
same ordinals, and each guest edits each bound value differently. B restores A's
checkpoint in place (`WorldServer.RestoreCheckpoint`). The seek leg's in-place
door is G5's own law (`MachineBindingCheckpointLawTests`, a seek over a running
machine). The load-door seek and `replay.drive` doors have no leg yet.

**Witness for G5:** the law is red when the restore drops the captured memo. The
write leg's guests are rewritten to the world's values (11 where the
uninterrupted run holds 7), and the read leg journals four mirror writes the
uninterrupted run never makes. Restoring each observation under its binding
name alone is red too: the left machine takes the right machine's memo and is
rewritten.

## Law 4: a displayed source survives the screen's changes

**Claim.** A displayed source survives pause, retargeting and a destination
change.

**Scenario.** The offscreen boot of the uploaded-sources canary fixture, which
the composition laws already build on the fake device: screen 0 shows a
machine's video, screen 1 a test pattern. The scenario reads screen 1's line in
`world.screens` and the camera view census, then applies, reading both after
each step:

1. pause the pipeline node, then the world rate, step, and resume;
2. retarget screen 1 with `screen.source` to a camera view, then to a QR source,
   then back to its original source;
3. change screen 1's route input from Presentation to Simulation, then back.

The route leg runs twice: once from the baseline, and once while the camera-view
retarget is in force, so a route change that drops the screen's override is
seen rather than masked by the baseline's own source.

**Decided by.**

- Each retarget takes effect: after it, screen 1's mapping segment (the source
  instance's name and handle) names the new source, not the baseline. A
  retarget that changes nothing fails here.
- Returning restores the baseline: after the return, the mapping segment equals
  the baseline.
- Through the route leg, the mapping segment is unchanged: the baseline's when
  run from the baseline, and the camera view's when run under the retarget.
- The `input:` field differs only while the route is changed.
- The view census returns to its baseline count, so no registration is left
  dangling.

**Crosses.**

- The screen binder: `WorldScreenBinder.ApplySource`, `ReconcileScreens` and
  its live binds.
- The source registry: `WorldScreenMappingSet.Reconcile` over
  `WorldSourceInstances`.
- Camera view release, pipeline and world-rate pause, and the screen route's
  destination.

**Not a duplicate of.** The mapping and source-instance laws cover publishing a
live bind, instance naming, and reordering or removing screens. The machine
lifetime laws cover a stopped instance and a retarget tearing down its cable, and
the render-graph laws cover a paused instance's standing image. Nothing composes
pause, a retarget there and back, and a destination change, or checks that the
census returns to its baseline.

**Lives in** `tests/Puck.World.Tests`. **GPU:** none for this law. A pixel check
of the standing image belongs to the existing uploaded-sources canary.

**What "pause" means.** Stopping a named machine is not a pause here: by design
its video output goes away, and the screen shows unbound glass until it resumes
with the same output. The law pauses the pipeline node and the world rate, which
keep the source bound.

**What "back" means.** `screen.source` has no "row" kind, so the only way back
is an explicit bind of the original source, which proves a rebind, not a return
to what the row authored. The law uses the return seam G7 added:
`screen.source <index> row` drops the live bind and the screen shows its row's
own source again (gap G7, `screen.source <index> row`).

**As implemented.** The fixture boots headless, where the offscreen boot's
composition is unavailable on a host without a GPU device. Headless, only screen
0, the machine's video, publishes a mapping with a known extent, so the law runs
on screen 0 and reads the mapping's source instance, the first token of its
mapping segment. A pipeline node has no rendered instance to pause headless, and
only the render root configures the views, so the pipeline pause, the
camera-view retarget and a census that moves are the offscreen canary
`displayed-source-render-root`, which requires a GPU. `pipeline.time` addresses
only a source-driven graph row, and the fixture's graphs are packages while its
screens show producers, so the canary layers the feedback pipeline over the
fixture and pauses that;
the headless law still reads the census before and after. The
retarget is a QR source, and under it the route leg also reads the QR's
authoring back, which proves the live bind survived. Red legs: a `row` form that
leaves the live bind fails the return; a route change that drops the live bind
fails the retargeted route leg.

**What the destination change does not prove.** A Simulation route maps through
the row's own mapping, not the live bind, so the change proves only that the
binding survives. It does not prove that a simulation hit sees a
presentation-only retarget. Passthrough is refused for document rows, so only
Presentation and Simulation are cycled.

## Law 5: a federated commit keeps private profile data at home

**Claim.** A federated commit never carries private profile data to the
destination.

**Scenario.** The owned identity document the identity projection wire laws
build, which already carries private markers. It also gets a `chat$inbox` cell,
a HUD panel text and a binding overlay, each with its own marker.

- Codec leg: take the commit members the source logged in its departure
  record, the members it then sends, encode them with
  `WorldFederationCodec.EncodeCommit`, and decode them.
- End-to-end leg: seat the identity on the federation harness's source,
  walk it across to the remote destination, and drain.
- A colocated twin runs the same crossing between two rows of one host.

**Decided by.**

- None of the private markers, and no embedded `puck.world.definition.v1`
  document, appear in the encoded commit bytes. The traveller's id does appear,
  as a control.
- After decoding and after landing, the destination seat's profile has no
  document, no bindings and no HUD.
- The federated and colocated crossings leave the same profile projection.

**Crosses.**

- The profile: `WorldIdentity` and its allowlist projection
  `WorldIdentityProjection` (`Project()`), whose contract keeps chat, controller
  history, bindings, cross-game rows and the private HUD at home.
- The commit: `WorldTransferCommitMember` and the federation codec's commit
  member.
- Intake: `WorldPeerHost` serving the commit, `TryDecodeCommit`, and the escrow
  seating the profile.
- The contract it proves is the federation identity privacy lane's: only a
  `WorldIdentityProjection` crosses a seam, on every path (reservation, commit,
  a commit retried after a source restart, colocated crossings, the crossing
  log, the arrival tape, checkpoint leaves), written through the one wire form
  `WorldIdentityProjectionWire`, and a destination never saves a foreign
  identity.

**Not a duplicate of.** The identity projection wire laws
(`IdentityProjectionWireLawTests`) prove that the reservation carries the
projection and never the owned document, and `CrossingIdentityPrivacyLawTests`
proves each seam on its own: a remote commit, a colocated crossing and a retried
commit install the projection and write no private bytes into the destination's
crossing log, arrival tape or checkpoint. No law reads the commit's own encoded
bytes (`WorldFederationCodec.EncodeCommit`). This law adds the one end-to-end
case the milestone names: a commit that crosses, federated and colocated, read
at the bytes on the wire and at the seat it lands in.

**Lives in** `tests/Puck.World.Presentation.Tests/FederatedCommitPrivacyLawTests.cs`. The
federated leg skips itself on a host without QUIC; the federation harness has no
such skip, so the harness's own laws fail there instead. **GPU:** none.

**Witness for G4:** the law is red when the projection carries the owned
document in its name, the one free text a projection has: the commit bytes then
hold the private row. A projection's records cannot carry the document's world
rows at all, because the projection wire refuses them.

## Law 6: an unsupported operation changes nothing before it refuses

**Claim.** Each intentionally unsupported operation refuses before any partial
state change.

**Scenario.** One theory over every refusal the engine classifies as an
intentionally unsupported operation. The classification lives on the refusal in
code (`RefusalAttribute.Unsupported`), and the theory's rows are
enumerated from it, so a new unsupported operation joins the law by being
declared, and one with no arrangement fails the law by name. Each row carries its
name, how to arrange it, the operation, and the refusal it expects. For each row,
two servers boot from one document: A runs the operation at tick N and then
steps, and B only steps. The set as designed, which the classification must
reproduce:

| Operation | Refusal |
|---|---|
| An op-id-preserving mutation on a basic link | `world.transport.operation-metadata-unsupported` |
| An external transfer into a closed rewind group | "closed rewind group refuses an external transfer" |
| A machine operation while recording | `MachineOperationStatus.Refused` |
| A machine operation on a provider without operations | `MachineOperationStatus.Unsupported` |
| An undo past the journal horizon | "undo refused: …" |
| A checkpoint while unsupported addons, screen operations, or coupled links are live | the checkpoint's named refusal |
| A fact on an anonymous seat | `WorldRuleEffectRefusal.IdentityUnbound` |
| A player-scope HUD replace layer | `HudRefusal.SeatPanelReplaceRefused` |
| A vector-cell effect kind | `RuleRefusal.VectorEffectNotAdmitted` |
| An addon mutation it never requested | `AddonMutateRefusal.NotRequested` |
| A transfer by an undeclared producer | the transfer's named refusal |
| A history seek while a session is live | `world.history: seek refused — …` |
| A transfer of a rigid body | the transfer's named refusal |
| A transfer while the traveller carries another body | the transfer's named refusal |

**Decided by.**

- The refusal matches the expected code, or the expected text for a refusal
  that has no code yet.
- A's and B's document bytes are identical.
- A's and B's authoritative hashes (`WorldStateHashComposition.HashAuthoritative`)
  are equal.
- The mutation journal count is unchanged.
- Where a checkpoint can be taken, the encoded checkpoints are byte-identical.
- State a checkpoint does not capture is compared through witnesses, because
  the rows most likely to leak are the ones whose checkpoint capture refuses.
  Each row declares the runtime surfaces it touches, and the law compares each:
  a machine's registers and memory through its runtime's own capture (a digest
  of `IMachineCheckpointRuntime` state, or the runtime's register read), and a
  link's pending transport work through its outbox depth and pending operation
  ids. A row that touches a surface with neither a checkpoint leaf nor a witness
  fails the law, rather than passing with that surface unread.
- Each row also runs the legal variant of its operation, with the shared
  refusal-with-control helper, so the observable is shown to move when the
  operation is allowed.

**Crosses.** The refusal machinery (`RefusalAttribute` and `RefusalKind`, the
`RefusalCatalog` behind `world.refusals`, and submission refusal codes), the
prepare and install split that makes a mutation atomic, and every system in the
catalogue.

**Not a duplicate of.** The all-or-nothing mutation laws, the batch compose law,
the machine hardware and operation laws, the checkpoint laws and the addon
prepare gate each prove one refusal leaves state alone, mostly by checking one
field or the verdict. None compares a whole-state hash with a twin, and nothing
enumerates the unsupported operations. This law adds both: the twin comparison,
with witnesses for what a checkpoint misses, over a set read from the code.

**Lives in** `tests/Puck.World.Tests/UnsupportedOperationLawTests.cs`.
**GPU:** none.

**As implemented.** G6's classification is `RefusalAttribute.Unsupported`,
carried on `RefusalCatalogEntry` and tagged `, unsupported` in `world.refusals`.
The theory's rows are the classified set, and an arrangement names how both
twins are arranged, the operation, a witness over what the checkpoint does not
capture, and the legal variant on its own twin. A classified refusal without an
arrangement fails by name, and an arrangement naming no classified refusal fails
`EveryArrangementNamesAClassifiedRefusal`. Six refusals are classified so far:
`replay.record/StartNotCheckpointable`, `storage.pull/PullWhileRecording`,
`world.undo/PastHorizon`, `machine.operation/WhileRecording`,
`machine.operation/ProviderWithoutOperations` and
`state.rule.compile/VectorEffectNotAdmitted`. The machine-operation rows witness
the operated machine's generation and configuration; the provider row's twins
run an engine without operations and its legal variant one with them. The
vector row submits a rule adding to a vector cell, and its legal variant adds to
an integer cell. `HudRefusal.SeatPanelReplaceRefused` refuses only through
`identity.hud`, which needs a joined seat's roster profile, so its arrangement
needs a twin that can join one. The rest
of the table above joins as each refusal gains a code and an arrangement;
`IdentityUnbound` refuses inside a rule firing whose scope rewinds it, so its
arrangement needs a gate the twins can hold apart.

**Scope.** Delivered effect arms (a cue, a pose, a body motion, a rigid impulse,
a field paint, a save) are outside the atomic promise by contract: they fire
after the commit, and a refusal there is one counted `IrreversibleArmFailed`
that undoes nothing. They are not classified as unsupported operations, and the
classification says so for each of their refusals.



## Open items

The observable invariants above have six laws and the G1 to G7 repairs
(G6 as `RefusalAttribute.Unsupported`). The following arrangements remain
uncovered; this is an inventory of scenarios rather than an implementation
order. Checkpoints already capture scoped replays and retained undo, and restore
both through `WorldPersistence.Checkpoint.cs`; rewind history is not a blanket
checkpoint refusal.

- Law 6: arrange `HudRefusal.SeatPanelReplaceRefused`, which refuses only
  through `identity.hud`, so the twins must join a seat's roster profile.
- Law 6: arrange `AddonMutateRefusal.NotRequested`, which needs a guest addon
  whose manifest never requested `Mutate`.
- Law 6: give codes and arrangements to the remaining checkpoint refusals
  (unsupported addons, screen operations, and coupled links).
- Law 6: give codes and arrangements to the closed rewind group's external
  transfer, the undeclared producer's, the rigid body's and the carrying
  traveller's transfers.
- Law 6: give codes and arrangements to a history seek while a session is live,
  and to the op-id-preserving mutation on a basic link.
- Law 6: arrange `WorldRuleEffectRefusal.IdentityUnbound`, which refuses inside
  a rule firing whose scope rewinds it, so it needs a gate the twins can hold
  apart.
- Law 2: the federated, rollback and shared-identity variants of the crossing
  replay.
- Law 3: the load-door and `replay.drive` doors.
- Law 4 and law 5: the GPU canary `displayed-source-render-root` and law 5's
  federated leg, owed on a GPU host with QUIC.
