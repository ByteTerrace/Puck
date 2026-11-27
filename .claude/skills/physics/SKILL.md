---
name: physics
description: "Maps Puck physics and contact: Puck.Physics kernels, IContactField and its providers, the body step, sweeps, step-up and snap, rigid solver, tether, gravity, navigation, field lattices, motion."
---

# Physics

A map of the deterministic fixed-point kernels in `Puck.Physics` and the World
code that applies them to bodies. Two layers, and the split is the first thing
to get right:

- **The kernels** (`src/Puck.Physics`) return a correction, an acceleration or
  a route and decide nothing. They parse no document and know no body
  identity: contact geometry, the contact seam's analytic and field solvers,
  step-up and snap, the rigid solver, the tether, gravity solvers,
  navigation, field lattices, bounded perception and the motion-program core.
- **The host** (`src/Puck.World.Server`) owns body state and decides what a
  result means: `WorldBody` (one body's step, contacts, rigid, carry, tether,
  hold, sleep, refusal capture), `WorldPopulation` (the roster, body pairs,
  the rigid solve, wakes), `WorldColliderSet` and `WorldSolidField` (the two
  contact providers), `WorldEventFeed`. Authored rows become kernel input in
  `Puck.World.Schema` (`BodyMotionProgramFactory`, `BodyActionSpecFactory`,
  `WorldMotionTuningFactory`, the collider compiler).

[Physics kernels](../../../docs/reference/physics.md) is the kernel reference;
[the server README](../../../src/Puck.World.Server/README.md#the-entity-table-worldpopulationcs-worldbodycs)
documents the host side; the document fields (kits, `motion`, `collision`,
`navigation`, `fields`) are in the `puck-world` references
([motion](../puck-world/references/documents-motion.md),
[render facets](../puck-world/references/documents-render.md),
[state and navigation](../puck-world/references/documents-state.md)). When this
file and the code disagree, the code wins and this file is corrected in the
same change.

## Find the owner by what you need

| You need | Owner | Read |
|---|---|---|
| A body resolves its swept position against the world | the seam `IContactField` (`Resolve`, `ResolveSweep`, `TrySupportDistance`, `TryUp`), providers `WorldColliderSet` (analytic) and `WorldSolidField` (signed distance) | [contact seam](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| Every sweep that moves a body, entity-aware | `WorldBody.SweepContact`, the one dispatch; an `IEntityContactField` learns which body moves | [below](#the-body-step-and-the-seam-door) |
| Analytic colliders: spheres, boxes (rounded, chamfered, oriented), superellipsoids, cylinders, half-spaces | `FixedStaticCollider` (`TryGetPush`, `RoundedBox`, `ChamferedBox`, `Cylinder`), `FixedOrientedBox`, `FixedConvexContact` (GJK and EPA), `FixedStaticContactSolver` | [physics reference](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| A solid creation grows or shrinks with contact | `WorldColliderSet.RefreshAttached` reads compiled `sizeBy` bindings by arena ordinal; `WorldCreationSize.Of` clamps the sample; `TryRideDisplacement` carries the support's material point through `WorldBody.SweepContact`; `WorldSnapshot.SolidScales` publishes the sample | [rig size](../sdf-authoring/references/rig.md#drivers) |
| A solid some bodies pass and others meet (a barrier a carried key opens) | a placement's `solid { passes [...] }` (`WorldSolid.Passes`, body counter registers): the row lives by row in the live set (`WorldPlacementStamp.CollidesLive`), and `WorldPopulation.HoldPasses` opens the moving body's held mask on it (`WorldColliderSet.HoldPasses`, read by `Excludes` in `QueryAttachedSet` and the field provider's `PosedField`) around its ride, advance, growth clearance and kit escape | [below](#contracts-that-bite) |
| A carved, relieved or smoothly blended creation collides as drawn | field colliders (`WorldColliderSet.CollidesAsField`) with a `FixedFieldPartition` narrowing hard unions to their exact pieces | [sdf-authoring contact admission](../sdf-authoring/references/shapes.md#contact-admission) |
| Never tunnel, however fast or thin | `FixedFieldContactSolver.ResolveSweep` (certified sweep through `ICertifiedSweepQuery`); `FixedStaticContactSolver.ResolveSweep` (sliced analytic sweep); both refuse with `ContactRefusal` rather than wrap | [physics reference](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| Walk up a kerb; stick to a downhill slope | `FixedStepUp.TryStep` and `FixedStepUp.TrySnap` over one `IContactField.TrySupportDistance` query, driven by kit `motion.stepHeight` and `motion.groundSnap`; `FixedStepUp.Attempt` says which check refused a step (`FixedStepUpOutcome`), and its lowering reaches down to the start's height even when the step rose along its ground, so a step over a crease at a crest lands on the gentler slope beyond; the solver asks for a step only at an edge that rises more than the contact skin above the plane the body is walking (`FixedStaticContactSolver`'s `GroundRise`), so a crease where a slope eases off is walked, not stepped | [motion](../puck-world/references/documents-motion.md) |
| Count what the body step decided (step-ups taken and refused, snaps, leaving and meeting the ground, airborne and stuck runs, slope refusals, how deep a solve still pushed) and gate a route on it | `BodyKinematicsWitness` (`WorldBody.Kinematics.cs`) per body, `physics.bodies` for the process, `kinematics(body, n, <witness>)` for rules | [below](#kinematic-witnesses) |
| Body-against-body depenetration and collision events | `FixedDynamicBodyContacts.TryCorrection`; events ask `FixedDynamicBodyContacts.Overlaps` (a positive push at zero margin, so a touching pair is neither), so an event and a contact never disagree; host pairs in `WorldPopulation.ResolveDynamicContacts` | [physics reference](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| Which bodies might touch: pairs in sweep order under a candidate budget, or the bodies near a box | `FixedSweepGrid` (X sweep crossed with a Y/Z grid, culling before it charges the budget), the one broadphase behind body contacts, `WorldEventFeed` discovery and the sleeping index (`WorldPopulation.NearbySleepingBodies`, `WakeSleepersNear`) | [physics reference](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| Remove a velocity's approach into a contact | `FixedVector3.LimitAlong` with the inward axis and a zero bound, shared by ordinary, field, tether and seam responses | `maths-usage` |
| Rigid bodies: impulses, friction, restitution, warm-started manifolds | `FixedRigidWorld` (the one solver), `FixedRigidContacts`, `FixedRigidManifold`, `FixedPairManifoldSlotTable`, `FixedTwoBodyKernel`; host side `WorldBody.Rigid.cs`, `WorldPopulation.Rigid.cs` | [the rigid solver](../../../docs/reference/physics.md#the-rigid-solver) |
| A rope that caps distance and keeps swing momentum | `FixedTetherConstraint` (`ResolveAnchor`, `Reel`); host `WorldBody.Tether.cs` | [tether](../../../docs/reference/physics.md#the-tether-constraint) |
| A net of rope strands that gives, sleeps and is climbed (a climbing net, rope bridge, hammock, cargo net, banner, sail) | a placement's `net` facet (`WorldPlacementNet`); the kernel `FixedNet` (strands through `FixedTetherConstraint.SolvePair`, compliant caps; reach through `Solve`; `CellColliders`); host `WorldNets` and `WorldPopulation.StepNets` (after the bodies, from `WorldTick.Step`); colliders `WorldColliderSet.RecordNetColliders`; rides `TryRideDisplacement` (standing) and `TryHeldDisplacement` (a `Pull` hold's anchor) | [net facet](../puck-world/references/documents-render.md#a-placements-net-facet), [decision](../../../docs/decisions/engine-design.md#a-net-is-a-node-grid-the-authority-steps-drawn-by-displacing-one-rest-mesh) |
| Grapple, pick an anchor, probe for a holdable face | `FixedSurfaceQuery.TryNearest`, `TryNearestDirected`; `IContactField.TryHoldableSurfaceAlongDirection`, which the analytic provider answers with `FixedStaticContactField.TryCast` over the holdable subset's own grid, so a probe traces only the colliders near its ray | [physics reference](../../../docs/reference/physics.md#contact-and-rigid-body-kernels) |
| Climb a wall: hold a face, spend and restore a register, hang at its edges, pull up over its top, swing round an outside corner, keep a leap on its face | a kit's `Pull` hold row and its `when`, `spend`, `restore`, `driveSeconds`, `mantle` and `corner` facets, resolved in `WorldBody.Hold.cs` (`ResolveHold`, `GuardHeldFace` over `HeldFaceEdges`, `TryBeginMantle` over `FixedStepUp.TryStep` and `FixedStepUp.Stands`, `TryBeginCornerWrap` over the body's own sweeps, `ImpulseStopsAtFaceEdge`, `CommandOnFace`, `HoldOwnsHeading`); the standard's `climbAbility` (`worlds/standard/abilities.puck`) | [holds](../puck-world/references/documents-motion.md), [the climbing plan](../../../docs/plans/climbing.md) |
| Gravity between many bodies | `GravitySolvers.Create` over `PairwiseGravitySolver` (the oracle), `FastMonopoleGravitySolver`, `AdaptiveFmmGravitySolver` | [choose a solver](../../../docs/reference/physics.md#choose-a-gravity-solver) |
| Gravity from a shape: a ball, a line, a plane, the nearest solid surface; areas that blend, flip or fade by a state row | `FixedWorldGravityArea` (`AccelerationAt`, `WeightAt`, `SurfaceAcceleration`), folded per body by `WorldGravityField`; a surface area asks `IContactField.TryUp` and keeps the answer while the body and the field stand | [the gravity section](../../../src/Puck.World.Schema/README.md#the-gravity-sectionacceleration-not-geometry) |
| A far body's pull on a whole world | `GravityTide` (the tidal tensor of a point mass, the multipole solver's own gradient), summed once a solve by `WorldGravityTides` and applied to each body's offset from the tide origin | [influence between worlds](../../../docs/decisions/worlds-and-federation.md#influence-between-worlds-is-a-delayed-value-and-a-tide) |
| A body on rails: a closed-form orbit, exact at any tick | `FixedConicOrbit` (`Phase`, `EccentricAnomaly`, `PositionAt`, `TryFit`) in `Puck.Maths`; `WorldOrbitField` places each orbit and glides its rider | [the gravity section](../../../src/Puck.World.Schema/README.md#the-gravity-sectionacceleration-not-geometry) |
| Carry a body to a point of the world along an arc (a launch) | the `timedMove` effect's `toward`, `arc` and `exclusive`, over `FixedTimedMove` and `WorldBody.ProposeTimedMove` | [timed moves](../puck-world/references/documents-motion.md#timed-moves) |
| Routes, off-grid checks | `NavigationRuntime` over `NavigationDomainInput`, proved with the body's own resolve and sweep against the static contact geometry (`WorldColliderSet.StaticField`, a `FixedStaticContactField`, or `WorldSolidField.StaticField`), each domain baked once on the thread pool (`NavigationDomainBake`, kept across rebuilds while its `IRegionalContactField.Within` part is equal); medium fields through `INavigationMediumField`; host `WorldPopulation.Navigation.cs` | [navigation](../../../docs/reference/physics.md#navigation-kernel-navigation) |
| One route across several domains (a building's levels and stairwells, the valley), to a cell or a place | `NavigationRuntime.FindRoute` with an `INavigationRouteGoal` (`NavigationCellGoal`, the host's `WorldNavigationPlaceGoal`), joined at aligned grids' shared cells within one authored `network`; `world.navigation route` checks it against the contact field, `world.navigation map` draws a domain | [navigation](../../../docs/reference/physics.md#navigation-kernel-navigation) |
| Many bodies heading to a few places (a crowd, a town's citizens), with no route per body | goal fields: `NavigationRuntime.GoalField` derives one per goal cell over the goal domain's network (a reverse search over the domain's cached route graph, `Domain.GoalGraph`, kept as one step per cell, `NavigationGoalField`) and `NavigationRuntime.GoalStep` reads a body's next step; `RequestGoalField` starts a derivation off the step that the read (`GoalField`, the only read, which always yields the field) joins, so residency decides work and never a tick's outcome; a cache within `NavigationCapacity.GoalFieldBytes`, never checkpointed or hashed; authored `shared: true`; host `WorldPopulation.ReadGoalFieldTarget`; work in `NavigationSearchWork` (`physics.navigation`) | [navigation](../../../docs/reference/physics.md#navigation-kernel-navigation) |
| A domain whose extent is a place's box (one region, written once) | authored `navigation.domains[].region` (a static placement's unturned box region) and `margin`, resolved into origin, width, depth and probe band by `WorldNavigationDomains.Of`, the one reader of a domain's extent; the standard's `placeDomain` | [schema README](../../../src/Puck.World.Schema/README.md#the-navigation-sectionbounded-routes-over-world-truth) |
| Pay more to walk some ground than other ground, or never enter it (roads, crossings, lawns, shallow water), per kind of agent | area types and cost profiles: `NavigationAreaSet` (marked volumes, last wins, over `NavigationDomainInput.Area`), `NavigationCostProfile` (fixed-point multiplier per type, or exclusion) taken by `FindRoute`, `Domain.FindPath` and `GoalField`, a goal field keyed by goal and profile; authored `navigation.areaTypes`, `areas`, `profiles` and `navigated(profile:)`, compiled by `WorldNavigationAreas` | [navigation](../../../docs/reference/physics.md#navigation-kernel-navigation) |
| A path a walker steers along: off walls, through a doorway's middle, cut to the corners it turns at | the route search's clearance cost (`RouteClearanceCells`, in `Domain.RouteSteps`), `NavigationRuntime.Straighten` over `Domain.IsStraightClear`, `NavigationRuntime.TryLocateNear` for a body against a wall; host `WorldDocumentNavigation.Plan`, the navigation one world document describes, compiled from that document alone, which the seat driver (`WorldSeatDriver`) plans over for the document its seat's client was delivered | [the benchmark plan](../../../docs/plans/benchmark.md#the-route-is-places-played-by-an-authored-seat-program-stage-4) |
| A door or swap that changes part of a domain's geometry without re-baking the domain | `Domain.TryBeginRebakeFrom` over `IRegionalContactField.TryDifference`: a worker copies the predecessor's bake and proves again only the cells and edges the changed colliders reach; `world.navigation` counts `rebaked=` | [physics reference](../../../docs/reference/physics.md#navigation-kernel-navigation) |
| Evolving scalar fields (water, heat, snow) and their solids | `FieldLattice` (reactions, `IFieldLatticeHost`), `FieldLatticeSolid`, `UnionField`, ordering by `ReadWriteHazard.Conflicts` | [field lattice](../../../docs/reference/physics.md#field-lattice-fields) |
| Steering samples and flocks | `FixedSpatialNeighborhood` (bounded, never a broadphase), `FixedFlockSteering` | [perception](../../../docs/reference/physics.md#bounded-local-perception) |
| How a body moves under its kit: drive, shaping, holds, actions | `CompiledBodyMotionProgram`, `BodyMotionOp`, `FixedMotionTuning` (Schema; gate-bearing host records), executed by `WorldBody` phases | [motion core](../../../docs/reference/physics.md#the-motion-program-core-motion) |
| Sleep, wake on a moved carrier or lattice, rest | `WorldBody.Sleep.cs` (the authored idle floor; the standard's is `idleSleepAfter` in `worlds/standard/kits.puck`), `WorldPopulation.ObserveContactFieldChange`, which settles parked rigid islands and idle kit sleepers (`SettleIdleSleepers`) by the change's box, `WorldPopulation.ContactFieldVersion` | [server README](../../../src/Puck.World.Server/README.md#the-entity-table-worldpopulationcs-worldbodycs) |

## The body step and the seam door

- `WorldBody.Advance` runs the motion program's phases, then
  `ResolveProgramContacts` sweeps the step through the field. A rigid kit only
  prepares its step and joins the population's one `FixedRigidWorld` solve;
  carried and tethered bodies have their own files beside it.
- **The kit advance split.** `WorldPopulation.AdvanceSimulated` takes every
  body through one set of phases (plan, steer, admit, move), so every producer
  runs before any body moves and a body a step wakes steps on the next tick. A
  body whose step is local (`WorldBody.StepIsLocal`,
  `WorldPopulation.ProducerIsLocal`) runs them in a fixed slot partition on the
  job pool; every other body runs them in the serial partition, on the forking
  thread in index order, inside the kit transaction
  (`WorldPopulation.Partitions.cs` holds the phases and the invariant). A world
  with a seam partitions too: a sweep through `WorldAdjacencyContactField` reads
  the tick's pinned projections and writes only the correction budget of the
  body it moves. Each body's input pass (`WorldBody.AdmitProducerObservation`)
  runs before any body moves, and a body takes an ordinary step only on a tick
  whose pass found it could; `physics.bodies.kit-forks` counts the phases that
  left the stepping thread. A step that reaches shared population state,
  the input runtime or a register from a partition throws
  (`ThrowIfPartitioned`, `WriteStateRaw`); a new shared write a body step can
  reach goes through the body's `WorldBodyStepLog` or makes the body serial.
  Producers read other bodies only through the frozen perception image. The
  navigation runtime is read-only during the forks
  (`NavigationRuntime.EnterReadOnly`), the plan's own fork included: a
  partition's plan lists the bodies whose goal field is owed, and the serial
  settlement that follows it (`PlanKitStep`) bakes every domain a partition
  reads and resolves the fields. A partition's plan writes its own bodies,
  their entries and its own lists; a wake goes through the bound step log.
- **A producer at rest lets its body sleep.** A producer-driven body counts
  toward `bodies.sleepAfterSeconds` only while its producer's last run found it
  at rest (`WorldPopulation`'s `ProducerRests`: the neutral intent, no state
  moved, and sensors that read only what wakes the body when it changes). A new
  sensor source rests only once it is named there with its waking door, and
  every wake withdraws the verdict (`WorldBody.WakeUp`).
- **One door for every moving sweep.** `WorldBody.SweepContact` is the
  dispatch every sweep that moves a body goes through; the body hands itself
  to an `IEntityContactField` (today `WorldAdjacencyContactField`), so a
  neighbour's answer is admitted against that body's own budget and no sweep
  names a body by index. While a kit fork runs, the door
  (`WorldPopulation.AdmitAdjacencyResponse`) throws for any body but the one
  its thread is stepping, and the doors that move a carrier
  (`WorldBody.ApplyCarryBlock`, `ApplyAdmittedContactResponse`) throw. A
  probe that moves no body calls the field directly and names none. A new
  path that moves a body calls `SweepContact` or the `BodyContactStep` bound to
  it, never `IContactField.ResolveSweep` directly.
- **An image addressed by channel ordinal crosses a mapping change by logical
  identity.** `WorldBody.InstallInputMapping` is the one door that changes a
  body's input mapping under live images: it rebuilds the submitted and held
  images and carries the tape, the producer's staged image and the steering
  image the population keeps between producer runs across, so a step that
  resolves its intent after another body retuned it reads no stale ordinal. A
  new image kept by ordinal crosses there too, and a kit step that can install
  a mapping saves what a refusal must put back
  (`WorldInputRuntime.CompleteKitStep`).
- **A refused sweep is a full block.** `ContactRefusal` means the body keeps
  the pose it started the tick with. `WorldBody.SweepRefusal.cs` captures
  everything the step writes (`WorldBodyTransferState`,
  `WorldBodyIntegrationResidue`, pose and contact facts) before the step and
  restores it, and a walking body's emitted effects are withdrawn. A body
  refused this tick (`WorldBody.SweepRefusedThisTick`) is static to its pair
  partners, unpulled by a tether and never corrects a carrier until
  `WorldPopulation.CompleteStep`. A field added to either captured record is
  covered with no change.
- **Only a proven contact corrects a carrier.** A sweep that held its body for
  want of proof reports `ContactResolution.Unproved` and hands the carrier
  nothing.
- A `timedMove` action uses `FixedTimedMove.Sample` for both its yaw and its
  travel. `WorldBody.ProposeTimedMove` updates the proposal before the contact
  phase, and the refused-step snapshot includes the move's progress.
  [Authoring](../puck-world/references/documents-motion.md#timed-moves)
- **A body at its skin is in contact.** Every solve asks at the skin plus
  `FixedContactPushMath.RestingBand`, and `Classify` reads walkable ground in
  the band as a resting contact that grounds without moving, so a body walking
  level ground grounds in its own sweep and pays no snap, and its support
  search (`FixedStepUp.TryFollowSupport`) is one sweep that does not move it.
  A cull ahead of a solve keeps every collider within
  `FixedStaticContactSolver.ContactReach`, never the bare skin.
- **Two snaps share one answer** where the body has not moved between them
  (`BodySupportMemo`, keyed by field, volume derivation, start, orientation, up
  and reach).
- A kit change whose collider would embed the body steps it out along up or
  world X/Z, never down (`WorldBody.EscapeEmbedding`).

## Kinematic witnesses

`WorldBody.ResolveProgramContacts` folds every contact phase that resolved into
the body's `BodyKinematicsWitness` (`WitnessKinematics`), where the step
already decides each fact:

| Witness (rule name) | Decided by |
|---|---|
| `stepUps`, `stepRefusals`, `stepRise` | `FixedStepUp.Attempt`'s outcome. A refusal (`FixedStepUpOutcomes.IsRefusal`) is a riser the body cleared at its step height with nothing walkable to stand on beyond (`NoLanding`, `Unstandable`); a wall, a riser taller than the step height or a ceiling is not one |
| `snaps` | `FixedStepUp.TrySnap` in the ground-follow branch |
| `leaves`, `landings`, `airborne`, `airborneLongest` | the grounded verdict at the end of the contact phase against the witness's current airborne run |
| `stuck`, `stuckLongest` | a step whose intent drives the body (`DrivenBy`, the obstruction latch's idle test) and moves it less than `WorldBody.StuckDisplacement` |
| `slopeRefusals` | a driven step that ends with an obstruction normal facing up more than a sixteenth: a face too steep to stand on |
| `refusalOutcome`, `refusalHeight`, `refusalNormalX`/`Y`/`Z`, `refusalX`/`Y`/`Z` | the latest refused step-up: its `FixedStepUpOutcome`, the blocked sweep's `RequiredStepHeight` and `ObstructionNormal`, and where the body stood; a test that gates refusals reports them, so a failure names its edge (`tree-walk-tests.puck`) |
| `depth`, `penetration` | `ContactResolution.Residual`: the deepest displacing push the solve's last relaxation pass still applied, zero when that pass found the pose clear (both `FixedStaticContactSolver` and `FixedFieldContactSolver` report it) |

The witness is a field of `BodyMotionSection`, so a checkpoint carries it and a
refused sweep restores it with the step. `physics.bodies` counts the same
events for the process (`step-ups`, `step-refusals`, `snaps`, `leaves`,
`landings`, `stuck-steps`, `slope-refusals`, `residual-steps`) and keeps
peaks a reader clears (`step-rise-peak` and `penetration-peak` in micrometres,
`airborne-peak` and `stuck-peak` in milliseconds); `WorldBodyWork` is sharded,
so a kit partition counts without an interlock. `body.motion` echoes a body's
witness after `kinematics:`. `worlds/debug/tree-walk-tests.puck` plays the
`tree-walk` route and latches the witnesses per waypoint.

## Contracts that bite

- **Determinism.** Every kernel is fixed point over `Puck.Maths`; mixed-scale
  products and wide roundings go through `FusedArithmetic` and its siblings,
  so a kernel rounds the way every other consumer does (`maths-usage`). A
  deliberate correction moves body poses and state hashes; judge it by the
  new answer's exactness, not by matching old bytes (AGENTS.md rule 4).
- **Kernels decide nothing.** Walkability, authority, waking and admission
  are the caller's. A kernel returning a correction must not mutate a body.
- **One mechanism per question.** One rigid solver, one body broadphase
  (`FixedSweepGrid`), one static-collider grid (`FixedColliderGrid`, behind
  `FixedStaticContactField` and so `WorldColliderSet`), one round-core rule
  (`TryRoundPush`, behind both static spheres and dynamic round pairs), one
  box geometry owner (`FixedOrientedBox`), one approach-removal
  (`FixedVector3.LimitAlong`), one overlap sense for events and contacts.
  Writing a second is a defect; extend the one that exists.
- **Perception is not a broadphase.** `FixedSpatialNeighborhood` drops
  candidates when its attention budget binds; a contact solver may not.
- **Work is counted, not timed.** `physics.contact`
  (`FixedStaticContactWork`), `physics.sweep` (`FixedContactSweepWork`) and
  `physics.bodies` are deterministic work sources; read them with
  `world.counters` before and after a change. `physics.contact.field-unproved`
  counts every field solve that proved nothing, the ground snap's bracket
  probes included, so a body standing at an unprovable feature adds several a
  tick and the total follows that body's path: a change that moves one pose by
  a raw can move a sweep's total by thousands. Before calling such a delta a
  bounds regression, run the same poses through both builds and compare each
  outcome and box interval ([the crease item](../../../docs/plans/open-items.md#certified-field-contact)).
- **Scale reaches every contact query.** A body's `Scale` multiplies its
  collider volumes for world contact, body pairs, events, the adjacency
  sweep's local side and rigid probing alike.
- **Growth is held to the room about the body.** A tick's scale resync
  (`WorldPopulation.SyncBodyScale` through `WorldBody.SetScaleWithinClearance`)
  admits a larger size only where the grown body, lifted clear of its floor,
  takes no push down its own up and only a skin's worth in all; otherwise it
  takes the largest size that fits and writes it back into the register, so a
  size field (`worlds/standard/size-field.puck`) that asks again each tick
  grows the body once it walks clear. Shrinking is always admitted; a
  crossing's resync takes the pair's size whole.
- **Carried rigid contacts use the solve.** `TwoBodyHandle` makes a carried body
  static with its carrier's velocity. Its owned pair manifolds still join the
  solve; it never joins the integration roster. Carry's positional pair split
  handles only locomotion neighbours.
- **Contact softness is authored.** `collision.bodyContacts` supplies
  `contactHertz`, `contactDampingRatio` and `contactSpeed`; the standard package
  chooses their values. `RigidContactPolicy` compiles them once, and a retune
  rebuilds the solver. The authored rolling lever also bounds torsion as the
  contact patch radius; neither row resists without normal load.
- **Solid size reads the current authority.** `WorldTick` passes its live definition to
  `WorldPopulation.AdvanceSimulated` for `WorldColliderSet.RefreshAttached` to sample.
  The geometry compiler's retained definition can still name a retired arena after a
  checkpoint restore. Restore keeps the captured sample; the next tick reads the restored arena.
- **Workspaces are reused and not thread-safe.** The hierarchical gravity
  solvers and navigation domains allocate once and keep their arrays.
- **Every body feels the field as a vector.** A rigid body integrates it. A kit
  body splits it in `ApplyHoldGravity`: the signed component along its up
  drives the vertical channel (a field pulling toward the head lifts the body
  and clears `m_grounded`, as a jump does), and the rest fills
  `m_ballisticTangent`, the airborne velocity across the up that the drive
  never shapes. The up turns toward the field only at a metre a second squared
  and above (`MinFieldUpMagnitude`), so a faint field (a tide) moves a kit body
  through the tangent without turning it.
- **An airborne body's ballistic velocity is world truth.** `SetUp` re-expresses
  the vertical channel and the tangent against a new up with their sum
  unchanged, and carries only the drive's planar velocity with the frame; on
  the ground the vertical channel turns with the surface. The tangent ends on
  the ground, in a hold that owns the vertical channel, at a teleport or
  exclusive move, and at every world-space write (`SplitVelocity` hands it to
  the planar channel); a transfer carries it as planar velocity. It is a
  `BodyMotionSection` field, so checkpoints and a refused sweep keep it.
- **Ground is what the field presses the body onto.** `FieldHoldsOn` accepts a
  contact normal within the world's walkable slope of the field's up (any
  normal under no field or one too faint to orient); the contact phase grounds
  a body only on such a surface, so a floor a field turned over or across is
  slid off, not stood on. Under `GradientDerivedUp` a grounded body keeps its
  surface's up while that holds (`StandsBySurface`, asked by `ResolveUp` and by
  the contact phase's adoption of the measured normal); otherwise the field
  steers the up while the body travels to the surface the field now holds it on.
  The hold system's walkable reads (`WalkableUnderHoldUp`) measure from
  `HoldGravityUp`, the same axis its cones use.
- **A body's passes are opened only on the serial path.** A gated row's
  colliders are left out of a query exactly while the moving body's held mask
  is set on the live set, and `WorldPopulation.HoldPasses` is the one door that
  sets it: it refuses inside a kit partition, and a body holding a pass is
  planned serial (`HoldsPasses` beside `StepIsLocal`). A new body query outside
  the step (a growth or escape like `SetScaleWithinClearance` and
  `EscapeEmbedding`) opens its body's passes the same way. The rigid solve,
  body pairs, navigation and the presentation query field read no passes: a
  rigid body meets every gated row, a route plans as if it stands, and the
  presentation field leaves it out so a camera never stops in front of a body
  that crossed.
- **A moving solid is one root.** A solid placement attached to a body, or
  sized by state, is a root of the moving set (`WorldColliderSet.Build`'s
  `movingOnly` rows) under either provider; the field provider's static
  program leaves it out.
- **Moving solids reach gameplay queries too.** `WorldSolidField` queries the
  static program and the contact owner's live roots through `FieldWorldQuery`,
  which uses the existing SDF march. Grip reads the sampled row's own policy.
  Moving fields compile without world-space partitions at every scale, including
  one. A solid-consumer rebuild retains unchanged roots' last samples.
- **A sampled size is a state disclosure.** Session and federation deliveries
  filter it with `WorldSolidScaleDisclosure` through the source cell's live
  `WorldRecipientView`; a cached binding never caches read permission.

- **A net's nodes stay within their reach of rest.** `FixedNet.Step`'s last pass
  (`FixedTetherConstraint.Solve` against each rest position) and `FixedNet.Restore`'s
  range check are the only writers of its offsets, so the drawn box
  (`SdfSheetDisplacement.Bound`), the collider sheet and the snapshot never measure
  the nodes. A net steps on the authority alone (`WorldTick.Step` calls
  `WorldPopulation.StepNets` after `StepBodies`), never inside `StepBodies`, which a
  client's prediction runs too: a predicting population's nets stay at rest.
  A body's weight on a net is a `FixedNetLoad` at its hold's anchor; its volumes push
  nodes to their radius plus the net's strand and the contact reach, the band a body
  resting on the net's cell colliders stands in, so leaning moves nodes and standing
  near does not.

## Where each contract is enforced

| Contract | Enforced by |
|---|---|
| A body never moves through space no sweep proved clear | `FixedFieldContactSolver.ResolveSweep` and `FixedStaticContactSolver.ResolveSweep`: budget exhaustion keeps the proved prefix, an unrepresentable step refuses whole |
| A kit capsule fits its sweep ceiling; no volume quantizes to a point | collider validation (`CapsuleCoreFitsSweep`, `VolumeCoreSweeps`) at load |
| A refused step changes nothing | `WorldBody.SweepRefusal.cs`'s capture of whole records, restored on refusal |
| A neighbour's contact answer is admitted against the moving body | `WorldBody.SweepContact` dispatching to `IEntityContactField` |
| A lattice's solid stays inside the carrier | `FieldLatticeSolid.CellFits` at world validation; the constructor throws otherwise |
| A net's every node lies within its reach of rest | `FixedNet.Step`'s final reach pass and `FixedNet.Restore`'s range check, its offsets' only writers |
| Field-program dependency edges mean the same on both sides | `ReadWriteHazard.Conflicts`, called by the lattice and the Schema compiler |
| Same input, same poses | the population's checkpoint codecs and `puck test --reproduce` |

A field row’s `simulationOnly` flag keeps the height scale in `FieldLattice.StepFlow` and excludes the row from body contact and medium queries. Presentation checks `WorldFieldRow.DrawsSurface` before it subscribes to the row or allocates a height field.

## Open gaps

- **The adjacency sweep's remote side reads a neighbour's unscaled collider**:
  the delivered snapshot carries no per-entity `Scale` yet.
- **No query crosses a portal door.** Line of sight (`WorldPopulation.HasLineOfSight`),
  a tether's aim, a hold's reach, body pairs and routes all stop at the glass,
  and a door that moves is not swept. `WorldPortalHoleContactField` only takes
  back the push of the wall behind an opening.
  [Portals in play](../../../docs/plans/portals-in-play.md#2-queries-through-doors)
  designs the one layer that carries every ray through, composed where the
  opening and seam layers are.
- **Approximations the reference names.** Rounded or chamfered circular rims
  use a contained cylinder core with a reported inward bound (the fix is an
  exact rim contact); a superellipsoid above exponent two and the carved
  primitives stay bounded approximations in the analytic provider (an
  exponent-2 collider measures its exact distance through
  `FixedEllipsoidProjection`, and a spheroid is the field's own surface); a
  triaxial ellipsoid's gauge field, and a capsule scaled apart in X and Z (that
  gauge elongated, `FixedStaticCollider.Elongation`), dilates past its
  collider's true margined surface along its long axes (the field side's fix is the true distance
  under a non-uniform scale); tilted box-in-cylinder edges are conservative.
- Packets: [certified field contact](../../../docs/plans/open-items.md#certified-field-contact),
  [collision versus drawn mesh](../../../docs/plans/open-items.md#collision-versus-drawn-mesh),
  [water](../../../docs/plans/open-items.md#water) and
  [the Moth's motion](../../../docs/plans/open-items.md#dual-stick-play-and-the-moths-motion).

## Verify

- Kernel laws: `tests/Puck.Physics.Tests` (gravity against the pairwise oracle,
  contact kernels, sweeps, the rigid solver's mechanism and sabotage cases,
  navigation, lattices, neighborhoods), filtered to the class you touched.
  Body integration laws are in `tests/Puck.World.Server.Tests`.
- Scenarios in the debug world: `puck test worlds/debug/gallery-tests.puck --jobs 1`
  (stairs, slabs, the lift), `noclip-tests`, `suit-tests`, the `water-*-tests`,
  `meadow-tests`, and `tree-walk-tests` (stepping, snaps, airborne and stuck
  runs and landings over the gallery tree's whole walkway, played by the
  `walker` route through seat 1's controls); add `--reproduce` for anything
  that moves a pose.
- Live read-backs over stdin: `body.where` (pose, support,
  `sweep=refused(...)`, `asleep=`), `body.motion` (effective `stepHeight=`
  and the body's kinematic witness),
  `world.kits`, `world.collision.status`, `world.fields`, `world.counters
  physics.contact`, `physics.sweep` and `physics.bodies`. `puck creation stats`
  builds a creation's contact field offline (`sdf-authoring`).
- The reference pages' canary names (`thin-wall-sweep` and others) are
  quarantined under `experimental/` with the other canaries.

## Look-alikes

- The chase camera's clearance sweeps and an effector's surface probe query
  the rendered field for presentation (`WorldFieldAnswer`, `puck-world`), not
  the contact seam.
- `FixedSpatialNeighborhood` is perception; the body-pair broadphase is
  `FixedSweepGrid`, budgeted by `collision.bodyContacts` for contacts and by
  `collision.events` for events.
- Binary heaps (navigation's `NodeHeap`, the board path cost, the
  neighbourhood's top-k) all run on `Puck.Abstractions.Collections.BinaryHeap`;
  a new priority queue uses it with a struct `IComparer<T>`.
- `collision.events` budgets proximity events; `collision.bodyContacts`
  budgets physical depenetration. They are separate limits.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-world` | Kit, `motion`, `collision`, `navigation` and `fields` document fields, the tick order, running a World. |
| `sdf-authoring` | Which shapes a creation's contact admits, contact tuning, `puck creation stats` and `creation.shape`. |
| `maths-usage` | Fixed-point vectors, rate accumulators, rounding and the certified sweep's field queries. |
| `maths-laws` | Law coverage for a changed `Puck.Maths` member a kernel uses. |
| `state` | Rules that read body facts or write body targets, and the rule-gate interpreter. |
| `rendering` | The SDF evaluator and mesh the drawn surface comes from. |
| `verification` | Leases, the private CLI copy, flaky versus failing scenarios. |
