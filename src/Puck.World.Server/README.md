# Puck.World.Server — the authoritative world runtime

This project is the server half of the world game: the entity table, the tick
step, the capability-grant authority model, the QUIC peer transport,
the addon host seam, player profiles and their storage, and the deterministic
replay codec. It consumes
the document and protocol shapes from
[`Puck.World.Schema`](../Puck.World.Schema/README.md) and
[`Puck.World.Protocol`](../Puck.World.Protocol/README.md) and knows nothing about
rendering or input devices — the same architecture lane profile that fences
those two projects (see `build/Architecture.props`) denies this project every
presentation and backend assembly. The composition root that hosts it is
[`Puck.World`](../Puck.World/README.md).

Project references: `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.Networking`,
`Puck.Storage`, `Puck.Hosting`, and — through the schema — `Puck.State`, whose
reader, catalog, topologies, and rule evaluator the tick runs; `WorldServer` is
the evaluator's host (`WorldServer.RuleHost.cs`: the mutation door, preflight
scopes, the world's effect arms — over a value frame, `WorldServer.RuleFrame.cs`,
see below), and interactions and decisions stay here as
the rule kinds only a world evaluates. The addon guest runtime itself is
[`Puck.World.Addons`](../Puck.World.Addons/README.md), which references this
project rather than the reverse — see `IWorldAddonHost` below.

Recorded providers and durable external operations share the engine's authority
and replay paths. Operators start with [declarative composition](ExtensionConfiguration.md).
Developers can use [Hosting service extensions](ExtensionHosting.md)
for scoped clients and the shared worker. See [Extensions and external operations](Extensions.md) for
the hosting contract, operation recovery, and the distinction between a
deterministic WASM guest and a provider whose contributions are recorded.
The optional [Azure resource extension](../Puck.World.Azure/README.md) supplies
an ARM provider without adding Azure management dependencies to the server.

## The tick (`WorldServer.cs`)

`WorldServer.Step` advances one exact fixed tick, in a pinned order its own
XML documentation states: admit queued recorded-extension contributions,
then tick the mounted addon guests
(`IWorldAddonHost.TickAddons` — decodes and validates, applies nothing) →
drain the buffered live edits (mutations and whole-document swaps) → drain the
buffered intents → apply the guests' contributions
(`IWorldAddonHost.ApplyContributions`) → fold each human-occupied body's
contributions (`FoldChannelContributions`) → settle per-body contention →
advance every body → resolve the guests' reads
(`IWorldAddonHost.ResolveReads`) → deliver the tick's `WorldSnapshot`.

A quiet tick's cost splits across two shapes: the animating population's own
contact solves against the compiled solid field
(`WorldSolidField`/`FixedFieldContactSolver`, walked once per awake body's
`Advance`), and every rule operand's row-version/value read through
`WorldDefinition.StateCatalog`. `WorldStateSection` owns an immutable copy of
every list it carries (`World`/`Body`/`Identity`/`Lattices`), taken at
construction and again on every `with`, so a candidate that changes shape is
always a new `StateRaw` reference — the batch compose path
(`WorldServer.MutationCompose.Batch.cs`) commits each cell write through
`WithWorldState` rather than editing a shared row list in place, and
`WorldStateCatalogLawTests` pins that an in-place edit of a caller's own array
can never reach an already-built section. `WorldDefinition.StateCatalog`
therefore keys its compiled product to the `StateRaw` reference
(`ConditionalWeakTable`) and trusts a warm hit without re-walking the section's
shape — the walk runs once, at the reference's first read, never again for
that reference. The contact-solve share has no per-call shortcut: it is real
per-body physics work against a program sized by every district's solid
geometry. `puck bench world`'s "shipped world: idle tick (median)" row and
`tests/Puck.World.Tests/HandleTickPathLawTests.cs` are the read-backs.

Every non-intent submission arrives as one `SubmissionEnvelope` through
`WorldServer.Submit` — a single ordered domain, drained in submission order.
Enqueue and drain both run under the same authority gate `Step` and every
federation operation hold (`WorldServer.EnqueueOrdered` is the one door), so the
queue and its reentrancy guard are single-threaded state. A drain reached
without that gate can be skipped by another thread's in-flight drain, which
would leave an already-applied population change — an admitted arrival — standing
without the grant rows its own queued event carries.
The same queue also carries the server-authored `PeerAdmitted` and
`PeerDisconnected` entries; clients cannot submit those events. They apply
through the population/grant doors and are exposed to the replay tape only
after their point of effect.
On the in-process loopback that drain runs inline on the tick thread before
the `Submit*` call returns, so commands, grants, session requests, and queries
apply synchronously at submit, while definition swaps and mutations buffer to
the tick boundary. The practical consequence for scripts: within one stdin
batch, a grant submitted before a command is visible to that command, and a
mutation followed by an `Immediate` read is serialized by the console's drain
barrier (see the console section of
[`Puck.World`'s README](../Puck.World/README.md)). Results return through
typed completions (`WorldSubmissionResult`), and deliveries fan out through
`WorldOutputHub.cs`, which supports multiple subscribed sinks. A sink's live
definition delivery is `DeliverDefinition` after a shape change or
`DeliverState` after a value-only write — see `Puck.World.Protocol`'s
`IClientSink`.

## Rule effects land on a frame (`WorldServer.RuleHost.cs`, `WorldServer.RuleFrame.cs`)

`EvaluateWorldRules` loads a `StateFrame` (`Puck.State`) from the installed
document once at the start of the tick's rule evaluation and holds it active
for `IRuleReader.Store` for that call only — a read from anywhere else (flock
affinity among them) falls back to reading the installed document directly,
which is the frame-commit contract: reads outside a tick's own rule
evaluation never see a mid-evaluation frame the tick's own fold has not yet
installed. A cell write, push, board combine, write-set, or a transfer by
first/last/key answers straight from the frame's value array — no document
compose — and queues its mapped `WorldMutation` on one flat, firing-order list
(`m_ruleFrameMutations`) that a rejected preflight scope truncates back to its
own start. Placement and HUD effects join that same ordered list after their
candidate passes the ordinary document checks. At the end of the tick's evaluation, `FoldRuleFrameMutations`
installs that list as ONE mutation (a single member installs as itself,
several as a `Batch`) through the ordinary door — one compose, one
touched-row validation, one journal entry, one delivery — replayed from the
document the tick actually started on, never from whatever the frame's own
cross-row path left `m_definition` at mid-tick. A tick that wrote nothing
folds nothing.

Shapes a value frame cannot answer — a text cell (the frame holds only
raw integers), a cell removal, a generator draw (advances a row's own
`DrawCursor`/`DrawnMasks`, not a cell value), a shuffle, and a transfer by
`Random` or `Slice` (both need the generator stream `First`/`Last`/`Key`
never touch) — and minting a new cell of a keyed row (a frame's layout is
fixed-size once built) all fall to `TryApplyCrossRowStateMutation`: it
replays this tick's own queued mutations plus the new one as one
`WorldMutation.Batch` through the batch workspace below, from the tick's
starting document, and re-derives the frame from the result so a later
same-tick read (another rule's gate, or the very next effect) sees it. A cell
write or removal among the replayed members shares that one workspace row-list
copy instead of paying for a fresh whole-document compose per member, so a
tick with many cross-row writes (a Klondike deal) no longer recomposes its
whole prefix once per already-queued member.
Document effects use the same queued prefix when preparing their candidate,
so a later effect sees earlier numeric writes as well as earlier placement or
HUD changes. Nothing here installs for real — the tick's own fold still owns that.

A preflight scope (an explicit `transaction` effect, or the transient
validate-then-fire pass every top-level state effect takes before it commits
for real) tracks the frame's own journal mark, its slice of the mutation
list, and a stamp of how many cross-row reloads have happened so far.
Closing the scope compares that stamp: unchanged, an ordinary journal rewind
(or commit) suffices; changed, the frame is re-derived from `m_definition` as
the scope's own close already left it, since a cross-row reload's whole-frame
`Load` bypasses the journal and a plain rewind cannot undo it. Committing a
scope keeps its speculative candidate and ordered writes; it does not install
a separate document half. Rejecting a transaction discards its state and
placement changes together; authored transactions cannot contain another transaction.
Compiled handles continue using the installed program's catalog during speculative
document-value refreshes, because rule effects cannot change row declarations.

`WorldMutation.Batch` composes as one edit
(`WorldServer.MutationCompose.Batch.cs`). A cell write or removal lands in a
workspace — one private copy of the row list, written in place by every such
member — while a member of any other kind composes through its own arm and
the next cell write opens a fresh workspace over its result. The running
candidate is re-seated over the workspace (`SyncWorkspace`) only where
something other than a cell write needs it — before a non-cell member
composes, before a document-value rehydration actually reads it back, and
once more when the batch finishes — never once per placement:
`WorldStateSection` owns an immutable copy of its own row list, so handing the
workspace to it is the one point that copy is paid for, and a run of
independent cell writes pays it once for the whole call. The document-value
refresh runs against one referenced-row set per batch
(`WorldStateDocumentValues.CollectReferencedRows`), collected on the first
state member and reused until a member that can add or drop a reference (a
whole-row write, an edit to another section) drops it; a member whose row is
in the set rehydrates (`WorldStateDocumentValues.TryRehydrate`) exactly where
a one-by-one compose would. A batch installs the same document its members
reach one by one, a member that reads a row an earlier member wrote included
(`tests/Puck.World.Tests/BatchComposeLawTests.cs`), and one refused member
refuses the whole batch.

## Local flock steering

`ProduceFlockIntent` consumes the assigned kit's `producers.<name>.flock`
profile. Range, cone, line of sight, candidate/neighbor limits, perception
cadence, and separation/alignment/cohesion/goal/inertia weights are authored
data. Tangent mode uses the body's support normal; volume mode requires a
motion program that consumes vertical input.

`WorldPopulation` freezes positions, orientations, and prior-step travel
before any body advances. The deterministic spatial query limits inspected
candidates, not only retained neighbors. A budget-limited result is nearest
within a rotating sample, not a promise of globally nearest neighbors.
Grids use power-of-two range levels and rebuild lazily from the frozen image;
adding an unused long-range profile does not alter a short-range sample.
Perception updates cache the unclamped neighbor contribution and any sensed
target's observed position. Target selection and neighbors share one candidate
budget over the larger sensing range, with their own scope/cone/sight filters.
Between samples, sensed targets do not silently acquire live position updates.
Explicit designations, route waypoints, headings, and tangent frames blend every
simulation step. Checkpoints carry both caches, cadence residue, observer-local
sample ordinal, and every slot's generation. Slot reuse cannot transfer an old
observation to a new occupant.
An unchanged producer binding keeps its perception cadence across rebuilds;
changing its profile or target source refreshes perception on its next step.

Optional `cohesionAffinity` and `alignmentAffinity` expressions assign independent
relative weights to each retained neighbor: "stay near this companion" need not
mean "follow its heading." Both use the ordinary Fixed postfix evaluator, with
`left` bound to the observer and `right` to the neighbor (`$left`/`$right` for
state-cell keys). Only state-backed facts are admitted; live body,
channel, navigation, and machine facts could change midway through movement and
make an observer depend on body iteration order. A rule-authored belief row
therefore affects the next eligible perception sample, not movement
that already happened. State handles recompile on document
installation even when the population does not rebuild.

An omitted affinity is one. Results clamp to [0,1]; arithmetic failure supplies
zero and increments the failure counter. The shared expression arithmetic refuses
zero divisors and overflow without allocating exceptions; Fixed products and
quotients retain nearest/ties-to-even rounding, and Int division truncates toward
zero. Affinity zero excludes that neighbor
from the corresponding weighted mean, never from separation or collision. All
retained neighbors still spend the same perception budget; the engine does not
scan farther until it finds a friend. Weighted means are normalized before the
outer flock weights apply, so equally scaling every nonzero affinity does not
reduce term strength. An unwritten belief row reads its authored initial value;
nothing creates reciprocal friendship.
See the [authoring example](../Puck.World.Schema/README.md#keyed-belief-rows-and-flock-affinities).

`world.flock` describes profiles and work; `world.budget` repeats the structural
cost, including movement-domain checks/refusals, affinity evaluations/failures,
and conservatively charged affinity work. Affinity expressions share the world
rule work ceiling, including indirect scans and the worst-case simultaneous
initial sample of every body. No counter affects decisions.
An optional `movementDomain` names a volume or medium navigation domain. Its
root-centered `agentRadius` must enclose the kit's collider, including local
offsets. Every integrated locomotion step is checked continuously against that
domain, solid clearance, and live medium containment before it commits. A refused
step stops translational momentum without teleporting; the constraint ends when
the producer ends. It is not pathfinding or recovery: a displaced/outside creature
may remain stranded. Later impulse overlays, body contacts, tethers, and authority
teleports are separate physical/authority operations, not silently cancelled by
this locomotion constraint. Surface-domain constraints are not implemented here.
Flock weights alone imply no containment, remembered belief, shared routes, or
obstacle avoidance.

## Gravity fields

`WorldGravityField` is the one authoritative gravity evaluator. It gathers
bodies in stable entity order, runs the selected global solver once, adds the
uniform acceleration, then folds matching compiled local areas in stable
priority/authored order. Local areas remain fixed-point and placement-relative:
static rows use authored pose, while attached rows refresh through
`WorldPlacementAttachment.TryResolve` before the tick's solve. Per-entity
participation is separate from vector magnitude, so a zero Replace, exact
cancellation, or radial center suppresses kit fallback, while a body outside
every area in an areas-only document retains it. The same participation verdict
gates gravity-derived ambient orientation when a body crosses an area edge and,
under the surface-following body-frame policy, gates contact-normal orientation.
`gravitationalConstant > 0` runs the global body-source solve even with no
static attractors. Composition additions saturate per Q48.16 component instead
of wrapping, and a later Replace remains an ordinary assignment.

## Navigation

The A*/shared-search kernel — `Puck.Physics.Navigation.NavigationRuntime` — is
Physics vocabulary; see [its README](../Puck.Physics/README.md#-navigation-kernel-navigation)
for the algorithm, the swept-sphere edge proof, and the shared-tree checkpoint
shapes. `WorldPopulation` owns the document seam: `CompileNavigationDomains`
(`WorldPopulation.Navigation.cs`) compiles each authored `navigation.domains`
row into a `NavigationDomainInput` once at resolve time, mapping the
document's own kind/connectivity enums onto the kernel's, and
`NavigationMediumFieldAdapter` bridges `FieldLattice` to the kernel's
`INavigationMediumField` seam so a medium-kind domain never needs the
lattice's own representation.

A domain's own occupancy sweep and edge bake are lazy: `Domain`'s constructor
only sizes its workspace arrays, and the kernel samples the solid field the
first time something genuinely needs the answer — a route request, an
off-grid locomotion check, a direct `WalkableCellCount` read, or a live-edit
reconcile proving retention. A domain nobody ever routes through never
sweeps the field; `puck bench world`'s construction row measures this.

A domain's optional `parent` resolves its local origin and X/Z axes through the placement frame compilation;
surface probes remain vertical. `world.navigation` includes the resolved origin and yaw. Moving a court thus
rebuilds the domain in its new frame. This is not incremental SDF invalidation: a domain is retained only when the
replacement solid query proves every cell, clearance probe, and static edge query unchanged; later off-grid segment
checks use the replacement query, and medium domains require the same field provider and synchronized revision.
Capacity changes rebuild as well. The [granary authoring example](../Puck.World/Assets/worlds/modules/README.md#grow-and-rearrange-the-court)
combines these frames with preserved dealt instances and guarded reflow.

Changing a navigation definition, or an edit whose proof affects that domain,
clears a body's local route cache; a retained domain keeps its route and shared
search workspace. Clearing designations, switching producers, or transferring authority still clears a body's local route cache
(`BodyNavigationState`); `WorldPopulationNavigationCheckpoint` carries the
domain, goal cell, waypoint cursor, status, and expanded-node count, and
`WorldAuthorityCheckpointCodec` validates every domain, node, waypoint,
status, and budget before restore. The authoritative state hash includes
producer-domain and route state. `world.navigation`, `body.targets`, and
`world.budget` expose occupancy, state, expansions, the current followers'
simultaneous-replan ceiling, and fixed workspace bytes; `$nav:` rule facts
(`hasPath`, `active`, `arrived`, `remaining`, `pending`, `unreachable`) read
the same live status.

**Mutations, the journal, and undo.** A `WorldMutation` applies by composing a
candidate document, revalidating the WHOLE document through
`WorldDefinitionValidator`, and only then swapping, journaling, and rebuilding
the changed section's derived state; a failure rejects loudly and changes
nothing. The journal is the undo engine: `world.undo` restores the loaded base
definition and deterministically replays the journal minus its tail through
the same apply path — no per-mutation inverse exists.
`world.save` writes a canonical session snapshot and compacts the journal (the
saved definition becomes the new base). `world.reset`/`world.load`/`world.reload` are ONE
rebuild-and-swap mechanism (`WorldServer.ApplyRebuild`) over three document
sources — the server's own base, a different file, or a re-read of the
current origin — that also wipes and re-seeds the ENTIRE runtime grant table
(`WorldGrants.Reset`, replaying only the new document's own `Grants` section
plus every admitted peer connection's re-minted admission grant; every other
live `world.grant` acquisition drops). The `dirty` count in `world.status` IS
the journal length.

`host.journalDepth` bounds the undo horizon: `0` (the default) leaves the
journal growing for the life of the process, exactly as before the field
existed; a positive depth makes `WorldServer.EnforceJournalDepth` fold the
oldest entries past the horizon forward into the base the journal already
keeps — the same per-entry compose-and-rebase `ApplyUndo` performs, run
forward instead of backward — once per completed tick
(`WorldServerStepShell.Step`), so the journal never grows past the depth and
`world.undo` can never reach further back than what it kept (it refuses by
name, naming the depth, rather than silently undoing fewer than asked).
`world.status` echoes the authored depth.

World-rule failures accumulate in the evaluator's ledger, one entry per
category. The first occurrence is narrated; repeated Level-rule failures only
increment their counter. `world.rule.failures` reports the count and latest tick/rule/effect/reason.
Rule/interaction installation is also guarded by a static aggregate work budget,
reported beside evaluation slots in `world.budget`; dynamic body-index keys use
a prebuilt string cache on the evaluation path.

**Lifetime sweeps.** Four per-tick passes run side by side at the end of
`WorldServer.StepCore`, each firing ORDINARY mutations under
`WorldPrincipal.World`'s structural exemption so recovery is journalled rather
than a bespoke erase: `ReclaimExpiredEscrows` (an unaccepted ownership offer),
`WorldTransferEscrow.ReclaimExpired` (an unclaimed destination reservation),
`SweepContributionTenure` (`WorldServer.Contributions.cs` — a presence-tenure
contribution slot whose watched `adjacencies` row has read dropped past the
slot's own `graceSeconds`), and `WorldPopulation.ReclaimExpiredParks` (a
disconnected seat or peer past its reconnect grace). `SweepDeadlines`
(`WorldDeadlineTable.cs`) runs the first three as one step beside rules; parks
stays a separate call after `SweepPlacementResponses`
(`WorldServer.Responses.cs` — right after `StepFields`, so it reads this
tick's own lattice writes: the first `WorldPlacementResponse` entry whose
condition holds at a placement's coupled cell becomes its prototype) and
`SweepPlacementDeals` (`WorldServer.Deals.cs` — right after the response
sweep, so the rule frame has folded and a cell a rule wrote this tick deals on
this tick: each placement carrying a `WorldPlacementDeal` is a template whose
children follow its keyed row, one `UpsertPlacement`/`RemovePlacement` per
arriving, leaving, or re-variant cell, folded into one `Batch` under
`WorldPrincipal.World`; a child keeps the region offset it was dealt and a new
cell takes the lowest free one; the sweep returns at once when the installed
document is the one it last left, and skips a template whose row, variant
row, and own row are unchanged before reading its children, so a quiet tick
allocates nothing; read back with `world.placements`). Escrow
reclaim, transfer expiry, and park teardown are all driven by a
`WorldDeadlineTable<TToken>` — entries sorted by due tick, drained from the
front, so a tick with nothing due pays for one comparison rather than a walk
of every row, lease, or population entry the owner holds; ownership and parks
rebuild their table only when the document (ownership) or `WorldPopulation`'s
own revision (parks) has moved since the last rebuild, and the transfer
table is kept incrementally in sync with `WorldTransferEscrow`'s own lease
table. The contribution sweep still walks every placement each tick — it has
to observe a live adjacency link dropping, which the document cannot reflect
— and reads link liveness through `WorldServer.TryLinkLiveness`, which pairs
`WorldEventFeed.LinkStalenessTicks` with the row's compiled
`livenessGraceSeconds`; its retraction defers, rather than proceeding, while
the slot's inhabitant is drive-possessed. Every recovery mutation remains undoable.

**Steady-state performance contract.** The per-tick pipeline — intent fold,
sim step, snapshot emission, binding resolution — allocates nothing; document
and JSON work is confined to the boundaries (load, save, and mutation
application), and a mutation rebuilds only the changed section's derived
state, never the whole document's. The binding half of that claim carries one
documented bound: `InputRouter` folds a signal's per-command memos in a stack
buffer sized for 32 bindings on one source, far above what one page plus the
host plane authors, and a signal that exceeded it would fall back to a heap
buffer for that fold alone.

## Field lattice

The reaction integrator — `Puck.Physics.Fields.FieldLattice` — is Physics
vocabulary; see [its README](../Puck.Physics/README.md#-field-lattice-fields)
for the reaction kinds, paint fills, medium-coupling geometry, and checkpoint
shape. `WorldPopulation` owns the document seam:
`WorldPopulation.CompileFieldLatticeInput` (`WorldPopulation.Resolve.cs`)
flattens the already-compiled `WorldFieldProgram` (Schema — the typed
executable reaction IR over `StateHandle`s) into a `FieldLatticeInput`,
mapping field handles to ordinals and `WorldFieldWriteOp` onto the kernel's
own enum, so the kernel parses no document; the companion `WorldFieldsSection`
otherwise remains authoritative for topology, cadence, paint, and
presentation.

`WorldServer.StepFields` runs after the rules (so a tag a rule wrote this tick
is what an `emit`/`expose` reaction reads this same step) and before the
snapshot (so the step's cell writes ride this tick's delivery), on the
lattice's own `stepEveryTicks` cadence. `WorldServer` is the
`IFieldLatticeHost`: `ReadScalar`/`AddScalar` resolve through
`ReadScalarSlot`/`AddScalarSlot`, the SAME `WorldStateReader.TryRead` seam
every other state read uses, and `AddScalarSlot`'s write lands through the
ordinary `UpsertStateCell` mutation (`WorldPrincipal.World`, journaled,
undoable), never a bypass — a season row a rule writes and a reaction reads
can never disagree about the value. `WorldServer.Responses.cs`'s
`WorldPlacementResponse` condition resolves its scalar the same way, by row
name, since a response trait is authored outside the compiled reaction
program and carries no precompiled handle.

Cell values are checkpointed (`FieldLattice.Checkpoint`) and delivered as
`FieldCells` deltas on the snapshot (`FieldsFull` on a primer) — never
document rows, so nothing journals them directly;
`WorldAuthorityCheckpointCodec` owns the wire encoding. A whole-document
rebuild may replace compatible reactions in place without resetting cells,
deltas, revision, or checkpoint shape (`FieldLattice.CanInstallInput`/
`InstallInput`); adding/removing a lattice or changing topology, cadence, or
a field envelope refuses and asks for a host restart. The `world.fields`
read-back includes installed node order, dependency edges, and cell/body pass
counts.

## Simulation authority

Every entry in the entity table is a simulated player advanced on the server
from a `PlayerIntent` — no entity is pose-driven, and poses are never accepted
from outside the simulation. Drivers (seats, console verbs, addon guests,
authored producers, replay tapes) only produce inputs; poses flow out through
the tick snapshot. Simulation state is `Puck.Maths` fixed point and exact
engine-tick durations throughout — no wall clock, no RNG, no float. That
determinism is a design contract verified by running and by the replay verbs
below; no build gate enforces it for this game (see `CLAUDE.md` rule 3).

A body's pose is always six-degrees-of-freedom (a `Vector3` position and a
quaternion attitude); its body motion program (`grounded` or `free`) decides how an
intent integrates. Ways of moving are DATA: a `WorldKit` row in the world
document names a motion program, tuning, producer parameter maps, and action bindings, and
entities distribute across kit rows by the document's assignment policy. A new
way of moving is a new row, not an engine enum.

Each entity carries one `IntentSource` — what fills its intent gaps between
scripted tape segments: `live` (the submitted stream), `idle` (hold still), or
`producer:<name>` (an authored producer program declared by the selected kit). The per-tick merge rule is
tape > submitted > producer > zero.

## The entity table (`WorldPopulation.cs`, `WorldBody.cs`)

Capacities are single-sourced in `WorldBodiesLimits`
(`Puck.World.Schema`): up to 4096 authoritative bodies, of which indices 0–3 are
the reserved local seats and the rest host simulated stand-ins and network
peers. The client reserves 128 full-detail catalog rigs and represents later
active indices with one-instance coarse capsules, keeping the worst-case SDF
program under its fixed instruction/transform ceilings. `WorldBody` owns one entry's integration, pose, tape, motion row, and
action state — including its live `Scale` multiplier (`WorldBody.Scale.cs`),
1 unless `bodies.scaleRow` names a keyed `state.world` row carrying this
body's cell; `WorldPopulation.SyncBodyScale` resyncs every active body's
`Scale` wholesale from that row at the same `Install`/admission choke points
`WorldGrants.SyncState` resyncs its own drive-gate index at (construction,
every `Install`, every seat join/peer admission, `RestoreCheckpoint` after
`WorldPopulation.Restore` rebuilds every body at the constructed default, and
a detached-seat/peer transfer restore), so a reused population slot — or a
body a checkpoint restore or transfer just minted fresh — never inherits a
previous occupant's value nor sits at the unscaled default the row itself
disagrees with. `Scale` multiplies the kit's
shared collider volumes about the body's own root (never mutated in place —
a per-call scratch span, scaled only when `Scale != 1`), the resolved move
speed and turn rate, hold probe height/standoff/reach, a hold's own gravity
fall/rise and its vertical-channel envelope (including a medium's idle/settle
target), a wall hold's travel speed, and a
pull's own rate (`WorldBody.Hold.cs`) — a shrunk body's fall and depenetration stay
proportionally gentle rather than free-falling one tick of full-scale gravity
into a collider whose own contact skin margin it can no longer absorb; the
client reads the same row live and folds it into the rendered rig. Only the
self-collision sweep (`WorldBody.Step.cs`'s `ResolveProgramContacts`) reads
the scaled volumes — body-vs-body contact, overlap events, adjacency transfer
sweeps, and the cross-boundary continuum trajectory still read the kit's
shared unscaled copy.

A placement's `Inhabit.Count` (`Puck.World.WorldPlacementInhabitCount`) is
either an authored literal or a cell reference (`{"row", "key"}` naming an Int
`state.world` row) — the spawner primitive a rule-written cell rides. A
literal-count placement grows/shrinks under the ordinary structural
`WorldPopulation.ReconcileInhabitants` pass, exactly as before; a cell-driven
placement is skipped there and starts every structural install (boot
included) with zero live bodies, admitted only by
`WorldPopulation.ReconcileInhabitCounts` — called from
`WorldServer.InstallRuntimeStateValue` on every state-only mutation apply, so
a rule's end-of-tick fold reconciles on the same tick it writes the cell. The
method caches the last raw value it resolved per placement ordinal and skips
a placement whose cell has not moved, so an unrelated state write (or a
document with no cell-driven inhabit facet at all) costs one bounded array
scan and no allocation; a structural install invalidates the whole cache,
since a placement's ordinal can carry a different row reference after a
reorder without its overall count changing. Growth claims the highest free
slot in document order (mirroring the literal path); shrink retires the
lowest surviving index that is not a seat's own claimed body
(`Entry.IsRemoteHuman`), so a possessed inhabitant never gets torn out from
under its player. The resolved count clamps to the tighter of the world's
peer capacity and the distribution's own sample count, echoing the clamp on
the `world.placement` narration channel (`count <n> of <cell> (clamped by
<bound>)`) when it actually bites. Bodies advance against the one contact-resolution seam
`IContactField.cs`, which has two providers: the analytic `WorldColliderSet`
(document-derived convex colliders) and the SDF-backed `WorldSolidField.cs`.
Both include solid scene rows, screen frames, and the shapes emitted by solid
creation placements. The field compiles those surfaces into one fixed-point
signed-distance program. A world authoring `collision.gridCellSize` reads that
program through `SdfBandedFieldEvaluator` over an `SdfDistanceGrid`
(`Puck.SignedDistance.Queries`): exact inside the contact band — the largest
kit collider extent at the scale row's ceiling, plus the contact skin, plus the
grid's slack — and the grid's corner bound beyond it, so a contact sample,
sphere cast, ground probe, or sight line in open air reads one corner instead
of marching every solid. The grid covers every finite instance bound padded by
`WorldSolidField.GridPadding`; the census's `SolidBakeHash` folds the program's
packed words, the cell size, and the contact reach, a `SetCollision` edit that
keeps the cell size keeps the grid, and kit and bodies-row edits rebuild the
field because the band derives from them (a whole-row upsert of the scale row
itself takes effect at the next solid rebuild). `world.collision.status` echoes the
cell size, corner extent, baked corner count, band, and hash. The analytic provider emits exact isotropically
scaled spheres and world-axis bounds for other finite placement primitives;
rotated, rounded, non-box, smoothed, and boolean-carved geometry is therefore
conservative there. A solid row participates in simulation, which is why
mutating scene, screen, creation, or placement geometry is a real authority
widening.

A non-seat body sleeps once its program has produced no motion and it has
received no intent for `bodies.sleepAfterTicks` engine ticks (`WorldBody.Sleep.cs`;
0, the default, never sleeps). `WorldPopulation.AdvanceSimulated` skips a
sleeping body outright — no producer staging, no motion program, no contact
solve — until something wakes it: an adopted intent (tape, submitted, or
producer), a hard teleport, a transfer, a targeted effect, a designation
written into one of its own registers, a parked peer resuming, or a bump to
`WorldPopulation.ContactFieldVersion`. That version sums
three independently-increasing counters — a document install/adjacency
reconfigure, `WorldColliderSet.RefreshAttached` finding an attached row at a
new pose, and the field lattice's own `Revision` — so an unchanged sum proves
none of them moved since a sleeping body last observed it. `body.where` trails
`asleep=<tick>` for a sleeping body (absent otherwise); `world.status` echoes
the population-wide `sleeping` count. A rigid kit's own rest latch
(`WorldBody.Resting`) is a distinct, older mechanism — a rigid body never
reaches this one at all (`Advance` returns before it).

Body-frame policy is compiled separately from that provider seam. Every body
uses opposed solved gravity (or the contact field's ambient up fallback) as its
ambient frame. Authoring `GradientDerivedUp` additionally selects
surface-following: a measured walkable support normal may orient a grounded
body. Without it, the normal remains a grounding fact, so a rounded lip cannot
silently pitch the body. A live collision rebuild installs the new policy
beside the new provider; the adoption rule is authoritative on the next step,
and a defined new ambient direction reseats the held axis then.

A kit shaping its planar velocity through a `dynamics` row (rather than a
whole-vector `along` row) carries the follower's Q32 state — position
and velocity raws, plus the previous commanded target the `r` term needs —
as ordinary `WorldBody` sim state (`WorldBody.Dynamics.cs`); a medium hold's
vertical lane carries the scalar counterpart. Cross-world motion continuity
round-trips their values through `TransferState`. A same-world authority
checkpoint additionally carries their seeded latches, the arbitrary-up
frame/reseat/turn fractions, and complete hold/tether state through
`IntegrationResidue`/`WorldAuthorityCheckpointCodec` (still under development —
`SupportedVersion` stays fixed and the fail-closed wire shape changes directly,
with no compatibility reader).

`kit.autonomy` independently batches non-human motion and producer steering in
engine-tick time. Bodies are deterministically phased across each interval;
elapsed time is integrated in one batch. Local seats, connected remote humans,
live sources, tapes, and bodies with pending external input stay at full
authority rate. `motionSeconds` must remain zero on `bodyContact: solid`, since
a body skipped for a tick cannot honestly participate in that tick's dynamic
contact solve. Use overlap bodies for large flocks and tune perception,
steering, and motion cadences independently.

`collision.events` bounds proximity-event work separately from physical
contact: established pairs have continuity priority, new discovery uses a
deterministic sweep with per-body candidate and degree limits plus a global
begin budget. `maxPairsPerBody: 0` disables pair events without disabling
world contact. Saturation deliberately omits lower-priority new event pairs.

`collision.bodyContacts` is the separate physical-depenetration budget for two
`solid` kits. Its per-body candidate budget (default 16, maximum 32) and
resolved-pair degree (default 8, maximum 16) bound a fully coincident crowd;
stable population order decides which later pairs are omitted. The counters
`DynamicContactCandidates`, `DynamicContactNarrowPairs`,
`DynamicContactResolvedPairs`, and `DynamicContactLimitedBodies` expose the
actual work to tests and host diagnostics.

### Rigid dynamics (`WorldBody.Rigid.cs`, `WorldPopulation.Rigid.cs`)

A kit carrying a `rigid` facet (`FixedWorldRigid`, derived from the kit's own
sphere/capsule/box collider and authored mass via `Puck.Maths.FixedMassProperties`)
hands its bodies to the rigid solver instead of the grounded/free motion
program: `WorldBody.Advance` branches to `AdvanceRigid` before any intent,
action track, or hold runs. Each substep rotates and translates the body
about its own centre of mass (`root + orientation·CenterOffset`), not its
root: the CoM under the pre-substep orientation is displaced by the
substep's linear motion, and the root is re-derived from THAT against the
new orientation — updating the root directly at the linear velocity and
re-deriving it from the new orientation's offset would additionally kick
the true CoM by the rotation alone every substep. Static-world contact is a
swept, substepped integration against the SAME `IContactField` every
locomotion body resolves against. Substep count is derived per body per
tick from its speed and the collider's own bounding radius against an
authored travel fraction (`collision.bodyContacts.rigidSubstepTravelFraction`,
floored by `collision.bodyContacts.rigidSubstepMinimumTravel`), capped by
`collision.bodyContacts.rigidSubstepCeiling`; the derived count is echoed in
`world.budget`'s `rigid` segment and `RigidStaticSubstepsThisTick`.

`WorldBody.ScaleRigid` derives a scale-consistent copy of the compiled facet
from the body's own live `Scale` (`WorldBody.Scale.cs`) on every read:
mass ∝ `Scale`³ against the authored mass at scale 1 (a uniformly bigger body
of the same material is heavier by its volume ratio), inertia (mass·length²)
∝ `Scale`⁵ so inverse mass/inertia scale by `Scale`⁻³/`Scale`⁻⁵, and the
centre-of-mass offset and bounding radius ∝ `Scale` — the same linear rule
`ScaledColliderVolumes` applies to the collider itself. Restitution, friction,
rolling friction, and both damping rates are dimensionless coefficients,
unaffected by `Scale`. `Scale == One` (the overwhelming common case) returns
the facet unchanged, touching no arithmetic. `AdvanceRigid` reassigns its own
local `rigid` to the scaled copy once at entry, so every downstream read in
that call — including the reference `ResolveRigidContact` receives — is
already scale-consistent; `TwoBodyHandle`, `TryApplyRigidImpulse`, and the
`RigidMass`/`RigidBoundingRadius`/`RigidCenterOfMass` read-backs each derive
their own scaled copy independently. `RigidCenterOfMass` (`body.where`'s
`com=`) is `root + orientation·(CenterOffset × Scale)` — the point a rolling
or tumbling rigid body's substeps actually rotate and translate about, which
orbits away from the root `body.where`'s `pos=` always echoes.

The ground (walkable) and obstruction (wall) contacts `IContactField.Resolve`
reports are independent channels — a grounded body still bounces the first
time it clips a wall — so each carries its OWN rising-edge restitution latch
(`m_rigidGroundContacting`/`m_rigidObstructionContacting`, `WorldBody.ResolveRigidContact`):
restitution fires only on a genuine impact on THAT channel, never on
continued contact, which would read gravity's own per-tick pull (or ongoing
sliding contact) as a fresh hit and never let the body settle. A body
depenetrated exactly to its contact skin's minimum distance reads no push
(`distance >= minimum`) for a run of substeps even while still touching,
until gravity's own per-substep pull re-closes the gap past fixed-point
rounding — a smaller collider needs proportionally more such substeps
(`AdvanceRigid`'s own derived substep count grows as `BoundingRadius`
shrinks). Dropping the latch on the first such miss read every one of those
substeps as having left the surface and the next contacting one as a fresh
landing, re-firing restitution on a body that never actually left the ground.
`m_rigidGroundMissStreak`/`m_rigidObstructionMissStreak` (`WorldBody.RigidGroundMissStreak`/
`RigidObstructionMissStreak`) count each channel's run of consecutive misses;
the latch drops only once a streak reaches that SAME tick's own derived
substep count — one full engine tick's worth of grace, however finely the
tick happened to be substepped — so a boundary miss rides it out while a
departure spanning more than one tick's substeps still clears it and a real
landing still restitutes. `GroundNormal` reads Zero on a genuinely grounded
substep only through `WorldAdjacencyContactField`'s own cross-authority
merge, which omits it from the `ContactResolution` it returns — the
local-only solver always sets a walkable `GroundNormal` together with
`Grounded`; that path's friction/rolling-resistance run against the same
`UnitY` the static sweep's own up axis already assumes instead. Tangential
(slip) friction at either contact is a real coupled Coulomb impulse, not a
decay curve: the contact-point velocity (linear plus the rotational
contribution `ω × r`, `r` the collider's bounding radius along the contact
normal) is arrested toward zero through `Puck.Physics.FixedTwoBodyKernel`,
clamped to the authored `friction` coefficient times the normal impulse the
sweep just applied to stop the substep's own inward motion — the SAME
meaning `friction` carries against another rigid body (below), never a
speed-independent rate. `r`, the contact anchor, is the shape's own true
witness point along the normal (`Puck.Physics.FixedRigidWitness.Anchor`) —
the real support point of the collider's box/capsule/sphere geometry oriented
by the body's own quaternion — never a point on the conservative bounding
sphere, so an off-centre wall clip carries a real lever arm. A GROUNDED box or
capsule additionally resolves over its own support MANIFOLD instead of one
witness point (`WorldBody.ResolveRigidGroundManifold`,
`FixedRigidWitness.SupportManifold` — up to four box corners, or two capsule
cap points when it lies on its side): `collision.bodyContacts.rigidManifoldIterations`
sequential-impulse passes distribute the normal impulse across every
manifold point still closing, then friction clamps per point to THAT point's
own accumulated normal impulse — never the manifold's total — which is what
keeps an upright body's centre of mass over its support polygon without an
artificial extra damping term; a standing capsule or any other single-point
volume falls back to the ordinary witness-point path, which already carries
the same torque over the one point that actually touches. The world is
modeled as an infinite-mass static phantom (`WorldBody.GroundPhantomHandle`),
so linear and angular motion stay coupled exactly as inertia dictates and a
spinning ball can genuinely start rolling (or a rolling one stop spinning)
rather than the two evolving independently. `rollingFriction` remains a
separate pure angular-velocity
decay while grounded — rolling resistance, not slip friction. Rolling
friction and both damping coefficients are authored per-second RATES,
applied as `(1 - rate·dt)` each tick so the same value decays identically at
any simulation rate; the rest thresholds and hold window
(`collision.bodyContacts.rigidRestLinearSpeed`/`rigidRestAngularSpeed`/`rigidRestHoldSeconds`)
are authored the same way, defaulting to the engine's original hard-coded
values.

Dynamic-vs-dynamic rigid contact rides the SAME broadphase/narrowphase
`ResolveDynamicContacts` already runs for two `solid` kits
(`FixedDynamicBodyContacts.TryCorrection`, whose correction direction points
from the second body toward the first, and whose box-vs-box separating-axis
test is `Puck.Physics`'s own — see that project's README). When at least one side is rigid,
`WorldPopulation.ResolveRigidPairContact` replaces the plain positional split
with an impulse computed through `Puck.Physics.FixedTwoBodyKernel` — the
kernel's own contract names its "A" side the body the contact normal points
AWAY FROM, so the pair's roles are assigned to match that direction exactly
(swap them and an approaching pair reads as a positive, separating, closing
speed). Each rigid side's contact anchor is its own true witness point facing
the other body (`Puck.Physics.FixedRigidWitness.Anchor`), never the body
center and never a point on the conservative bounding sphere — real torque,
not a torque-free strike, reaches both sides when both are rigid, exactly
matching the true surface a struck capsule or box actually contacts. `ResolveDynamicContacts` runs a derived-down count of EXTRA full
broadphase-plus-narrowphase sweeps in the SAME tick (`RigidPairPassesThisTick`,
capped by `collision.bodyContacts.rigidPairIterationCeiling` and derived down
from `rigidPairIterationBudget` divided by the first pass's own count of pairs
routed through the rigid impulse path), each re-scanning every solid body's
CURRENT (already corrected) position rather than replaying only the pairs the
first pass happened to find — a rigid pair's own positional split can move a
body into a third one only a later sweep discovers — so an impulse chain (a
rack break, a falling domino line) crosses more than one pair-hop within the
tick instead of propagating one body-hop per tick; an extra sweep that
resolves nothing stops the run early, and both counts are echoed in
`world.budget`'s `rigid` segment. A
resolved
pair's tangential (friction) impulse is likewise a real Coulomb impulse
through the kernel — the full-stick impulse that would zero the relative
tangential velocity, clamped to the pair's average friction coefficient
times the normal impulse just applied — never an independent rescale of
either body's whole velocity vector, which would burn or invent momentum
along the normal too. Below a small authored closing-speed floor
(`collision.bodyContacts.rigidPairRestitutionSpeed`) restitution is treated
as zero — a rigid pair carries no per-pair rising-edge latch, so without
this floor two touching bodies would restitute a hair apart every tick they
are found overlapping and never fully settle. A kinematic (locomotion) side builds a
STATIC phantom handle (`WorldBody.TwoBodyHandle`) carrying its own live
velocity so it contributes to the closing-speed term without ever receiving
an impulse back (`FixedRigidBody.IsDynamic` gates every write) — "a
kinematic character contributes its velocity; it is never pushed by them."
Positional depenetration still runs, restricted to the rigid side(s) alone
against a kinematic partner, through `WorldBody.ApplyRigidPositionalCorrection`
(never the locomotion `ApplyDynamicContact`, whose planar/vertical-velocity
channels a rigid body does not use) — which also wakes the body it moves,
since a body another one is visibly displacing is not at rest whatever its
latched velocity said a moment ago.

A body settles to `Puck.Physics.Motion.ActionFact.Resting` (`BodyFacts.Resting`,
published by `WorldBody.FactHolds`) after its linear and angular speed stay
below threshold for a short hold window while grounded — the LINEAR threshold
scales with the body's own live `Scale` (a spatial rate, like a hold's own
travel speed), the ANGULAR one does not (no length dimension). Once the latch
closes, `WorldBody.AdvanceRigid` stops integrating that body entirely — position,
orientation, and both contact latches hold bit-identical every tick — until
something wakes it (`TryApplyRigidImpulse`, `CommitRigidHandle`,
`ApplyRigidPositionalCorrection`, a `Pose` teleport, or `SetContactField`
handing it a different field reference — a live solid edit that moved or
removed the floor it rested against — each of which clears the latch
itself): this
is what makes `$physics:quiescent`/the `resting` fact mean a body is not
moving, rather than merely reporting zero velocity at the instant the latch
last closed. `body.impulse`
(`WorldCommand.RigidImpulse`, checked for `IsRigid` server-side and refused
by name otherwise, then `WorldBody.TryApplyRigidImpulse` — refused by name a
second way when the impulse's scaled velocity delta overflows the fixed-point
representation, leaving velocity entirely unchanged rather than applying a
wrapped or partial result) wakes it. `$physics:quiescent`
(`WorldPopulation.RigidBodiesQuiescent`) reads 1 when every active rigid body
rests, vacuously 1 for a world authoring none. `world.rigid` echoes the live
per-body census (mass, velocity, angular velocity, resting) plus the
quiescent verdict; the compiled rest/substep/pair-restitution policy and the
last tick's solver work (pair resolutions, worst substep count) are
`world.budget`'s own `rigid` segment. Checkpoint (`IntegrationResidue`)
and the diagnostic population hash (`WorldReplaySnapshot.HashState`) both cover
linear/angular velocity, the resting latch and hold-tick counter, BOTH
restitution edge latches, and both channels' miss streaks. A kit swap that
adds or drops the `rigid` facet resets every rigid-solver field
(`WorldBody.RecompileKit`) rather than leaking the other kind of body's stale
state forward; a live retune that keeps the facet carries its velocity
through unchanged. Cross-world transfer of a rigid body is refused by name
(`WorldInstanceHost.Transfers.cs`); a carrier holding one refuses its OWN
transfer for the same reason (see Carry below).

`TryApplyRigidImpulse` refuses by name a second way beyond a raw fixed-point
overflow: the resulting velocity's magnitude may not exceed
`WorldPopulation.RigidVelocityCeiling` — the document's own declared speed
ceiling (`WorldFacePortalPolicy.SpeedCeiling`, the same fastest travel a
crossing face already reads), so a degenerate `body.impulse` magnitude is
refused before it can reach the solver as an unrepresentable per-tick
position delta rather than accepted and left to overflow deeper in the
sweep. `world.budget`'s `rigid` segment echoes the compiled ceiling as
`impulseVelocityCeiling=`.

`WorldBody.ScaleRigid`'s inverse-mass/inverse-inertia scaling saturates to a
representable ceiling rather than reverting to the unscaled component when
the correctly rounded `Scale⁻³`/`Scale⁻⁵` product overflows the signed
64-bit raw — inverse inertia's fifth-power law reaches
`FixedRigidScales.RoomScale`'s placement well before inverse mass's
third-power one does, and reverting only the overflowing component would
leave it at the FULL-SIZE body's magnitude while every sibling component
already reflects the shrunk one, an inconsistency severe enough to read as
a light body friction can decelerate but that resists spinning as if it
were still heavy. Saturating both components the same way keeps every
scaled quantity moving in the SAME direction (lighter, easier to spin) past
the point the exact magnitude stops fitting.

### Carry, as attachment (`WorldBody.Carry.cs`, `WorldPopulation.Carry.cs`)

A kit's `carry` facet (`FixedWorldCarry`: a body-local frame offset, a
carrier mass-equivalent, an authored carry-mass fraction, and a reach) is a
distinct facet from `rigid` — the seam `body.carry <carrier> <target>` and
`body.release [carrier]` use. `WorldBody.TryBeginCarry` refuses by name: a
carrier kit with no `carry` facet, a target with no `rigid` facet, either
body already a party to another carry relationship, a target outside the
carrier's own live-scaled reach (`MaxReach × Scale`), or a target whose own
live-scaled `RigidMass` exceeds the carrier's live-scaled ceiling
(`MassEquivalent × MaxCarryFraction × Scale³` — the same mass ∝ `Scale`³ law
`ScaleRigid` derives against). The body-local carry offset scales linearly
with the carrier too, matching its collider and reach. A carried body's own `Advance` is a no-op —
its rigid integration is suspended, never solved — and
`WorldPopulation.PrepareCarriedBodies` first releases invalidated relationships
before integration; `UpdateCarriedBodies` then runs after both advance passes,
dynamic-body contact, and tether correction, so it derives a DESIRED position
and orientation from the carrier's final authoritative pose for that tick:
`carrier.root + carrier.orientation·(Offset × Scale)`. That desired position is
never taken unconditionally — TANGIBLE carry: the target's own collider is
swept from its previous position to the desired one against static geometry
(`WorldBody.FollowCarrier`, the same `IContactField.ResolveSweep` a rigid
body's own static contact uses), and `WorldPopulation.ResolveCarriedBodyPush`
then resolves the swept position against every other active solid body on the
same positional-split terms `ResolveDynamicContacts` already applies to a
plain pair, pushing the other body away. Whichever correction either sweep
applied is handed straight back to the carrier through its own motion seam:
`WorldBody.ApplyRigidPositionalCorrection` for a rigid carrier (which also
wakes its exact-rest latch), or `WorldBody.ApplyDynamicContact` for a
locomotion carrier. The carrier itself is therefore stopped by what it is
holding, not just the held body. The target derives rigid velocity from the
carrier's own `ApproximateWorldVelocity`.
A carried body is excluded from `ResolveDynamicContacts`'
broadphase and from `$physics:quiescent`'s census (`CarriedBy: null` on both) —
its own tangibility sweep above is the ONLY contact path it participates in.
`body.release` refuses by name (`WorldPopulation.TryEndCarry`, leaving the
relationship untouched) when the target's CURRENT pose still overlaps static
geometry (`WorldBody.IsPenetratingStaticGeometry` — a zero-displacement probe
sweep) or another active body; otherwise it
hands the target back to the solver with the carrier's own current
velocity rather than snapping it to rest. `Carrying`/`CarriedBy` are `int?`
population indices (`-1` raw, on the same "never a boolean fact" terms
`AffectingSubject` already carries) folded into checkpoint
(`IntegrationResidue`) and the authoritative hash; a relationship whose
mirror breaks (a body going inactive, or a live kit retune away from the
facet either side depends on) self-heals in the same per-tick pass rather
than leaking a dangling reference forward. That pass walks a sorted,
preallocated table of active relationships, not the full population capacity.
`body.where` echoes
`carrying=<index>`/`carriedBy=<index>` only while set.

### Tether (`WorldBody.Tether.cs`)

A kit's `tether` facet (`FixedWorldTether?`, constructor/`RecompileKit`
parameter alongside `rigid`/`carry`) is a further distinct facet — the seam
`body.attach`/`body.detach`/`body.reel` use. `ProcessTetherIntent` reads the
facet's `attachChannel`/`detachChannel` ordinals DIRECTLY off the intent every
tick (never through the kit's action table, since it carries its own
threshold), firing at most one transition per tick: a detach edge always wins
over a same-tick attach edge, and a fresh attach only ever starts while
untethered. `TryAttachTether` throws a directed aim query
(`FixedSurfaceQuery.TryNearestSurfaceAlongDirection`) along the body's facing
within `maxAnchorDistance`/`aimHalfAngle`; a candidate anchors a
`FixedTetherConstraint` at the candidate's own resolved distance (never the
authored ceiling), with `minLength` clamped down when authored past what the
attach actually found. `ProcessTetherReel` hands the held reel channel's
value straight to `ReelTether` at `lengthRate`, scaled by the channel's own
sign. `DetachTether` clears the tether and scales the body's surviving
planar/vertical velocity by `releaseVelocityScale`. `m_tether is not null` is
the single source of truth for "is this body attached" — there is no
separate mode field. An optional `modeState` counter ordinal, resolved once
at kit compile time, is written `1`/`0` on every transition
(`WriteTetherModeState`) so a world's camera program can `select` on it. A
kit swap that changes the facet's own presence (`RecompileKit`) drops any
live attach rather than let it dangle against ordinals the new (or absent)
facet no longer resolves, on the same terms a rigid-facet swap resets
solver-owned fields. Any other tether-facet retune drops a live attach and
publishes the cleared fact through its retained `modeState`. A changed attach/detach binding resets its old edge
history, and a changed `modeState` ordinal clears the old row and publishes
the live attach fact through the new one. `IntegrationResidue.Tether`
(`WorldBody.TetherResidue`)
carries the attach/detach previous-bits and the complete rope state
(length, min length, reel remainder, anchor) through
`WorldAuthorityCheckpointCodec` and the authoritative hash; a cross-world
transfer deliberately drops it (`TransferState` carries none of it) since a
tether anchor names the source authority's own coordinate frame.
`body.tether` echoes `attached=<yes|no> anchor=(x, y, z) rope=<length|n/a>`;
`body.where` trails `tether=<length> anchor=(x, y, z)` only while attached.
`WorldPopulation.ResolveTethers` (unchanged by the per-kit fold) solves every
attached body's rope after every body has integrated and dynamic contacts
have resolved, reading a body-anchored tether's anchor from the SAME tick's
just-advanced pose.

World rules can carry [decision policies](../Puck.World.Schema/README.md#decision-policies).
`WorldServer.Decisions.cs` owns each binding's selected option, local PCG state,
reconsideration and commitment timers, and interrupt-edge memory. It reuses
ordinary predicate/expression/effect evaluation and keeps state through an
unchanged-policy recompile. The server checkpoint includes these bindings;
restore validates them before changing authority state, and authoritative
hashing visits their sorted keys without allocating a sorting buffer.
`world.decisions` reads the choices back, including the raw last-evaluated score
and consumed draw count. A policy edit starts a new decision episode rather
than applying old option ordinals to a different policy.
Parameterized neighbor options also retain the selected body's generation.
`WorldServer.DecisionNeighbors.cs` freezes poses once before ordinary rules,
shares lazily rebuilt power-of-two range grids, and uses the physics sampler's
inspection bound even in a coincident crowd. Per-option reusable choice buffers
avoid a population-sized stack or per-reconsideration allocation. The selected
individual's gate is checked during commitment; physical perception refreshes
only when deliberation runs. Diagnostic work counters are not simulation state.
`world.budget` reports the shared pose-image and range-grid ceilings alongside
query work, including how many grid points may be sorted in one tick. The
static ceiling does not discount authored cadence because all policies can
reconsider together. It is a structural work sheet, not a frame-time prediction.

Committed journal entries use `WorldSubmissionCodec.TryEncodeCommittedMutation`
and `TryDecodeCommittedMutation`: world-authored rule effects must survive a
checkpoint. Pending external submissions and replayed external inputs retain
the live mutation codec, which refuses a world actor on both encode and decode.
The committed codec admits only the canonical world actor and does not relax
nested grant validation.

### Transfer recovery and forwarding

An occupied source slot retains its pending recovery; a rollback-only checkpoint
keeps only the remaining paired body/profile records and can never retry Commit.
Restoration reinstalls the original mobility identity even if that slot was reused.
A contradictory peer commit verdict after rollback leaves recovery held and
reports once; it cannot create another body or stop unrelated worlds.
Non-atomic parties split before reservation, so a parent lease cannot block its
own children. Each child has its own capacity verdict.

Component stress and replay MATCH alone do not prove
federated delivery; transport and host recovery require their own checks. The
host retains unresolved destination identities, remote endpoints, and exact commit
payloads through repeated capture and restore.
`RestoreRow` validates all in-doubt and forwarding records before changing host state. A local
destination can be admitted later: reconciliation matches its authority identity,
not merely its registry name. Reinstalling a host slice replaces its transaction
records without duplicating them. The original cohort and source boundary frame
also survive partial rollback and restart, so a confirmed refusal can still clamp
the traveler inside the source boundary. Resolver destination, scope, and generation
remain available for outcome narration.

A known destination commit is checkpointed as `CommitConfirmed` until every
source-side route and roster publication succeeds. A publication failure reports
`PUBLICATION-PENDING` once per running recovery record and retains its exact member
payloads and frozen source histories. Retries, including after restart, finish
publication without querying status, committing again, or restoring a second body
at the source. The captured `FollowedSeatMask` keeps an already-moved participant
occupied if a later member's publication fails. Only successful publication retires
source histories and acknowledges the destination's exact commit receipt. Restore
rejects contradictory commit/rollback phases and invalid or overlapping seat masks
before replacing host state.

Remote recovery reconnects through the networking library using the retained
endpoint and expected authority identity. After a confirmed commit, forwarding
and local seat routes derive their credential from the retained traveler and
its next ownership epoch, never from a connection's reservation cache. A fresh
connection did not perform the original reservation; a slot-keyed cache might
also name a later occupant.

Once a transfer is finished, the source keeps a forwarding route so input sent
to the old authority can still reach the traveler. These routes survive restarts
independently of pending transactions. Each captures the original source authority,
destination identity, and mobility credential; remote routes also capture their
endpoint and definition. A missing local destination produces a named unavailable
result and remains saved until that exact authority is admitted, even under a
different registry name. Remote routes reconnect lazily over QUIC and check the
expected authority identity. No connection or held-input lease is stored in the
checkpoint. Replacing a local route releases its old held-input lease, and that
retired lease cannot publish again. An empty source authority with forwarding
routes is not automatically reaped. Explicitly stopping a destination unbinds
incoming routes; admitting the same authority later binds them again.

The route follows later transfers whether a hop is local or reached over QUIC.
Each hop checks its own source-scoped credential before following the next route,
and a local call releases its authority lock before entering another authority.
Synchronous local traversal refuses after 64 hops, bounding stack use when a
broken route forms a cycle. An accepted leave retires the traveled credentials
and every retained branch for that incarnation in each forwarding host, without
removing another traveler's routes. The final body still follows the world's
authored reconnect-grace policy.

The console can move any active body through that same transfer path:
`world.transfer <source-instance> body:<index> <target-instance>` uses a zero-based
body index, including creature and network-peer slots. Bare numbers select the
four local seats using their one-based display numbers; `party` selects the active
local-seat cohort. Explicit body targets do not bypass Drive grants or destination
admission, and an index outside the source world's actual capacity is refused at
the transfer drain.

A disconnected seat or peer does not drop its body on the spot — it PARKS
(`Entry.Parked`/`ParkedUntilTick`) for `bodies.reconnectGraceSeconds` (converted to ticks at compile),
retained pose/state and all, before `ReclaimExpiredParks` tears it down; a
matching re-Join resumes the retained body instead of minting a fresh one.
The park defers the BODY only: a disconnecting peer generation's grant rows are
released at the `PeerDisconnected` event itself (and a checkpoint restore
releases a restored park's at `RestoreCheckpoint`); a verified-identity
reconnect that resumes the parked body re-mints its admission templates
through the ordinary `PeerAdmitted` event.
See [references/session-lifecycle.md](../../.claude/skills/puck-world/references/session-lifecycle.md)
for the full contract.

## Network transport (`WorldPeerHost.cs`)

`WorldPeerHost` binds the networking library's QUIC peer listener from `host.listen`
(a document field the composition root also lets `--listen` reflect for one
run). `WorldPeerNetwork` owns a shared, lazily created `Puck.Networking.Peers.Peer`.
The desktop persists its key under the state directory's `Network/peer.pk8`, or
uses the explicitly supplied federation key; a silo activation uses its configured
key. Local-only worlds initialize neither QUIC nor a certificate. There is no TCP
fallback. The library owns TLS, certificate-bound peer identity, message signatures,
and bounded message queues. `PeerStream` supplies ordered bytes to the World codecs,
segmenting large documents into bounded messages without changing their contents.
Before closing a completed World exchange, the host uses the networking library's
bounded stream drain (at most 500 ms, cancelled by shutdown). A completed QUIC write
does not guarantee that immediate connection disposal preserves the final refusal;
the drain gives the reader time to consume it. An unadmitted connection retains
its handshake slot during this wait.

For the authenticated peer stream, this drain waits for the link to close, even
if the peer already ended its sending direction. A half-close after a truncated
identity frame must still leave the receiving direction open for its named refusal.

The host accepts an optional `TimeProvider` for its admission deadline; production
uses `TimeProvider.System`. Tests advance that clock after observing the actual
challenge or queued admission, so timeout coverage does not wait ten wall-clock
seconds. `PendingWorkCount` reports queued work awaiting the tick-thread drain.

World admission remains an application policy, separate from proving possession
of a peer key. After the networking handshake, two World checks run off the tick thread before any body is
admitted — neither touches server state beyond a read-only document snapshot:
door 1 is the raw protocol-version handshake (`WorldProtocol.WireProtocolKey`
via `WorldHelloDoor.TryAccept`, `Puck.World.Protocol`); door 2, once door 1
passes, is the IDENTITY challenge-response
(`Puck.World.Protocol.WorldAdmissionDoor`) — the host mints a fresh
nonce, the peer answers with a signed `Puck.Attestation` claim (and, for a
vouching root, its two-hop chain), and the door verifies it against the world
document's own authored `admission` section, mapping the verified identity to
that entry's own authored grant templates. Each door refuses by its OWN named
spelling (`version-mismatch: …` vs `identity-refused: …`) — the two are never
conflated. Only once BOTH doors pass does population admission run
(`WorldServer.TryAdmitPeerConnection`, refused by name when the 128-body table
is full or the document's `networkPlayers` admission cap is already met); every
subsequent frame
(decoded through the SAME `WorldFrameCodec`/`WorldSubmissionCodec` leaves the
loopback and tape use), and disconnect
(`WorldServer.DisconnectPeerConnection`) are marshaled onto the tick thread —
`WorldServer`/`WorldPopulation`/`WorldGrants` carry no lock, so nothing may
touch them from a connection's background reader directly. The LOOPBACK path
(`WorldServer.ApplySession`'s `SessionRequest.Join` case, driven by
`LoopbackTransport`) crosses door 1 only, by construction — see that method's
own remarks on why the process boundary is the trust boundary there and no
identity check applies.
`WorldPeerHost.DrainPending`, called from `WorldServerStepShell.Step` before
`WorldServer.Step`, is where that hand-off actually applies: one global FIFO
for v1, no per-connection quotas or bounded-queue backpressure. A decoded
payload's own embedded principal (Command/Session/Mutation each carry one,
read directly by their handlers) is re-stamped with the connection's admitted
`Peer` identity before it becomes an envelope — a handler reads the identity
the door resolved, never the one the client's bytes claimed.

v1 is strictly request-then-response per connection, so no correlation id
travels on the wire; the downstream reply is a small NEW grammar
(`Puck.World.Protocol.WorldPeerWireFormat`) carrying exactly the Completion lane
(`WorldSubmissionResult`, i.e. Ack/Session/Query) — never a streamed
snapshot/definition/composition/lever (`WorldOutputHub`'s encoded lane stays
a scaffold beyond this one lane). `--connect` does not speak this door as a
client at all: `Puck.World.Program` enqueues a federation transfer
(`WorldInstanceHost.EnqueueTransfer` with `TransferDestination.Remote`),
which authenticates the resulting `WorldRemoteAuthority` purely over
`Puck.Networking.IAuthenticator` (`Puck.World.Protocol.WorldAttestedAuthenticator`,
a signed claim over the challenge — never a shared secret) — the interactive
attestation identity door above is server-side only today; no production
client crosses it.
`Puck.World.Console`'s `WorldNetworkCommandModule`'s `world.peers` echoes the
connection table this class owns — each connection's verified admission
identity (domain/subject) — plus an `arrivals:` group naming every body
admitted by transfer and the authority its verdict was decided against;
`Puck.World`'s `WorldMutationCommandModule`'s
`world.admission` echoes the document's own authored `admission` entries —
the runtime and document halves of the admission decision, respectively.
`world.links`, in the same module, is the seam-liveness read-back: one line per
authored `adjacencies` row naming its destination, neighbour authority, the
tick-derived staleness/grace the `$link:` rule channel and the
`linkEstablished`/`linkDropped` event family both read, and — clearly marked
presentation-only, never a simulation input — the transport lane's wall-clock
backoff state.

Each connection's whole lifetime runs under `WorldNarrationScope.Current` set
to this row's `AuthorityIdentity` (an `AsyncLocal<string?>`, flows across every
await): a host running several rows uses it to tag the narration a connection
writes to `Console.Out`/`Console.Error` by which row wrote it, without
threading a row identity through every write site. Unset (and unread) on the
desktop.

## Engine narration (`WorldNarration.cs`, `WorldOutputHub.Narration.cs`)

A `WorldServer`/`WorldPeerHost`/`WorldRemoteAuthority`/`WorldReplayTape`/
`WorldInstanceHost`/`WorldOwnedWorlds`/`WorldMachineHost`
line that would otherwise write straight to `Console.Error` instead calls
`WorldOutputHub.Narrate(channel, format)` — `channel` is the bracketed tag the
line opens with (`world.grant`, `world.mutation`, `replay.drive`, …), `format`
a `Func<string>` invoked at most once and only while a sink is attached
(`HasNarrationSink`), so a quiet run pays for neither the interpolation nor
the write. `WorldServer.AttachNarrationSink`/`WorldInstanceHost.AttachNarrationSink`
attach an `IWorldNarrationSink`; `Puck.World.Console`'s `WorldConsoleNarrationSink`
is the one every composition root binds, so a headless script or canary reads
byte-identical lines to a direct `Console.Error` write — this project cannot
hold that implementation itself (`build/Architecture.props` denies it a
`System.Console` reference). A server's own
narration must be attached from its constructor's `narrationSink` parameter,
not after — the document's own authored grants narrate during construction,
before any post-build wiring step could reach them. The machine host
(`Puck.World.Addons.Machines.WorldMachineHost`, reached here through
`IWorldMachineHost`) takes an optional `WorldOutputHub`/narration sink
because it owns no single server of its own to share (a peer singleton to
`WorldServer`, constructed first; `WorldOwnedWorlds`,
`WorldRemoteAuthority`, and `WorldFederatedServerLink` carry the same shape
for the identical reason). A narration site inside a loop that assigns any of
the format closure's captured locals before a possible early exit must gate
on `HasNarrationSink` first and re-bind what the line needs into locals scoped
to that one branch — the compiler hoists a captured loop-body variable into a
display class allocated at its own declaration, not at the closure literal, so
an unguarded capture allocates every iteration regardless of whether a sink is
attached (`WorldServer.Responses.cs`'s `SweepPlacementResponses` is the worked
example). A handful of `Console.Error` sites remain in `WorldServer.MutationApply.cs`,
`WorldServer.RuleHost.cs`, `WorldServer.Step.cs`, and `WorldPopulation.Admission.cs`,
owned by other in-flight work; `build/Architecture.props`'s
`PuckArchitectureDeniedApi` lane states the eventual destination (no
`System.Console` reference under this project) and stays disabled until those
land.

### One admission entry, every ingress

`WorldServer.TryAdmitVerifiedParticipant` is the only path from an ingress to a
population body plus grant rows. It takes a `WorldAdmissionVerdict` and nothing
else — no arm accepts raw `WorldGrant` rows — and only
`WorldAdmissionDoor` produces one: from a verified attestation claim
(`TryAdmit`), from an already-verified identity re-matched against a candidate
document (`TryMatchEntry`, the whole-document rebuild's re-authorization), or
from an authenticated federation authority's namespace (`TryAdmitArrival`).
A caller with no verdict is refused by name rather than admitted on a default
seed. `WorldServer.BuildAdmissionGrants` fills in the two fields a template
cannot carry — the `Peer` principal, and a `body:<n>` subject for a template
that authored none (`WorldAdmissionGrant.SubjectFor`) — and passes every other
field through, so an authored template states exactly what the peer holds.

A federated or colocated transfer crosses the same door. `WorldTransferEscrow`
runs `TryAdmitArrival` once at reserve against `request.SourceAuthority` (the
namespace `Puck.Networking.IAuthenticator`'s signed-claim handshake derived from the
verified proof — never a label the connection merely claimed — or the
in-process host's own for a colocated authority), carries the verdict on
the lease, and commits it through `WorldServer.AdmitTransferredPeer`. Reserve
and commit therefore cannot disagree: the reservation's per-slot authorization
asks the verdict's templates whether they confer `Drive` over the body it is
about to bind, which is the question the mint answers again. An arrival's
identity columns name the authenticated authority, never the traveller's
carried profile — `world.peers`'s `arrivals:` group echoes them.

An `admission` row in `federatedAuthority` mode carries no key: its `domain` is
the authenticated authority namespace, or `*` for any authority that completes
the handshake. That namespace is `Puck.World.Protocol.WorldAttestedAuthenticator`'s own verified
claim subject — `host.authority` when the document authors one, else the
boot instance identity (`Puck.World.WorldDefinitionLoader.BootInstanceName`)
— never a label the connecting peer merely asserted, so `*` is what a
document authors when it cannot know its neighbours' identities in advance.
Such a row is skipped
when the door builds its attestation trust list — it can never verify a claim —
and a document authoring arrivals alone still admits no connecting peer.
## Federation transport (`WorldFederationCodec.cs`)

The same listener routes a second dialect off the first eight bytes:
`WorldFederationCodec.WireKey` opens an authority-to-authority connection
instead of a player connection. That connection is a persistent authenticated
lane — challenge/proof once (`Puck.Networking.IAuthenticator`), then framed requests
in order, request-then-response, until `Observe` or `IntentStream` takes it
over and streams on it. The frame grammar, the bounded reader/writer, and the
refusal vocabulary are the shared ones in
`Puck.Networking/WireCodec.cs`, so this codec is not a second
wire dialect: every leaf is Try-shaped and bounded before it allocates, and
every refusal frame's text opens with a `WorldFederationRefusal` name.
`WorldPeerHost.FederationRefusals` counts those names, so a refusal is read back
by name rather than by sentence.

Two ingress disciplines meet in this class, and which one applies is decided by
what the frame is:

- An ordinary admitted peer's admission, submissions, and disconnect marshal
  onto the tick thread (`RunOnTickThreadAsync` → `DrainPending`).
- An authenticated AUTHORITY operation — reserve, commit, abort, acknowledge,
  status, route, forwarded submission, published intent — runs on its socket
  worker inside `WorldServer.ExecuteAuthorityOperation`, which serializes it
  against `Step` under the server's authority gate. It must NOT wait for this
  host's next tick: two hosts crossing into one another at the same time would
  deadlock on each other's tick.

Whatever that gate protects is acquired and released under it.
`WorldOutputHub`'s subscriber list carries no lock of its own, so
`StreamProjectionAsync` disposes its projection lease inside
`ExecuteAuthorityOperation` exactly as it attached. Any check-then-act over
population state — is this transferred principal still live, then submit or
describe on its behalf — is ONE gated operation, never two.

The client half is `WorldRemoteAuthority` (`WorldRemoteAuthority.cs`), hosted in
this project though its type still carries the `Puck.World` namespace pending a
one-time normalization pass: an intent pump plus one
request lane per (source authority namespace, `WorldFederationLane` concern), so
connect, hello, and challenge are paid once per lane rather than once per
operation. A lane is strictly ordered, so transfer transactions and routed
traffic are kept on separate lanes rather than queueing behind each other. Only
a failure to connect takes a lane out of service; a break on an established
connection reconnects without entering backoff and re-sends only when
`ILaneProtocol.MayResend` says the kind is safe to send twice (`Submission`
never; the transfer-id-keyed kinds are idempotent at the host), otherwise the
request is answered `ConnectionClosed` and left in doubt. Each attempt runs
under a per-request deadline (`LaneRequestTimeout`): a peer that goes silent
after the request was written is answered `RequestTimedOut` with no re-send and
no backoff, and an unexpected exception from the dialect answers that one
request `LaneUnavailable` without killing the worker. A lane inside its backoff
window answers `LaneUnavailable` without touching a socket, which is what keeps
a closed edge from stalling the source's tick. A run that holds no federation
signing identity (no `--federation-key-file`) never opens a lane, an observer
session, or an intent stream at all: every request is answered
`LaneUnavailable` naming that, with one stderr line per authority, since no
connect could ever authenticate. An authenticator that verifies but cannot
prove (admission trust entries, no signing oracle) passes `IsConfigured`, so
the first proof it refuses is what reveals it; from then on the same gate
closes on it with the same answer.

Every document this codec writes goes out at the connection's disclosure tier.
`DisclosureFor` resolves it once per federation connection, through the same
`WorldAdmissionDoor.TryAdmitArrival` arm that decides what an arriving traveler
is minted; a namespace no `admission` row names gets `presentation`.
`EncodeDocument` writes `[tier byte][document bytes]` — a projection below
replica, the definition verbatim at replica — and `TryDecodeDocument` hydrates
the projection back into a `WorldDefinition` so the route answer, the
reservation reply, and the observation lane's `Definition` frame all keep their
existing shapes. Both arms hand back a document whose `state.<row>[.<key>]`
values are resolved, so a delivered definition is indistinguishable from a
file-loaded one and an arriving seat's binding recompose cannot fault on an
unresolved identifier; a projection leaf that still names a state cell is
refused as `PayloadMalformed`. The reservation leaf carries a
`WorldIdentityProjection` instead of the traveler's owned document.

An ordinary `Observe` stream attaches with the world's authored
`bodies.disclosure` and no observer body index. A narrowed policy
therefore cannot reveal embodied observations to that unembodied connection.
Remote snapshots are sampled at that policy's `updateSeconds` cadence (0.03 s
by default; 0 requests every authority tick). The sampler coalesces skipped
field writes, accumulates the delivered `StepTicks`, and retains one-shot
teleport/correction hints. This sampling occurs only at QUIC projection egress;
the local client and authority simulation remain full-rate. For large remote
crowds, combine cadence with `radius` or `selfOnly` disclosure rather than
shipping every visible body unnecessarily.

A transferred seat instead opens `ObserveTraveler` with its source-scoped mobility
credential. Its authenticated entry authority relays the current owner's
projection through the committed forwarding chain, including local worlds with
no network listener. At stream opening, every hop validates its own credential and
caps the requested document tier by its arrival policy; the request carries a
shared 64-hop limit. The final owner checks the traveler's Observe grant and
applies body-relative snapshot disclosure. A route seed precedes the definition
and snapshots. Ownership, definition, or final Observe-grant changes invalidate
the stream, and the client reopens through its original authenticated entry rather
than dialing a private world name. Projection queues are bounded; a slow consumer
disconnects instead of blocking the simulation. Disposing the client lease cancels
observation, and consumer disconnect detaches the server subscription even when
the world is paused.

A remote-admitted body is tagged `WorldPopulation.Entry.IsRemoteHuman`
(`IsAdmittedPeer` reads it) so `world.population`'s census lever can never
silently reassign or deactivate a connected human's body — see "The entity
table" above.

## Principals and grants (`WorldGrants.cs`)

Every write submission carries its acting `WorldPrincipal` — a seat, the
console, a named addon guest, or a generation-bearing `Peer(index,
generation)` — and one server-side table,
`WorldGrants`, is the single place a write is authorized. A grant row is
`(principal, capability, subject)` plus optional exclusivity, an untrusted
principal's per-tick dispatch budget, and the co-driving reach/consent pair.
Capabilities are `Drive`, `Observe`, `Control`, `Mutate`, and `Edit`
(`Present` was deleted 2026-08-02 — "contribute to what is drawn" is
`Mutate` over presentation-shaped sections); subjects are the `all`
wildcard, `body:<n>`, `screen:<n>`, `section:<name>`,
`state:<name>`, `composition` (the shared window-composition authority),
`creation:<id>`/`placement:<id>` (one creations/placements row apiece,
`Mutate`-only), or the two world-events-feed subjects
`region:<name>`/`seat:<n>` (legitimate
only for `Observe`), with a positive per-capability legitimacy rule
(`WorldGrants.IsLegitimateSubject`) so a new subject shape is refused by
default. `state:<name>` is the one subject that
narrows BOTH mutation kind pairs over one named row — the whole-row
`UpsertStateRow`/`RemoveStateRow` AND the per-cell `UpsertStateCell`/
`RemoveStateCell` (a slot is a table with one key, so there is one row and
one subject, never a separate `table:<name>`) — beneath its
own section-level `Mutate` hold — `Edit` over the concrete row, checked a
SECOND time at apply — rather than replacing it.

**Two mask payloads, two types, never one lane with two readings.** A grant
row may carry a `MutationKindMask` (`WorldGrant.KindMask`, ordinals from
`WorldMutationKindCatalog`) on a `Mutate` row over `section:<name>`,
`creation:<id>`, or `placement:<id>` — the dispatch door — or on an
`Edit`/`state:<name>` row, where it
separates the per-cell writes from the whole-row re-authoring beneath one
subject (`verbs:UpsertStateCell,RemoveStateCell` grants "bump the score"
without "redefine the score"). It may instead carry a `DocumentWriteMask`
(`WorldGrant.WriteMask`, `WorldDocumentWriteKind`'s `Set`/`Add`) on a
`Mutate`/`state:<name>` row — the cross-document durable-state write-back
channel `WorldOwnedWorlds.Decide` gates. `WorldGrants.CarriesKindMask` /
`CarriesWriteMask` state which row shape carries which, positively and in
one place; a mask offered on any other shape is refused by name. The two are
distinct C# types because they were one `ulong` once, read under whichever
vocabulary the row's subject kind implied — bit 0 meaning `UpsertKit` on a
section row and `Set` on a state row. An ABSENT kind mask means FULL reach
(opt-in narrowing beneath an already deny-by-default capability); an ABSENT
write mask admits nothing (that channel's mask is what admits a foreign
write at all). Both echo BY NAME through `world.grants` and `world.why`, in
the same `verbs:`/`writes:` spelling that authors them, and a mask denial
names the verb it denied.

Local play seeds permissively at boot (seats and the console hold wide
grants; addon guests hold nothing until granted), and a world document can
additionally ship grant rows in its `grants` section, applied at boot through
the same path the live `world.grant` verb uses. Every enforcement point asks
the table before acting — the intent drain, command application, mutation
application, whole-document swaps and undo, engagement, profile edits, and
addon dispatch — and a denial is loud and data-shaped (a named
`[world.grant denied: …]` line; the write drops). The read-back verbs are
`world.grants`, `world.why`, and `body.channels`.

Peer authority is never pre-seeded by index. Each admission or census
reactivation bumps the slot's generation, scrubs stale-generation grants and
engagement routes through the revoke door, then mints the new generation's
default Control grant through the grant door. Admission and disconnect are
tape-covered server events, so offline replay uses those same doors.

For untrusted principals, authority travels as handles rather than names:
`WorldHandleTable.cs` projects a principal's grant rows into per-instance
slots (never a whole-domain designation), stamped with the minting principal
and capability, and generation-checked so a revoked or re-sorted handle
refuses on its next use with a distinct verdict. The campaign that designed
this model was retired on 2026-08-10, its rulings moved into the code they
govern; what survives as WORK is carried in
[`docs/campaign.md`](../../docs/campaign.md). This README is the reader-facing
summary; the CODE outranks it on any point of disagreement.

Two settled rulings worth restating here because their absence is invisible:
ownership latching is unified through this table (control applications'
occupancy included — do not invent a parallel ownership mechanism), and the
authority decision is deliberately not modeled as a lattice or quotient
(see the state document's "What is NOT algebra" entry).

## Screen machines (`IWorldMachineHost.cs`)

A booted `IScreenMachine` (a diegetic screen's cartridge/cabinet —
`Puck.Abstractions.Machines`) is CORE state, not presentation-fed, but this
project carries no reference to the emulator cores or `Puck.SdfVm` — a
machine is a mounted guest, like a WASM addon. `IWorldMachineHost`, defined
here, is every member `WorldServer` and every offline re-drive (the replay
tape/inspector, a spawned `WorldInstanceHost` row) reach a booted machine
through; the concrete `WorldMachineHost` — the emulator cores, the Tune
instrument engine, and the boot/step/cable-link/reconfigure/memory-peek
machinery — lives in `Puck.World.Addons.Machines` (which references this
project, never the reverse) and is constructed by the composition root, a
peer DI singleton `WorldServer` takes as a constructor parameter typed
`IWorldMachineHost`, never a private field it builds, so the container
disposes the machines it holds. A caller inside this project that needs to
build one (a spawned instance's empty host, an offline re-drive's shadow
host) is handed a `Func<IReadOnlyList<WorldScreen>, IEnumerable<IScreenMachineEngine>,
string?, WorldOutputHub?, IWorldMachineHost>` factory by the composition
root — the same "the server calls out, the composition root supplies the
capability" shape `IWorldAddonHost`'s `addonHostFactory` uses.

Stepping runs inside `WorldServer.Step`, immediately after
`WorldEngagement.FoldTick`, fed that tick's per-screen pads directly
(`WorldEngagement.BuildPadSnapshot()`, in-process — no client/wire
round-trip). `screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink`
(`Puck.World.ScreenCommandModule`) submit a `WorldScreenOp`
(`Puck.World.Protocol`) through the ordered submission domain
(`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY like `Command`/`Grant`/
`Revoke` and checked against the ordinary grant table (`Control` over
`screen:<n>`) before the host is touched; `Insert` and a Machine-magazine
`Select` share one boot path (`TryBootMachine`) and are BOTH CAS-pinned
(`sha256-64` of the exact bytes read, or an `"absent"` sentinel when the file
could not be read at all) — a failed boot is reported as a failure, never a
disguised success, and the pinned signature rides the tape REGARDLESS of
whether the op succeeded (INCLUDING an unresolved engine — content is
read/signed before engine resolution is even attempted, never left unpinned
on that path), so a replay re-drive refuses by name if the file's on-disk
state no longer matches what was recorded. A content path ending in
the .cartridge.json suffix names an authored `puck.cartridge.v1` document: the host
compiles it through the engine's own forge once the engine resolves (the
signature still covers the bytes read off disk), boots the compiled image,
and records a `WorldMachineCartridge` (the source's canonical hash and the
image's hash) on `WorldMachineState.Cartridge` for `screen.state`; a forge
refusal is the bind's fault, verbatim. Declared cable links
(`IWorldMachineHost.ReconcileLinks`) are established/torn down at
construction (for a link declared in the boot document itself) AND on every
`WorldServer.Install` (every live mutation and every whole-document
rebuild) — never only once; the reconcile itself is two-phase and atomic
per call (every stale-or-changed declared link tears down FIRST, complete,
before anything (re-)establishes), so a re-shape that moves a screen from
one declared link to another within the SAME reconcile always succeeds
rather than silently failing while the old link still owns the screen.
Every op rides the replay tape (`WorldReplayEntry.ScreenOp`), and
`replay.record`'s arm gate refuses on THREE latches, none sufficient alone:
`WorldServer.AnyAddonEverPumped`, `AnyMachineEverPumped` (once any machine
has stepped), and `AnyScreenOpEverApplied` (once any screen op has applied
AT ALL, independent of stepping — screen ops apply synchronously, between
fixed steps, so an insert/eject/select/options/link/unlink can change live
host state before a single tick has run, which the other two latches would
miss) — offline replay reconstructs a FRESH host from the tape's embedded
definition, so a machine's accumulated core state (or a screen op's effect)
from before recording began can never be re-established, and the population
hash covers no machine state to catch the divergence. `Puck.World.WorldScreenBinder`
is a pure reader of this type's outputs for presentation (framebuffer
handle/light, `PublishFrame`) and still owns the genuinely presentation
screen sources (test pattern, authored QR, webcam, compositor capture,
jumbotron view) that are not this type's concern. See
`Puck.World.Addons/README.md` for the concrete host's own shipped-engine
list and boot/link mechanics.

### Machine memory bindings (`WorldServer.MachineMemory.cs`)

A screen row's `memory` array (`WorldScreenMemory`) is a standing mirror
between one address on the machine's bus and one kind=Int `state.world` cell
— distinct from an addon row's own `WorldAddonMemoryWatch` (an edge-triggered
event feed for a mounted guest, unfolded). `SyncMachineMemory` runs once per
tick, right before `IWorldMachineHost.Advance` steps every booted machine: a
`Write` binding reads the cell (`WorldStateReader.TryRead`) and, when its
value differs from the last value this binding successfully poked,
`IWorldMachineHost.TryPokeMessage`s it in (little-endian, low byte at the
declared address) so it lands before this tick's step — the memo updates
only on a successful poke, so an as-yet-unbooted machine is retried every
tick rather than silently latching a value it never delivered. A `Read`
binding peeks the machine (`TryPeekMessage`) and, when the value differs from
the last value it mirrored, applies one `WorldMutation.UpsertStateCell`
(`WorldPrincipal.World`) through the ordinary door — the same mutation shape
a rule-authored frame's own single-cell write folds into
(`WorldServer.RuleFrame.cs`), without this seam reaching into that frame —
and calls `WorldOutputHub.DeliverState`, matching every other engine-driven
per-tick cell write in this project (`WorldServer.Fields.cs`). Both memos are
keyed by (engine screen index, bus address), so an unmoved value costs one
dictionary lookup and nothing past it: the peek/poke round trip through
`Puck.GamingBricks.QueuedMachineWorker`'s marshaled worker thread is a real,
pre-existing cost every memory read/write pays regardless of caller (shared
by `screen.peek` and an addon's own memory watch) — what a quiet binding
elides is the mutation/install cost on top of it, not that shared floor.

## The addon host seam (`IWorldAddonHost.cs`, `WorldAddonReceipt.cs`)

`IWorldAddonHost` is every member this project calls on the mounted addon
guest host — the three tick-boundary pump points above, the
`TryPrepare`/`Commit`/`Finish` prepare/commit/publish transaction
`TryApplyMutation` (the `UpsertAddon`/`RemoveAddon` mutation's own last
fallible gate, refusing by name first when no host is attached at all),
`ApplyRebuild` (unconditional, for `world.reset`/`.load`/`.reload`),
`WorldAddonRuntime.TryCreate` (boot), and `ApplyUndo` each call, mutation
completion, and the undeclared-granted-channel disclosure. `Commit` is pure
reference adoption; `Finish` — narration and superseded-guest disposal —
runs only after the caller's own document/journal publication is durable,
so neither can unwind it. The opaque plan
crossing `TryPrepare`/`Commit` implements `IWorldAddonPreparedPlan`
(`IWorldAddonPreparedPlan.cs`), a bare `IDisposable` marker (plus a
`MountedCount` this project pre-sizes its per-tick addon contention
tracking against) this project declares so it never names the concrete
plan shape either.
`WorldServer` holds the host as `m_addons` and never names the concrete host
type; `WorldReplaySnapshot.Drive` takes an `addonHostFactory` delegate so an
offline re-drive can mount its own fresh guest set. `WorldAddonReceipt`
(one mounted guest's recorded-at-mount name/hash/fuel) stays here rather
than in `Puck.World.Addons` because this project owns the replay tape that
persists it. The concrete host — `WorldAddonRuntime`, the mount sequence,
the WASM guest ABI decode, the addon.mutate refusal catalog — is
[`Puck.World.Addons`](../Puck.World.Addons/README.md).

## Identity facts (`WorldServer.IdentityFacts.cs`)

A world declaring the reserved `identity` lane row (`WorldIdentityFactLane`, a
keyed `int` row in `state.world`) carries each seated identity's facts in that
body's cells, keyed `<bodyIndex>-<fact>`. The mirror runs once per tick inside
`LoadRuleFrame`, before any rule reads: a body whose `Profile` reference or
whose identity's `FactsRevision` moved since the last sync reloads its lane —
its cells the identity does not carry are written 0, every fact the identity
carries lands as a lane write — through the same `IRuleHost.TryApply` door a
rule's own cell write takes, so the writes queue on the frame, fold once at
the end of the tick, journal, undo, and replay like any rule-written cell. A
lane write that would leave a cell as it is queues nothing, so a quiet tick
moves no row version. Cells are never removed: a HUD binding a lane cell must
find it declared, and the row's authored `capacity` bounds bodies times facts.

`setIdentityFact` (`IdentityFactEffect`) resolves its body like `pose` does,
refuses by name a body driving under no owned identity (`IdentityUnbound` —
an anonymous seat's fact is refused, never minted), a document declaring no
lane, or a faulted expression (`IdentityFactUnwritable`), writes the lane cell
when the value differs (`Applied`; an unchanged value is `Skipped` and costs a
quiet tick what an unchanged ordinary write costs), and persists the fact on
the identity's own row through `WorldOwnedWorlds.TrySetFact` — the one door
the console's `identity.fact.set` shares — which saves the identity only when
its row changed. Inside a transaction the persist waits on the commit: a
preflight scope stacks its pending facts beside the frame's own journal marks
and a discarded scope drops them. The identity's `FactsRevision` moves on
every row change, so a console write reaches the lane on the next tick without
a second mirror path; the replay tape pins an identity's projection and not its
facts, so a re-drive of a tape recorded with a non-empty lane diverges at the
lane the way it does at a durable identity slot.

## Owned worlds and storage

`WorldOwnedWorlds` loads one `puck.world.def.v1` file per identity from
`owned-worlds` beneath the state root, plus any hand-placed basis chain link
under its `owned-worlds/basis/` subdirectory (outside the catalog's own
directory glob, so a link never enumerates as a second owned world). A document
whose BYTES are not a `puck.world.def.v1` document is DISCARDED, not tolerated:
the file moves once into `owned-worlds/unloadable/` (also outside the glob, so
it never enumerates again). Nothing distinguishes a retired document shape from
a corrupt file here, so neither is silently eaten and neither is migrated. A
refusal that can answer differently on the next boot — unreadable file, absent
file, unresolved `basis` link, or a validation claim resting on an adjacency
neighbour — is NOT discarded: those files stay where they are and are only
named, because the neighbour resolver reads the same directory a sweep would
empty. Each half reports as one stderr line grouping file names by their shared
reason, with the path stripped out of the reason. A quarantine destination that
is already taken takes an ordinal suffix rather than overwriting the earlier
copy, and the seeding pass that fills an emptied catalog from
`seatDefaults.identities` skips any id whose catalog path is occupied by a
file or directory, so a document left behind keeps its bytes and a stray
directory cannot crash startup, and `identity.create` refuses an id whose catalog
path is occupied for the same reason. `WorldOwnedWorlds.Discarded` and
`identity.list`'s `discarded=` column are the read-back for the disposals;
`WorldOwnedWorlds.Refused` and `identity.list`'s `refused=` column are the
read-back for the documents left in place. The
machine-local installation id stays separate in `machine.id`; controller
recognition is stored through named text state rows in the owned world.
`--user-id` and `--state-dir` still resolve who is playing and where those
worlds live. `WorldOwnedWorldSync` pushes and pulls those documents against the
per-user cloud container — one blob per world tip under `puck/worlds/`, ETag-guarded,
refuse-and-surface — when the composition root wires an endpoint and a resolved
identity. A world naming a basis pushes and pulls its WHOLE chain, not just its
flattened tip: each chain link lives under its own `puck/worlds/basis/{name}`
key, and a pull composing a chain-derived document writes each link to the
local `basis/` subdirectory (never a flattened file) so the next save keeps
writing a delta. Cloud version tokens persist in `owned-worlds/sync-state.json`
(tips and basis links tracked separately), and the `storage.push`/
`storage.pull`/`storage.status`/`storage.credential` verbs in `Puck.World`
drive and echo it.
`IObjectBlobStore` also exposes `ListAsync(target, objectId, keyPrefix)` (the
object-relative keys beneath a key path, matched by whole path segment — the same
key space a read or write address carries, whichever route served the list); a
whole-catalog `storage.pull` uses it to list the cloud `puck/worlds/` namespace
and DISCOVER worlds the catalog has never seen.

The platform edge (`AzureBlobObjectStorageTarget.EdgeNamespace`) cannot serve a
container list AT ALL — its path rewrite has no segment for a query-string-only
List Blobs request to occupy, so it 404s unconditionally before reaching blob
storage (verified live 2026-08-05). An edge-shaped endpoint therefore never
sends `ListAsync` through the edge: it routes to
`AzureBlobObjectStorageTarget.DirectEndpoint` — the world doc's
`storage.discoveryEndpoint` / its `--storage-discovery-uri` CLI reflection —
or `WorldOwnedWorldSync.DiscoverCloudIds` refuses whole-catalog discovery BY
NAME, before any network call, when no discovery endpoint is authored. A
genuine 404 through the direct connection (the edge-shaped container is
platform-managed and never legitimately absent) propagates as a named refusal
too, rather than reading as an empty prefix — only the raw/dev-emulator shape
(`EdgeNamespace` null, self-managed containers) swallows a 404 as "nothing
written yet."

Going direct means addressing a DIFFERENT layout of the same blob, and that is
the part easy to get wrong: the edge rewrite maps `/{namespace}/{container}/{rest}`
onto container `{container}`, blob `{namespace}/{rest}`, so what the edge route
addresses as container `{namespace}`, blob `{objectId}/{key}` is *stored* as
container `{objectId}`, blob `{namespace}/{key}`. The direct list therefore
enumerates the object's own container beneath a `{namespace}/` prefix — which is
also the only shape the per-user access policy grants — and strips that prefix
back off, so both routes hand the caller the same object-relative keys.
Enumerating the edge's view instead (a container named for the namespace) asks
for something no account layout has, and an emulator that has been laid out to
match the edge's view will pass while production 404s.

`WorldOwnedWorldFileName` (in `Puck.World.Schema`, because the earliest door that
has to enforce it is document validation) is the id↔file/blob-name mapping. It
escapes nothing: it takes a `SafeName`, whose fixed reserved-character set
(rather than `Path.GetInvalidFileNameChars()`) is what makes two machines on
different operating systems agree on the name an id maps to. That makes the
mapping injective into file-name STRINGS, which is not the same as into storage
LOCATIONS — the local catalog directory resolves names case-insensitively, while
the cloud object namespace is case-sensitive — so one id names one location only
under a **case-insensitive** uniqueness rule, held at every door: the document's
authored `seatDefaults.identities` seeds (refused by
`WorldDefinitionValidator`, so a case-variant pair never reaches disk),
`identity.create`, and adoption from a pull. The directory load holds the same
rule from the other side: a file whose name is not the one its declared id maps
to — ignoring case, because the filesystem's own resolution ignores it — is
refused and left where it is, so a case-only rename of a catalog file is
admitted rather than wedging the catalog. A pull additionally refuses a cloud document whose own
`identity.id` is not the id whose key was read, since adopting it would file the
document under one name and its version token under another; a listed cloud name
the mapping could never emit belongs to no reachable id and refuses by name in
the pull's outcome list rather than being silently dropped.

`storage.status`'s `lastWrite` reports the last push's actual outcome — `ok`,
`precondition-failed`, or `failed` — not the precondition bit alone.

The identity half is `Puck.World`'s `IPlayerStorageIdentityResolver`
(`WorldStorageIdentity.cs`) — an authored `storage.userId` / `--user-id`
override, or the local-only decline. There is no app registration and no
interactive sign-in: game clients ARE users, so a player's machine authenticates
ambiently and a hosted server runs as a user-assigned managed identity, both
through the one `DefaultAzureCredential` the blob backend already uses.
`storage.credential` probes whether that ambient credential can issue a storage
token from this machine and records the verdict for `storage.status`. Parsing a
STORAGE access token for identity remains ruled out — it says what a credential
is scoped to, never who is playing.

## Hosted worlds and the authority store

A hosted world's blobs live in a namespace sibling to, and never overlapping
with, the owned-worlds catalog above: `puck/hosted/{world}/…` for its
checkpoint/journal (never published), `private/puck/hosted/{world}/definition.json`
and `.../projection.json` for the pair the platform's public content edge
serves anonymously. One key writer, `WorldOwnedWorldSync.HostedAddressFor`,
computes both roots so a reader can never drift from it.

`IWorldAuthorityStore` (`WorldAuthorityBlobStore` over `IObjectBlobStore`) is
programmed against opaque encoded bytes throughout — `LoadLatestAsync` returns
the checkpoint blob's raw, hash-verified bytes plus its ordinal and tick, never
a decoded record; `WorldAuthorityCheckpointCodec` decodes what this store
hands back. A checkpoint write is content-addressed and
create-only (an identical retry is idempotent, verified by byte comparison on
a create-only loss), then the `checkpoints/latest` pointer moves under its own
if-match compare-and-swap; a journal page is a read-modify-write append under
the same discipline, relative to whichever checkpoint ordinal `checkpoints/latest`
currently names. `WorldAuthorityCheckpointCadenceCounter` counts master-step
engine ticks toward `WorldAuthorityCheckpointCadence.EngineTicks` and arms a
capture request a caller honours at its own next boundary; it never decides
whether a capture may proceed and never takes a row's own gate itself.

`WorldHostedOrigin` (a `WorldDocumentOrigin` arm beside `WorldFileOrigin`)
loads a hosted definition through `WorldDefinitionLoader`'s bytes entry — a
hosted definition is always stored already composed, so this load never
resolves a basis chain — and resolves its own `references[]` through
`WorldStorageNeighbourResolver`'s hosted-namespace arm
(`WorldStorageNamespace.Hosted`), the same resolver the owned-worlds catalog
uses with its default namespace.

Stored neighbours use the same composed-document parser and state-binding context
as local files. This resolves creation expressions such as `state.strideCadence`
before producing seam attestations. Adjacency claims are not recursively validated
while resolving a neighbour.

## Deterministic replay (`WorldReplayTape.cs`, `WorldReplayTape.Drive.cs`, `WorldReplaySnapshot.cs`)

The `replay.*` verb surface (`WorldReplayCommandModule`) lives in
[`Puck.World.Console`](../Puck.World.Console/README.md); it holds this
project's `WorldReplayTape`, `WorldReplayInspector`, and
`WorldReplayEntryDescriber` by their public surface, the same way every other
moved module reaches a Server type it does not own.

`replay.drive <name> [to <tick>]` re-drives a saved tape into the running
session: a forced `world.load` of the embedded definition plus the complete
boot authority checkpoint from a shadow server the recorded seats joined reset
the live world. This resets clocks, decisions, latches, fields,
grants, held input, and population together. `WorldServer.Advance` continues
from the restored clock; console waits retain a separate monotonic host-work
count, and local route epochs refresh so input can resume immediately.
Live replay refuses unresolved transfer reservations or
credentials, remote occupants, and host-owned transfer history. A tape owns
one authority's inputs; it cannot rewind obligations held by another world.
The ownership check and reset share the authority gate, so concurrent
federation ingress cannot reserve between them.
`WorldServerStepShell` feeds one recorded
tick through `WorldReplaySnapshot.ApplyRecordedTick` ahead of each live step
(the same apply the offline drive uses), `LoopbackTransport.InputMasked`
drops local seat intents and commands for the drive's span, and the first
live-vs-recorded hash divergence is narrated on stderr without stopping.
`replay.fork <name> <tick> <new>` fast-forwards the same drive to `<tick>`
(a burst of recorded ticks per shell call) and hands over to a recording
whose leading tick groups are the parent's, with `ForkedFrom` in the header;
the child is standalone. `replay.record <name>` captures the running session's record-start definition,
active seats, mounted-guest receipts, and the per-tick server-input stream,
while sampling both the LIVE population hash and authoritative state-system
hash; `replay.stop`
persists `<name>.puckreplay` and re-drives it once; `replay.verify <name>`
rehydrates a fresh boot-image world, re-drives the stream offline, and
reports MATCH or MISMATCH naming the first divergent tick (tick 0 indicts the
starting state; any later tick is a real trajectory divergence). A receipt
disagreement — the live tree moved past the recording — refuses loudly with
no verdict; a recorded mutation's accept/refuse outcome disagreeing with what
the replay's own apply pipeline produces refuses loudly by name too
(`MutationOutcomeMismatch` — see [addons.md](../../.claude/skills/puck-world/references/addons.md)'s prepare/commit
transaction); a codec defect (`Puck.World.Protocol.WorldReplayCodecException`)
reports as a host bug, never folded into either refusal. `replay.inspect <name>
[<from>-<to>] [--all] [--poses]` (`WorldReplayInspector.cs`,
`WorldReplayEntryDescriber.cs`) is the tape's read-back: the header facts,
then one line per tick carrying the recorded hash beside what changed that
tick (authority entries, intent channel edges); `--poses` re-drives through
the same `Drive` and prints each active body's pose per line, naming the
first pose-divergent tick. The MATCH/MISMATCH verdict uses the authoritative
trace; the pose trace remains the human-readable trajectory diagnostic.
Presentation (screen pixels,
cameras, overlays, audio) is excluded by design: a match proves the covered
state-system lanes, not the whole document, grant table, HUD, or machine cores. Known scope limit — the tape
captures every one of the twelve envelope payload kinds except `Lever`
(command, grant, revoke, session, designation, rebuild, mutation, undo,
composition, query, and screen-op) plus intents and the two
peer-lifecycle server events; a mid-session capture honestly reports
MISMATCH at tick 0 — carried in
[`docs/campaign.md`](../../docs/campaign.md).

## Verifying a change here

No build gate covers this project's behavior; verify by RUNNING `Puck.World`
over stdin. The apply pipeline's all-or-nothing contract (a mutation that
fails whole-document validation leaves the live definition byte-identical) —
the same gate `WorldServer.ApplyUndo`'s journal-replay loop passes each kept
entry through — is proven in-process by
`tests/Puck.World.Tests/MutationAllOrNothingLawTests.cs`; that suite does not
construct a genuine mid-replay validation failure, so the replay loop's own
early-return is unproven beyond code inspection.

No committed battery covers the ordered-domain envelope's ordering
contract. Verify it live instead: one stdin batch interleaving a grant and
the command that needs it, plus the reversed order as the discriminating
control.

Principal/grant enforcement (denial/control pairs per player-facing verb) is
proved by `AuthorityAdministrationLawTests`, `EngageAuthorityLawTests`, and
`ControlApplicationLawTests` in
`tests/Puck.World.Tests`.

A change that moves simulation math is expected to change replay hashes;
re-record any persisted tape it invalidates in the same change (`CLAUDE.md`
rule 4).

Adjacency/federation changes additionally run
`puck canary four-corners-sharded`. It starts five distinct authorities
(four ground worlds plus the floating island) and exercises generation-
addressed forwarding through a full four-ground-authority human circuit.
The automatic smaller proof is `puck canary seamless-adjacency`.

Verify a network-transport change by running two `Puck.World` processes: a
headless host (`--headless --listen <ip:port> --state-dir <tmp>`) and a
`--connect <ip:port>` client, both scripted over stdin — `world.peers`/
`world.grants peer:<index>:<generation>` on the host prove admission and the
disconnect-driven revoke; the client's own query replies prove the Completion
lane round-trips. No persisted battery exists for this yet (a live owner
conversation about runner disposition); do not add one without asking.

Discrete tabletop and tactics state is folded by `WorldStateTransforms` through
the existing mutation journal and transaction preflight. `WorldBoardQueries`
reads bounded topology scratch spans; physical fields remain a separate runtime
allocation. `StateObservations(row)` passes `observe state:<row>` and then the
row/cell audience policy for the authenticated submission stamp. Observation
payloads carry literal cells only. See the
[document contract](../Puck.World.Schema/README.md#discrete-boards-cards-and-turns)
for topology addressing, phases, private draws, knowledge refresh, and limits.
