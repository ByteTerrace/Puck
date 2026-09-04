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
`Puck.Storage`, and `Puck.Hosting`. The addon guest runtime itself is
[`Puck.World.Addons`](../Puck.World.Addons/README.md), which references this
project rather than the reverse — see `IWorldAddonHost` below.

## The tick (`WorldServer.cs`)

`WorldServer.Step` advances one exact fixed tick, in a pinned order its own
XML documentation states: tick the mounted addon guests
(`IWorldAddonHost.TickAddons` — decodes and validates, applies nothing) →
drain the buffered live edits (mutations and whole-document swaps) → drain the
buffered intents → apply the guests' contributions
(`IWorldAddonHost.ApplyContributions`) → fold each human-occupied body's
contributions (`FoldChannelContributions`) → settle per-body contention →
advance every body → resolve the guests' reads
(`IWorldAddonHost.ResolveReads`) → deliver the tick's `WorldSnapshot`.

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
`WorldOutputHub.cs`, which supports multiple subscribed sinks.

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
state-cell keys). Only state-backed and social facts are admitted; live body,
channel, navigation, and machine facts could change midway through movement and
make an observer depend on body iteration order. Social observations produced by
world rules therefore affect the next eligible perception sample, not movement
that already happened. State handles and dimension ordinals recompile on document
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
reduce term strength. Unknown beliefs use the authored dimension baseline;
confidence is a separate input, and nothing creates reciprocal friendship.
See the [authoring example](../Puck.World.Schema/README.md#social-flock-affinities).

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
Flock weights alone imply no containment, social memory, shared routes, or
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

`WorldNavigationRuntime` compiles each authored domain once at boot/rebuild.
Surface cells use `TryGroundHeight`, lower/head clearance, slope and step
limits; every admitted edge is proven with swept spheres and stored in one
26-bit mask per cell. Free-volume and medium domains use the same swept-sphere
edge proof in three dimensions. A medium additionally resolves its field name
to an ordinal once and checks the agent clearance volume at each live node plus
half-cell-or-shorter swept boxes on search and before following the next cached
edge. Every intersected voxel's free surface is checked, including dry caps
between wet layers; testing only point samples or cube corners is insufficient.
Each swept piece visits at most 27 voxels, with a hard subdivision ceiling.
This is how underwater
routes react to field evolution without rebaking static solids.

Without `shared`, search is bounded A* over reused arrays: integer costs (1000/1414/1732), stable
`(f, h, nodeOrdinal)` ties and authored expansion/path ceilings. Domain search
workspace allocates once at compile; per-body route storage allocates lazily on
first use, so steady-state searches allocate nothing. Changing a navigation definition,
clearing designations, switching producers, or transferring authority clears
the local cache; checkpoints carry active routes and the codec validates every
domain, node, waypoint, status, and budget before restore. The authoritative
state hash includes producer-domain and route state. `world.navigation`,
`body.targets`, and `world.budget` expose occupancy, state, expansions, the
current followers' simultaneous-replan ceiling, and fixed workspace bytes;
`$nav:` rule facts read the same live status.

A domain can instead declare `shared: { "goalCapacity": 4,
"expandedNodesPerTick": 128 }`. This runs queued reverse Dijkstra searches,
with stable `(cost, nodeOrdinal)` ties and one aggregate expansion allowance
per domain per simulation tick. Each expansion inspects at most 26 edges.
Resident goals take turns, one expansion at a time; unfinished requests continue
on later ticks. The shared tree can eventually cover every cell in the domain;
`maxExpandedNodes` remains the independent A* limit, while `maxPathNodes` also
bounds paths extracted from shared trees. Boot allocation and checkpoint size
are bounded by the world's sum of `cellCount * goalCapacity`.

The cache key is the domain and destination cell—not a leader, friendship, or
body slot. The domain fixes topology, clearance, and medium compatibility;
shared volume/medium users must fit its root-centered clearance sphere.
Each body retains its own waypoint array/cursor and exact designated final
point. A body can leave, change goal, or reconnect from another in-domain cell
without changing another body's route. Reconnection extends the same bounded
tree if necessary; there is no straight-line teleport or unbounded connector.
Completed trees are evicted least-recently-used using bounded, distinct recency
ranks, so repeated requests cannot erase the order of other goals. Pending requests pin a tree
for the next search step. If all slots are pinned, another goal reports
`capacity` and can retry; no independent search bypasses the budget.
`pending` is distinct from `unreachable`. These are ordinary `$nav:` facets,
alongside `hasPath`, `active`, `arrived`, `remaining`, and `unreachable`.

Checkpoint/hash state includes resident goals, discovered costs/successors,
settled flags, pending starts, eviction ages, and the scheduler cursor. Heap
layout is derived. Node digests are cached in 64-cell blocks; only changed blocks
are rehashed, and unchanged trees contribute cached roots in constant time.
Pending starts are hashed in sorted order from their bounded request list, not
by scanning the domain. These digests are derived and rebuilt after restore.
A change to the referenced medium field invalidates its trees;
other fields do not. Rebuilds invalidate domain-local caches. This first shared
implementation restarts affected trees rather than incrementally repairing them:
rapidly changing water can delay a distant request indefinitely under a small
budget. It does not provide crowd collision avoidance, bottleneck reservations,
hierarchical long-distance routing, or social/group membership.

The many-agents/one-destination approach is informed by
[Emerson's crowd pathfinding chapter](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf).
This implementation uses finite domain trees, not that chapter's tiled hierarchy.

**Mutations, the journal, and undo.** A `WorldMutation` applies by composing a
candidate document, revalidating the WHOLE document through
`WorldDefinitionValidator`, and only then swapping, journaling, and rebuilding
the changed section's derived state; a failure rejects loudly and changes
nothing. The journal is the undo engine: `world.undo` restores the loaded base
definition and deterministically replays the journal minus its tail through
the same apply path — no per-mutation inverse exists. Market listings, bids,
buyouts, cancellations, and settlements are economic finality barriers:
`world.undo` may remove later authoring edits, but refuses before crossing one
of those entries. Retention pruning moves no value and remains undoable.
`world.save` writes a canonical session snapshot and compacts the journal (the
saved definition becomes the new base). `world.reset`/`world.load`/`world.reload` are ONE
rebuild-and-swap mechanism (`WorldServer.ApplyRebuild`) over three document
sources — the server's own base, a different file, or a re-read of the
current origin — that also wipes and re-seeds the ENTIRE runtime grant table
(`WorldGrants.Reset`, replaying only the new document's own `Grants` section
plus every admitted peer connection's re-minted admission grant; every other
live `world.grant` acquisition drops). The `dirty` count in `world.status` IS
the journal length.

World-rule failures accumulate in a fixed-size category table. The first
occurrence is narrated; repeated Level-rule failures only increment their
counter. `world.rule.failures` reports the count and latest tick/rule/effect/reason.
Rule/interaction installation is also guarded by a static aggregate work budget,
reported beside evaluation slots in `world.budget`; dynamic body-index keys use
a prebuilt string cache on the evaluation path.

**Lifetime sweeps.** Five per-tick passes run side by side at the end of
`WorldServer.StepCore`, each firing ORDINARY mutations under
`WorldPrincipal.World`'s structural exemption so recovery is journalled rather
than a bespoke erase: `ReclaimExpiredEscrows` (an unaccepted ownership offer),
`SettleExpiredMarketListings`/`PruneExpiredMarketListings` (a
listing past its deadline, a terminal row past `market.retentionSeconds`),
`SweepContributionTenure` (`WorldServer.Contributions.cs` — a presence-tenure
contribution slot whose watched `adjacencies` row has read dropped past the
slot's own `graceSeconds`), and `SweepPlacementResponses`
(`WorldServer.Responses.cs` — right after `StepFields`, so it reads this
tick's own lattice writes: the first `WorldPlacementResponse` entry whose
condition holds at a placement's coupled cell becomes its prototype). The
contribution sweep reads link liveness through `WorldServer.TryLinkLiveness`,
which pairs `WorldEventFeed.LinkStalenessTicks` with the row's compiled
`livenessGraceSeconds`; its retraction defers, rather than proceeding, while
the slot's inhabitant is drive-possessed. Market settlement is journal-final;
market retention pruning and the other recovery mutations remain undoable.

**Steady-state performance contract.** The per-tick pipeline — intent fold,
sim step, snapshot emission, binding resolution — allocates nothing; document
and JSON work is confined to the boundaries (load, save, and mutation
application), and a mutation rebuilds only the changed section's derived
state, never the whole document's. The binding half of that claim carries one
documented bound: `InputRouter` folds a signal's per-command memos in a stack
buffer sized for 32 bindings on one source, far above what one page plus the
host plane authors, and a signal that exceeded it would fall back to a heap
buffer for that fold alone.

## The field lattice (`WorldFieldLattice.cs`)

The live cell values of a `state.lattices` topology, and the reactions that
evolve them — simulation state beside the population, values `FixedQ4816`,
every reaction integer arithmetic in a fixed cell order, so one document and
input reproduce the same fields bit for bit. `WorldServer.StepFields` runs
after the rules (so a tag a rule wrote this tick is what an `emit`/`expose`
reaction reads this same step) and before the snapshot (so the step's cell
writes ride this tick's delivery), on the topology's own `stepEveryTicks`
cadence. A reaction scalar (literal or `{"row": "name"}`) resolves through
`ReadScalarSlot`, the SAME `WorldStateReader.TryRead` seam every other state
read uses — a season row a rule writes and a reaction reads can never
disagree about the value. `expose` writes land through the ordinary
`UpsertStateCell` mutation (`WorldPrincipal.World`, journaled, undoable), never
a bypass. Cell values are checkpointed (`WorldFieldCheckpoint`) and delivered
as `FieldCells` deltas on the snapshot (`FieldsFull` on a primer) — never
document rows, so nothing journals them directly.

`WorldFieldLattice` receives the complete `WorldFieldsSection` companion plus
the already-compiled `WorldFieldProgram`: the companion remains authoritative
for topology, cadence, paint, and presentation, while the typed program is the
one executable reaction IR. `StepFields` reads and writes reaction state by
`WorldStateHandle`, not by repeating row-name lookup. A whole-document rebuild
may replace compatible reactions in place without resetting cells, deltas,
revision, or checkpoint shape; adding/removing a lattice or changing topology,
cadence, or a field envelope refuses and asks for a host restart. The
`world.fields` read-back includes installed node order, dependency edges, and
cell/body pass counts.

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
action state. Bodies advance against the one contact-resolution seam
`IContactField.cs`, which has two providers: the analytic `WorldColliderSet`
(document-derived convex colliders) and the SDF-backed `WorldSolidField.cs`.
Both include solid scene rows, screen frames, and the shapes emitted by solid
creation placements. The field compiles those surfaces into one fixed-point
signed-distance program. The analytic provider emits exact isotropically
scaled spheres and world-axis bounds for other finite placement primitives;
rotated, rounded, non-box, smoothed, and boolean-carved geometry is therefore
conservative there. A solid row participates in simulation, which is why
mutating scene, screen, creation, or placement geometry is a real authority
widening.

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
frame/reseat/turn fractions, and complete hold/grapple state through
`IntegrationResidue`/`WorldAuthorityCheckpointCodec` (`SupportedVersion`,
bumped whenever the fail-closed wire shape changes).

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
action track, or hold runs. Static-world contact is a swept, substepped
integration against the SAME `IContactField` every locomotion body resolves
against. Substep count is derived per body per tick from its speed and the
collider's own bounding radius against an authored travel fraction
(`collision.bodyContacts.rigidSubstepTravelFraction`), capped by
`collision.bodyContacts.rigidSubstepCeiling`; the derived count is echoed in
`world.budget`'s `rigid` segment and `RigidStaticSubstepsThisTick`.

The ground (walkable) and obstruction (wall) contacts `IContactField.Resolve`
reports are independent channels — a grounded body still bounces the first
time it clips a wall — so each carries its OWN rising-edge restitution latch
(`m_rigidGroundContacting`/`m_rigidObstructionContacting`, `WorldBody.ResolveRigidContact`):
restitution fires only on a genuine impact on THAT channel, never on
continued contact, which would read gravity's own per-tick pull (or ongoing
sliding contact) as a fresh hit and never let the body settle. Tangential
(slip) friction at either contact is a real coupled impulse, not a lever
formula: the contact-point velocity (linear plus the rotational contribution
`ω × r`, `r` the collider's bounding radius along the contact normal) decays
toward the authored per-second `friction` rate through
`Puck.Physics.FixedTwoBodyKernel`, with the world modeled as an infinite-mass
static phantom (`WorldBody.GroundPhantomHandle`) — so linear and angular
motion stay coupled exactly as inertia dictates, and a spinning ball can
genuinely start rolling (or a rolling one stop spinning) rather than the two
evolving independently. `rollingFriction` remains a separate pure
angular-velocity decay while grounded — rolling resistance, not slip
friction. Friction, rolling friction, and both damping coefficients are
authored per-second RATES, applied as `(1 - rate·dt)` each tick so the same
value decays identically at any simulation rate; the rest thresholds and hold
window (`collision.bodyContacts.rigidRestLinearSpeed`/`rigidRestAngularSpeed`/`rigidRestHoldSeconds`)
are authored the same way, defaulting to the engine's original hard-coded
values.

Dynamic-vs-dynamic rigid contact rides the SAME broadphase/narrowphase
`ResolveDynamicContacts` already runs for two `solid` kits
(`FixedDynamicBodyContacts.TryCorrection`, whose correction direction points
from the second body toward the first): when at least one side is rigid,
`WorldPopulation.ResolveRigidPairContact` replaces the plain positional split
with an impulse computed through `Puck.Physics.FixedTwoBodyKernel` — the
kernel's own contract names its "A" side the body the contact normal points
AWAY FROM, so the pair's roles are assigned to match that direction exactly
(swap them and an approaching pair reads as a positive, separating, closing
speed). Each rigid side's contact anchor is its own bounding-radius surface
point facing the other body, never the body center — real torque, not a
torque-free strike, reaches both sides when both are rigid. A resolved
pair's tangential (friction) impulse is likewise a real Coulomb impulse
through the kernel — the full-stick impulse that would zero the relative
tangential velocity, clamped to the pair's average friction coefficient
times the normal impulse just applied — never an independent rescale of
either body's whole velocity vector, which would burn or invent momentum
along the normal too. Below a small closing-speed floor
(`RigidPairRestitutionThreshold`) restitution is treated as zero — a rigid
pair carries no per-pair rising-edge latch, so without this floor two
touching bodies would restitute a hair apart every tick they are found
overlapping and never fully settle. A kinematic (locomotion) side builds a
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
below threshold for a short hold window while grounded; `body.impulse`
(`WorldCommand.RigidImpulse`, checked for `IsRigid` server-side and refused
by name otherwise) wakes it. `$physics:quiescent`
(`WorldPopulation.RigidBodiesQuiescent`) reads 1 when every active rigid body
rests, vacuously 1 for a world authoring none. `world.rigid` echoes the live
census plus the compiled rest/substep policy; checkpoint (`IntegrationResidue`)
and the authoritative pose hash (`WorldReplaySnapshot.HashState`) both cover
linear/angular velocity, the resting latch and hold-tick counter, and BOTH
restitution edge latches. A kit swap that adds or drops the `rigid` facet
resets every rigid-solver field (`WorldBody.RecompileKit`) rather than
leaking the other kind of body's stale state forward; a live retune that
keeps the facet carries its velocity through unchanged. Cross-world transfer
of a rigid body is refused by name (`WorldInstanceHost.Transfers.cs`).

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
nested grant or market-party validation.

### Social memory component

`WorldSocialMemory` stores what one individual has learned about another,
separately for each named dimension. An optional `state.social` policy installs
the bank in the server. World rules deliver explicitly perceived evidence through
`observeSocial`, query beliefs through numeric `social` expressions, and remove
impressions through `forgetSocial`. This is not a sensor: the author must gate
who witnesses or receives an event. The component API also accepts authorized
`WorldSocialEvidence` directly; `TryRead` returns the current impression and
confidence, and `Capture` exposes stored receipts and work counters.

The server advances the bank once per engine-clock boundary, before ordered
mutations and rule evaluation. Effects execute in document order, so a later
effect, gate, or decision can read an earlier observation in the same tick.
Unchanged policy content retains memory through recompilation. A changed or
removed policy resets it; old dimension ordinals are never reinterpreted. While
source transfer holds or destination import reservations remain unresolved,
mutation, load/reload/reset, and undo refuse a policy replacement before changing
the live definition, journal, or derived state. Equal detached policy content and
unrelated edits remain admissible. A full authority restore instead reinstates
the checkpoint's validated memory and holds together.
Authored rules have the same structural authority as other world effects:
permission is checked at rule authorship, not through nonexistent World grant
rows. These runtime-only writes do not broadcast private beliefs into the public
world document, and are not admitted inside state-cell transactions.

`world.social` is operator inspection, requiring the stamped caller's
`Observe/all` grant. With no arguments it reports policy, clock, limits, and the
last outcome. A query JSON argument reads one directed impression. This is not
a per-creature network disclosure API. `world.budget` includes declared social
storage and ingestion/expiry budgets; rule costs include numeric expressions
and any row scans used to resolve their body references. The authoring syntax
lives in [Schema](../Puck.World.Schema/README.md#social-evidence-and-belief-queries).

The full authority checkpoint includes the social policy identity, impressions,
receipts, frozen source observers, import reservations, work counters, clock, and last outcome. Restore validates detached
records before allocating the new bank or changing live state. Social decoder row counts must fit their remaining
wire bytes before allocating. The existing whole-checkpoint 64 MiB wire limit
still applies; maximum component capacities are not a guarantee that a full
authority checkpoint fits that envelope. Individual reservations carry private
observer exports inside the federation protocol's separate 32 MiB frame limit.

`CaptureObserver` copies one original incarnation's memory, including receipts
whose impressions were forgotten and expired receipts awaiting reclamation.
It traverses only that observer's entries, then sorts those entries into a
canonical checkpoint; it does not scan the authority's other memories. Receipt
ordinals are compacted without moving an old event across a forgetting boundary.
Unrelated observers' event counts and authority work counters are not exported.
The result is detached and retains the exact policy. Without a destination clock
it retains the source clock; supplying `engineTick` rebases the stored aging
anchors while preserving age, decay progress, and original event timestamps.
An anchor may precede the destination's clock origin, so it is signed 128-bit
engine time rather than an unsigned tick that could underflow. Rebasing models
an instantaneous logical cutover; it does not guess transport delay from wall
time. A rebase outside the signed representation refuses without changing the
source. Restoring creates an independent bank with a fresh ingestion allowance;
neither operation reserves destination capacity, removes source ownership, or
by itself performs a transfer. Capturing many individuals at once still allocates
and sorts all of their selected records.

`RemoveObserver` retires one original incarnation's locally owned impressions
and receipts, without touching what other individuals remember about it. It is
not `Forget`: removing the receipts deliberately ends this bank's deduplication
protection for that owner. Callers must secure any required durable copy and
resolve ownership first; temporary separation or an ambiguous transfer must not
call it. The operation allocates nothing, visits only that owner's records,
and removes each receipt from the indexed expiry heap in logarithmic time.
There are no deferred heap tombstones to accumulate during repeated crossings.
It preserves the bank's clock, work counters, and next admission ordinal. This
component operation is not the handoff's release operation: confirmed transfers
use matching-key `RetireFrozenObserver` instead.

`TryFreezeObserver` holds an incarnation's entire history for one exact transfer
key, including empty and receipt-only histories. Equal retries retain the first
freeze clock; competing keys and incoming reservations refuse. While held,
`Observe` returns `ObserverFrozen`, `Forget` returns false, and ordinary
`RemoveObserver` throws. Held receipts leave the expiry index, so they neither
expire nor block other observers' bounded reclamation. Their storage remains
occupied. The maximum number of simultaneous source holds is
`WorldBodiesLimits.CapacityCeiling`; this includes empty histories.

`CaptureFrozenObserver` copies the exact freeze-time history for its matching
key. It remains logically identical across unrelated learning, clock advancement,
and checkpoint restore. Ordinary reads still show current lazy age. No second
full history is retained internally: freeze/thaw visit only the owner's receipts
and allocate nothing, while capture remains an allocating cold path. The hold
metadata is checkpointed and hashed, but is not carried inside an observer export.
`world.social` reports whether a queried observer is frozen; the budget reports
the total held observers.

After a confirmed non-commit, `ThawObserver` releases only the matching hold and
returns receipts to normal bounded expiry without refreshing their age. After a
confirmed destination commit, `RetireFrozenObserver` removes the matching hold
and all locally owned records, leaving others' memories about that creature intact.
A deadline alone proves neither outcome; unresolved transfers must retain their
source holds until ownership is established.

`TryImportObserver` atomically adds an absent incarnation's exported memory to
an existing bank. It validates the complete incoming history before writing,
rebases age onto the destination clock, and assigns new local receipt ordinals
without losing forgetting boundaries. It neither overwrites an existing owner
nor evicts memories to fit. Malformed records, mixed observers, insufficient
total/per-observer storage, and clock or ordinal overflow refuse without changing
the destination. The source policy must exactly describe the checkpoint;
destination capacity and work budgets may differ, but its dimension declarations
(including order), learning coefficients, reliability rules, and evidence lifetime
must have the same meaning. Differing semantics refuse rather than reinterpret
the individual's memories. Validation copies and scratch scale with the incoming
records, not the source policy's maximum capacities. The operation preserves
destination work counters and is not ordinary evidence ingestion.

`TryReserveImport` holds both storage and absent observer identities for an
ordered group under one `WorldTransferKey`. Ordinary learning and unreserved
intake cannot consume the held slots; even a zero-record arrival has an exclusive
identity claim. Equal retries are idempotent, changed retries refuse, and the
caller retains ownership of its original list. The total number of outstanding
observer claims is bounded by `WorldBodiesLimits.CapacityCeiling`, including
empty claims. Memory-entry quotas remain independently authored. Reservation
counts and their cached logical digest enter checkpoints and state hashes;
`world.social` and `world.budget` echo groups, observers, and held storage.

`TryImportReserved` validates every incoming member, its source policy, its own
allowance, and all clock/ordinal arithmetic before applying any member. Quotas
are checked against detached records as well as caller-supplied collection counts.
A late
refusal leaves the whole group and reservation untouched. Success consumes the
reservation and releases any unused allowance; it preserves ordinary work
counters. Preparation allocates scratch proportional to the incoming records,
not bank capacity. `CancelImportReservation` releases only a matching hold and
does not remove memory. Clock advances never implicitly release holds: the
enclosing transfer owns its deadline and must cancel an expired or aborted
reservation explicitly. Checkpoint restore reconstructs the same holds before
ordinary learning resumes, and rejects duplicate, overlapping, or over-capacity
claims. `CaptureObserver` never exports the source authority's reservations.

`TryPrepareReservedImport` returns an owned, single-use token without changing
the bank. `TryCommitReservedImport` installs that token without allocating;
changing the bank, reservation instance, clock, or admission ordinal invalidates
it before any write. Updating an existing receipt without a new admission does
not invalidate it. This split lets body admission follow complete memory validation.

The host freezes each traveler's memory before requesting destination space.
`WorldTransferEscrow` owns a detached copy of that export and reserves both its
observer identity and exact storage quota alongside the body slots. No-source-bank
arrivals receive empty identity claims when the destination has a social policy.
A destination without a policy refuses any supplied social export, including an
empty one; incompatible memory meanings also refuse. Transfer policy JSON is
bounded to 65,536 UTF-16 characters before parsing. Capacity and work budgets may
differ without changing memory meanings.

Commit validates and prepares all histories before landing any body. Only after
all bodies land does it install the prepared histories; a refusal releases the
body and memory leases. Exact retries cannot replace the saved histories or
rewrite reply slots. Authority restore checks that each saved body lease has its
matching social quota before changing live state.

The source retires its frozen records only after confirmed destination commit.
A lost commit response retains body recovery and frozen memory for exact status
reconciliation. Confirmed non-commit restores bodies before thawing their memories.
An occupied source slot retains its pending recovery; a rollback-only checkpoint
keeps only the remaining paired body/profile records and can never retry Commit.
Restoration reinstalls the original mobility identity even if that slot was reused.
A contradictory peer commit verdict after rollback leaves recovery held and
reports once; it cannot create another body or stop unrelated worlds.
Non-atomic parties split before reservation, so a parent lease cannot block its
own children. Each child has its own capacity verdict and memory transaction.

This protocol uses the freeze-time logical cutover described above, not an
estimate of network delay. Component stress and replay MATCH alone do not prove
federated delivery; transport and host recovery require their own checks. The
host retains unresolved destination identities, remote endpoints, exact commit
payloads, and frozen social histories through repeated capture and restore.
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

Use `WorldMobilityIdentity.Incarnation` for observer, subject, source, and event
origin identities. A current body index is not a durable individual: the next
occupant of that slot must not inherit the previous occupant's relationships.
The bank does not find bodies, disclose private intent, or certify an event's
provenance. Its caller must enforce those boundaries before submitting evidence.
An event's origin, aspect, sequence, and original occurrence tick must survive
relaying. Giving a rumor a new identity every time it is repeated defeats exact
deduplication and is a caller error.

An event's original `OccurredAt` and its local aging anchor are separate. For a
new event, the component caller may supply `LocalOccurredAt` to project the
original instant into the receiving bank's clock; otherwise `OccurredAt` must
already use that clock. The caller owns the projection's provenance, including
after a receipt has expired and been reclaimed. A retained receipt uses its own
anchor regardless of a relay's offered projection, so a repeated report cannot
extend its admission window. World-rule observations currently use the local
clock; the component does not infer an unknown foreign event's age from its
timestamp alone.

Dimensions have authored baselines, bounds, inertia, learning rates, and maximum
per-event changes. Source reliability is an independent, optional [0,1] dimension,
so liking someone is not the same as believing them. Quality and source reliability
scale reports. Only a new direct event receives an uncertainty-driven follow-up
boost. Repeated copies add no support; the first contradictory report about the
same event may raise uncertainty once, and a later direct observation can correct
that report once without counting as another independent event. The first direct
observation then dominates later copies. This is bounded game logic, not a claim
to infer objective truth or calibrated psychological probabilities.

`Forget` removes an impression but keeps its unexpired receipt history, including
across later relearning of the same individual. The exact ledger never evicts an
unexpired receipt. Full storage returns `ReceiptCapacityLimited` or
`ImpressionCapacityLimited`; the caller decides whether to defer or discard an
attempt. Ingestion counts invalid and duplicate attempts against an authored
per-boundary budget. Unfrozen receipt expiry uses a separately bounded oldest-first heap, ordered
by projected occurrence time and admission ordinal. A large clock jump grants one
budget, not an unbounded catch-up loop. Unprocessed expired entries may therefore
temporarily keep the ledger full.

Optional weight decay and recovery toward baseline are evaluated lazily from
the last accepted update; reading never rebases the clock. Imported ages beyond
the unsigned 64-bit read-back range saturate that read-back, without truncating
the stored aging anchors or overflowing decay arithmetic. Simulation arithmetic
uses Q48.16 values and widened integer intermediates, truncating toward zero at
each narrowing. Storage is reserved at construction. The cached logical-state
digest excludes dictionary layout, the per-observer ownership indexes, and the
expiry heap's layout; checkpoint restore rebuilds those indexes and validates bounds, identity,
duplicates, and capacity into a new bank before exposing it. The digest is for
replay diagnostics, not cryptographic authentication. Component laws, numeric
oracles, per-observer capture/forgetting/retirement laws, and the 4,096-observer
allocation, capture, repeated ownership-replacement, and component round-trip probes live in
[`WorldSocialMemoryLawTests`](../../tests/Puck.World.Tests/WorldSocialMemoryLawTests.cs).
Reserved quota, empty-identity claims, late-failure atomicity, restart, and the
4,096-observer reserved-group round trip are covered by
[`WorldSocialImportReservationLawTests`](../../tests/Puck.World.Tests/WorldSocialImportReservationLawTests.cs).
Source hold, stable export, thaw, retirement, malformed checkpoint, and allocation
laws live in its [frozen-history partial](../../tests/Puck.World.Tests/WorldSocialImportReservationLawTests.Frozen.cs).
World-rule, grant, checkpoint, and full-step allocation laws live in
[`WorldSocialRuleLawTests`](../../tests/Puck.World.Tests/WorldSocialRuleLawTests.cs).
Its [frozen-history partial](../../tests/Puck.World.Tests/WorldSocialRuleLawTests.Frozen.cs)
checks wire continuation, rule-write refusals, and policy-replacement gates.
Those probes do not measure physical population, rendering, or whole-game FPS.

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

## Network transport (`WorldPeerHost.cs`, `WorldPeerWireFormat.cs`)

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
(`WorldPeerWireFormat`) carrying exactly the Completion lane
(`WorldSubmissionResult`, i.e. Ack/Session/Query) — never a streamed
snapshot/definition/composition/lever (`WorldOutputHub`'s encoded lane stays
a scaffold beyond this one lane). `--connect` does not speak this door as a
client at all: `Puck.World.Program` enqueues a federation transfer
(`WorldInstanceHost.EnqueueTransfer` with `TransferDestination.Remote`),
which authenticates the resulting `WorldRemoteAuthority` purely over
`Puck.Networking.IAuthenticator` (`WorldAttestedAuthenticator`, a signed claim
over the challenge — never a shared secret) — the interactive attestation
identity door above is server-side only today; no production client crosses it.
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
the handshake. That namespace is `WorldAttestedAuthenticator`'s own verified
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

## Screen machines (`WorldMachineHost.cs`)

Owner ruling, 2026-08-03: a booted `IScreenMachine` (a diegetic screen's
cartridge/cabinet — `Puck.Abstractions.Machines`) is CORE state, not
presentation-fed. `WorldMachineHost` — a peer DI singleton `WorldServer`
takes as a constructor parameter, never a private field it builds, so the
container disposes the machines it holds — owns boot, per-tick stepping,
cable-linking, live reconfiguration, and memory-peek for every declared
screen's machine, in EVERY boot shape including headless. Stepping runs
inside `WorldServer.Step`, immediately after `WorldEngagement.FoldTick`, fed
that tick's per-screen pads directly (`WorldEngagement.BuildPadSnapshot()`,
in-process — no client/wire round-trip). `screen.insert`/`.eject`/`.select`/
`.options`/`.link`/`.unlink` (`Puck.World.ScreenCommandModule`) submit a
`WorldScreenOp` (`Puck.World.Protocol`) through the ordered submission domain
(`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY like `Command`/`Grant`/
`Revoke` and checked against the ordinary grant table (`Control` over
`screen:<n>`) before `WorldMachineHost` is touched; `Insert` and a
Machine-magazine `Select` share one boot path (`TryBootMachine`) and are BOTH
CAS-pinned (`sha256-64` of the exact bytes read, or the `"absent"` sentinel
when the file could not be read at all) — a failed boot is reported as a
failure, never a disguised success, and the pinned signature rides the tape
REGARDLESS of whether the op succeeded (INCLUDING an unresolved engine —
content is read/signed before engine resolution is even attempted, never
left unpinned on that path), so a replay re-drive refuses by name if the
file's on-disk state no longer matches what was recorded. Declared cable
links (`WorldMachineHost.ReconcileLinks`) are established/torn down at
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
miss) — offline replay reconstructs a FRESH `WorldMachineHost` from the
tape's embedded definition, so a machine's accumulated core state (or a
screen op's effect) from before recording began can never be re-established,
and the pose hash covers no machine state to catch the divergence.
`Puck.World.WorldScreenBinder` is a
pure reader of this type's outputs for presentation (framebuffer
handle/light, `PublishFrame`) and still owns the genuinely presentation
screen sources (test pattern, authored QR, webcam, compositor capture,
jumbotron view) that are not this type's concern. The list above is the
current set.

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
escapes nothing: it takes a `WorldSafeName`, whose fixed reserved-character set
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

## Deterministic replay (`WorldReplayTape.cs`, `WorldReplayTape.Drive.cs`, `WorldReplaySnapshot.cs`)

`replay.drive <name> [to <tick>]` re-drives a saved tape into the running
session: a forced `world.load` of the embedded definition plus the complete
boot authority checkpoint from a shadow server the recorded seats joined reset
the live world. This resets clocks, social memory, decisions, latches, fields,
grants, held input, and population together. `WorldServer.Advance` continues
from the restored clock; console waits retain a separate monotonic host-work
count, and local route epochs refresh so input can resume immediately.
Live replay refuses unresolved social ownership, transfer reservations or
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
while sampling both the LIVE population's pose hash and authoritative state-system
hash; `replay.stop`
persists `<name>.puckreplay` and re-drives it once; `replay.verify <name>`
rehydrates a fresh boot-image world, re-drives the stream offline, and
reports MATCH or MISMATCH naming the first divergent tick (tick 0 indicts the
starting state; any later tick is a real trajectory divergence). A receipt
disagreement — the live tree moved past the recording — refuses loudly with
no verdict; a recorded mutation's accept/refuse outcome disagreeing with what
the replay's own apply pipeline produces refuses loudly by name too
(`MutationOutcomeMismatch` — see [addons.md](../../.claude/skills/puck-world/references/addons.md)'s prepare/commit
transaction); a codec defect (`WorldReplayCodecException.cs`) reports as a
host bug, never folded into either refusal. `replay.inspect <name>
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
