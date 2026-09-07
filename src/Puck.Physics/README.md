# Puck.Physics

Puck.Physics owns the engine's deterministic fixed-point simulation kernels:
exact and scalable gravitational fields, compound dynamic-body overlap, analytic
static contact geometry, a substepping rigid-contact solver, a bounded A*/
shared-search navigation kernel under `Navigation/`, and — under
`Motion/` — the body motion-program core. Everything here is built on Puck.Maths,
so identical ordered inputs return identical results — bit for bit — on every
machine and backend, and a run can be recorded and replayed exactly.

The library computes; it does not govern. Callers keep world-document
compilation, authority, gameplay classifications such as walkability, and the
final application of contact results. A kernel returns a correction or an
acceleration and leaves the decision of what to do with it to its caller.

## 🧭 The motion-program core (`Motion/`)

`Motion/` holds the instruction vocabulary a body advances under and nothing that
reads a document: `BodyMotionOp` (the closed opcode set), `CompiledBodyMotionProgram`
(the compiler that validates a declared name/version/kind/opcode set and groups the
selection into its intrinsic host phases), the per-body trigger and action-state IR
(`CompiledActionSpec`, `CompiledTrigger`, `CompiledFactTrigger`, `CompiledPredicate`,
`CompiledBodyInstruction`, `CompiledActionStateSlot`, `CompiledActionStateEnvelope`),
and the compiled fixed-point tunings the stages read (`FixedMotionTuning`,
`FixedMotionDefaults`, `FixedMotionScalarEnvelope`, `FixedSpeed`, `FixedTurn`,
`FixedUpTurnRates`, `FixedObstructionLatch`).
`ShapeVelocity` reads the one unified velocity-shaping table,
`FixedMotionTuning.Shaping` (`FixedBodyShaping[]`): the first row whose gate
opens governs — a row with no `Across` facet shapes the whole vector through
the engage/release response law (`FixedShapingAlong`), a row carrying one runs
the anisotropic drive decomposition instead (body-frame longitudinal/lateral/
residual lanes, each converging at its own rate), and a row naming `Dynamics`
(a compiled `Puck.Maths.SecondOrderDynamics.SecondOrderStep`, a pole-matched
second-order follower the host steps once per tick) shapes it through that
follower instead of either — exactly one of `Along` or `Dynamics` per row. The
forward target and the steering rate every row's drive decomposition reads are
the tuning's own `FixedSpeed`/`FixedTurn`, not a second spelling; `FixedTurn`'s
own speed-scaled authority curve and the governing row's `TurnScale` apply
identically whichever yaw-writing frame operation (`ResolveDriveFrame`,
`ResolveYawAttitudeAndPlanarFrame`, `IntegrateLocalAttitude`) is selected. A
held low-traction row (a kart's drift) is an ordinary row gated on a `Held`
predicate (`CompiledPredicateKind.Held`, a live channel-threshold read),
authored ahead of the row it overrides. An absent engage/release/reversalRate/lateral
rate compiles to an explicit instant-convergence flag; zero is not overloaded
as either instant or disabled. The vertical channel is a hold row's
own concern (`FixedBodyHold.Gravity`/`.Thrust`, `ResolveHold`/`ApplyHold` in
`BodyHold.cs`) — every Motion-kind kit authors at least one, so a drive kit's
own gravity and MoveUp thrust ride its hold list exactly as any other kit's do.

The translation from an authored world row into these shapes lives with the
authoring vocabulary, in `Puck.World.Schema` (`BodyMotionProgramFactory`,
`BodyActionSpecFactory`, `WorldMotionTuningFactory`). Execution — the per-phase
stages that read and write a body's pose, velocity, and action state — belongs to
the host that owns that state (`Puck.World.Server.WorldBody`); this project supplies
the program it executes, not the body it executes on.

`Puck.Abstractions` (the strict by-name enum converter every authored enum in
this vocabulary declares), `Puck.Maths`, and `Puck.State` (the comparison and
trigger-mode vocabulary a compiled predicate and a state rule share:
`ActionStateComparison`, `ActionTriggerMode`) are referenced.

## ✨ Key features

- *Three gravity solvers, one seam:* an exact pairwise oracle, a Barnes–Hut
  monopole treecode, and a low-order adaptive FMM, all behind `IGravitySolver`
  and selectable by kind.
- *Measured, not inferred, cost:* `GravitySolveStatistics` reports the actual
  direct and approximated work so you size against your own population instead of
  trusting a name.
- *Deterministic to the bit:* every kernel rounds the way the rest of Puck.Maths
  rounds — one checked square norm, ties-to-even centers of mass, exact `Int128`
  mass moments — so results and work counts are reproducible for identical
  ordered inputs.
- *Caller-supplied geometry:* the rigid solver and contact kernels take contact
  candidates and collider volumes as data, acquiring no absolute position, world
  schema, or SDF dependency.
- *Allocation-free after warm-up:* the hierarchical solvers retain and reuse
  their index, partition, and node arrays, so a single instance stops allocating
  once it reaches a workload's high-water mark.

## 🌌 Choose a gravity solver

| Solver | Use it for | Cost | Accuracy |
|---|---|---|---|
| `PairwiseGravitySolver` | small populations, tests, and oracle comparisons | Θ(N²) | every source evaluated directly in stable input order |
| `FastMonopoleGravitySolver` | large, spatially distributed populations | expected O(N log N), O(N²) worst case | distant octree cells replaced by their total mass at their center of mass |
| `AdaptiveFmmGravitySolver` | the largest spatially distributed populations | O(N log N) tree construction plus expected O(N) interaction; O(N²) worst case | mutually distant cells exchange first-order local expansions; near leaves stay direct |

The fast solver is a Barnes–Hut-style hierarchical monopole treecode. It is
often grouped loosely with fast multipole methods, but it is not the
higher-order Greengard–Rokhlin FMM: it has no local expansion or multipole
translation pass. `GravitySolveStatistics` exposes the actual direct and
approximated work so callers can measure their own population rather than infer
performance from the name.

The adaptive FMM is a separate dual-tree algorithm. Its upward M2M pass forms
exact `Int128` mass moments. A mutual M2L pass translates each accepted source
cell into acceleration and a Q32.32 tidal Jacobian at the target cell. The
downward L2L pass shifts those local expansions to children before bodies are
evaluated, sharing one accepted cell interaction across every target below that
cell instead of repeating a tree walk per body. The source expansion is a
monopole at its center of mass and the target expansion is first-order
Cartesian — the deliberately low-order FMM tier, not an unbounded
spherical-harmonic expansion.

## 🪨 Contact and rigid-body kernels

`FixedBodyColliderVolume` is the shared sphere/capsule/oriented-box vocabulary.
`FixedDynamicBodyContacts` supplies a conservative compound broadphase radius and
the deepest pair correction without knowing body identity or mutating state. A
box-vs-box pair depenetrates by separating-axis test over world X/Y/Z plus
each box's own three face axes (`FixedColliderBounds.BoxAxes`) rather than
the world-bounds approximation every other volume pair uses — an oriented
box's own face axes are what a world-axis-only overlap never tests, and
skipping them overstates penetration for anything but an axis-aligned box.
Face axes alone (no edge-edge cross products) bound the remaining error
rather than eliminate it: a pure edge-edge contact can still read deeper than
its true minimum.
`FixedStaticCollider.TryGetPush` does the same for analytic spheres,
axis-aligned boxes, and half-spaces, returning only a `FixedContactPush`.

`IContactField` is the seam a grounded body resolves its swept position
against, and `FixedStaticContactSolver` is the analytic provider's half of it:
the relaxation over a static collider set, written once. It takes two collider
spans and walks both inside each iteration, so a caller may hold a set it
compiled once beside one it rebuilds per tick without changing how the two
interleave. `FixedFieldContactSolver` is the other provider behind that seam: it measures
contact from a scalar field instead of a collider list, taking its push
direction from the field's own gradient at a confirmed penetration, and reads
that field through `Puck.Maths`'s `IFieldEvaluator` and `IWorldQuery`. That is
why neither this library nor a distance-field library needs to reference the
other.

`FixedSurfaceQuery` is the nearest-surface-point primitive over the same
collider vocabulary — the analytic anchor query climbing (surface attach) and
grappling (tether anchor selection) both resolve against, distinct from
`TryGetPush`'s depenetration-only contract. `TryNearest` returns the closest
point, outward normal, owning collider identity, and distance within a
caller-supplied reach across two spans (`FixedSurfaceColliderSource.Static`/
`.Dynamic`, mirroring `FixedStaticContactSolver.Resolve`'s split), ranked by
distance then a `(Source, ColliderIndex)` tie-break. `TryNearestDirected` is
the aim-assist cone variant for tether targeting: candidates are filtered to
a caller-supplied max distance and half-angle around an aim direction, then
ranked by angular deviation before distance. Every reach, max distance, and
half-angle is caller-supplied; nothing here derives one from a document.

`FixedRigidSolver` owns canonical contact ordering, persistent manifold slots,
warm starting, speculative activation, bounded deep-overlap recovery, soft
constraints formed at the substep width, and a fixed iteration budget. Geometry
stays caller-supplied through `FixedContactCandidate`, so the solver acquires no
absolute position, world schema, or SDF dependency. It solves exactly ONE body
against a candidate set every call — it has no member through which a second
dynamic body could reach it. `FixedRigidSolver` is not wired into
`Puck.World.Server` anywhere.

### Multi-body coupling

`FixedTwoBodyKernel` generalizes `FixedRigidSolver`'s single-body effective-mass
and impulse-application kernels to two named bodies, either of which may be
static — a static side's zero inverse mass and inverse inertia contribute
exactly zero to every sum, so a dynamic-vs-static pair degenerates to the
single-body formula without a special case. `FixedTwoBodyContact` and
`FixedPairManifoldSlotTable` extend `FixedContactCandidate` and
`FixedManifoldSlotTable`'s pattern from one body to a body PAIR: one manifold
slot table per active pair — the existing 16-slot cap read as a per-pair budget,
never a global one — with the same ordered-array, no-hash-container, total-key
eviction discipline, and a canonical-order key covering every declared field,
including the second anchor.

`FixedRigidWorld` is a SHAPE-ONLY multi-body orchestrator over any number of
bodies and the pairs among them: a dense, id-indexed, never-swap-removed body
store (a body's id IS its storage index, tombstoned on removal, never reused),
a dense pair registry with a declared `MaxActivePairs` budget, and the same
integrate/warm-start/biased-solve/integrate-positions/relax substep sequence
`FixedRigidSolver` already runs for one body, generalized to every live body
and every active pair in ascending id/index order. It carries a static/dynamic
softness asymmetry — a stiffer, less-damped constraint for any pair touching a
non-dynamic body — and exposes `LastStepQuiescentPairCount` as a READING toward
a future sleeping/islands decision; every pair still runs its full iteration
budget regardless of that count. **`FixedRigidWorld` is not wired into any
World tick** — nothing here is verified by running `Puck.World`.

`FixedRigidSolverOptions.Restitution` and `.RestitutionThreshold` drive one
post-substep restitution pass per `Step`: a slot whose pre-solve closing speed
clears the threshold is driven toward `-Restitution` times that closing speed,
clamped so the accumulated normal impulse never goes negative. The default
coefficient is zero, so a caller that never sets it sees no bounce.

`FixedRigidSolverOptions.Friction` drives a coupled 2×2 tangential solve during
the unbiased relax pass only — never while a biased position correction is
applying, so a push-out never reads as a physical force. The two tangent
directions are built once per slot each `Prepare()` by
`FixedVector3.OrthonormalBasis`, and the coupled effective-mass tensor between
them (nonzero whenever the tangents are not aligned with the body's principal
inertia axes) is inverted once and applied every iteration, mirroring how the
normal direction's own scalar effective mass is formed. The accumulated
tangential impulse is clamped to the friction cone `Friction · NormalImpulseRaw`
— the SAME raw the normal block that iteration just updated, not a step-wide
total — via an exact squared-magnitude compare, with `FixedQ4816.Sqrt` used
only on the branch that must rescale. Friction is per **slot**, which today
means per contact point: `FixedManifoldSlot` carries exactly one anchor, so a
multi-point manifold (a box lying flat, say) applies one friction cone per
point rather than one per manifold at a shared centroid. The default
coefficient is zero, so a caller that never sets it sees no tangential
resistance.

## 🪢 The tether constraint

`FixedTetherConstraint` is a distance-CAP (never a distance-PIN) constraint
between a body and a resolved anchor point: a no-op — bit for bit — while the
body sits inside the rope's length, and, once taut, a closed-form projection
that removes only the outward radial velocity component and leaves every
tangential component untouched, so a swing's momentum and a wall-kick's
redirect emerge from ordinary integration rather than being scripted. One-way
by construction (the anchor is taken by `in` and never written), so a
body-anchored rope is resolved by having the caller pass that body's CURRENT
pose through `FixedTetherConstraint.ResolveAnchor` each tick. `Reel` changes
the rope length at a caller-supplied rate, clamped to a caller-supplied floor,
through the same `FixedRateAccumulator` discipline the rest of this library's
per-tick rates use. `CaptureState`/`FromState` preserve the rope limits and
that accumulator's remainder when a same-world checkpoint must continue on
the exact next fixed-point fraction.

## Bounded local perception

`FixedSpatialNeighborhood` freezes a set of fixed-point positions into an ordered
grid. A query examines at most 27 cells and an explicit candidate budget, even
when thousands of points coincide. Nearby occupied cells share attention;
the caller's deterministic sample ordinal rotates the occupants examined.
The result is the nearest retained subset of that sample, not a promise of the
globally nearest neighbors when the budget binds. `FixedNeighborhoodWork`
reports both inspected work and unexamined candidates. Memory capacity and
perception work are independent limits.

Grid width bounds query radius. Rebuild and query reuse construction-time
storage, and squared-distance comparisons use wide raw integers without
rounding. This is a perception primitive, not a collision broadphase: a contact
solver cannot discard contacts merely because an attention budget ran out.
`FixedSpatialNeighborhoodTests` compares complete queries with an independent
integer oracle and exercises coincident crowds, rotating attention, coordinate
extrema, input-order invariance, and steady-state allocation.

`FixedFlockSteering` consumes that bounded frozen sample. It blends separation,
affinity-weighted centroid attraction, independently weighted velocity alignment,
goal direction and heading persistence. A support normal projects steering into
the body's actual tangent plane; a zero normal keeps all three dimensions.
Coincident pairs receive opposite deterministic separation directions. Affinity
and goal selection are caller policy, so following a competent stranger need not
imply attraction or friendship. The kernel returns intent, not a collision-free
trajectory. Its steering decomposition follows [Reynolds' steering model](https://www.red3d.com/cwr/steer/gdc99/).

## 🧭 Navigation kernel (`Navigation/`)

`NavigationRuntime` is a deterministic, bounded route-search kernel over a set
of finite grids a host compiles once from its own document boundary into
`NavigationDomainInput` — the kernel parses no document and holds no Schema or
Server reference; a host's live-medium field implements the narrow
`INavigationMediumField` seam instead of the kernel depending on that
representation. `Puck.World.Server.WorldPopulation` is today's host: it
compiles `navigation.domains` rows and bridges the field lattice below
through that seam (see [its README](../Puck.World.Server/README.md#navigation)).

A `Surface` domain samples ground through the host's `IWorldQuery.TryGroundHeight`,
proving lower/head clearance, slope, and step limits; every admitted edge is
proven with swept spheres from `maxStepHeight` above the foot up to the head
(the step rule already admits anything lower, and a sphere sweeping the floor
it stands on advances one contact skin per march step) and stored in one
26-bit mask per cell. `Volume` and `Medium` domains use the same swept-sphere
edge proof in three dimensions. A `Medium` domain additionally resolves its
field name to an ordinal once and checks the agent clearance volume at each
live node plus half-cell-or-shorter swept boxes on search and before
following the next cached edge, through `INavigationMediumField`; every
intersected voxel's free surface is checked, including dry caps between wet
layers, so field evolution never needs rebaking static solids.

Without `Shared`, search is bounded A* over reused arrays: integer costs
(1000/1414/1732), stable `(f, h, nodeOrdinal)` ties, and the domain's own
expansion/path ceilings. Domain search workspace allocates once at
construction; per-body route storage is a host concern, so a steady-state
search allocates nothing here.

When a host replaces its solid query, a domain workspace may be retained only
after the replacement provider returns the same ground, occupancy, clearance,
and every static edge result for the domain's complete grid. The retained
workspace forwards later off-grid segment checks to the replacement provider;
medium domains also require the same field revision. A failed proof compiles a
fresh domain, and shared destination trees remain with the workspace only when
that proof succeeds.

A domain carrying `Shared` runs queued reverse-Dijkstra searches instead,
with stable `(cost, nodeOrdinal)` ties and one aggregate expansion allowance
per domain per simulation tick (each expansion inspects at most 26 edges).
Resident goals take turns, one expansion at a time; unfinished requests
continue on later calls to `BeginStep`. The shared tree can eventually cover
every cell in the domain; `MaxExpandedNodes` remains the independent A* limit,
while `MaxPathNodes` also bounds paths extracted from shared trees.

The shared cache key is the domain and destination cell — not a leader,
friendship, or requester identity. Completed trees are evicted
least-recently-used using bounded, distinct recency ranks, so repeated
requests cannot erase the order of other goals; pending requests pin a tree
for the next search step, and a full cache with no idle slot reports
`CapacityLimited` rather than launching an unbudgeted independent search. A
change to the referenced medium field invalidates every resident tree; this
implementation restarts affected trees rather than incrementally repairing
them, so rapidly changing water can delay a distant request under a small
budget. It does not provide crowd collision avoidance, bottleneck
reservations, hierarchical long-distance routing, or group membership. The
many-agents/one-destination approach is informed by
[Emerson's crowd pathfinding chapter](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf),
using finite domain trees rather than that chapter's tiled hierarchy.

`CaptureShared`/`RestoreShared`/`ValidateShared`/`AppendSharedHash` checkpoint
and hash the shared scheduler: resident goals, discovered costs/successors,
settled flags, pending starts, eviction ages, and the scheduler cursor. Node
digests are cached in 64-cell blocks — only changed blocks rehash, and an
unchanged tree contributes its cached digest in constant time. Pending starts
hash in sorted order from their bounded request list, never by scanning the
domain.

## 🌾 Field lattice (`Fields/`)

`FieldLattice` is the reaction integrator behind a world's live field-lattice
state — the other half of the seam `FixedFieldContactSolver` sits on: it
measures a body against a scalar field, and this evolves one. It parses no
document: it builds and reinstalls from a plain `FieldLatticeInput` (topology,
field envelopes, reactions, and paint, all in fixed point, fields addressed
by ordinal) a host compiles once from its own document boundary, the same
seam `NavigationDomainInput` crosses for the navigation kernel.
`Puck.World.Server.WorldPopulation` is today's host — see
[its README](../Puck.World.Server/README.md#field-lattice).

Six reaction kinds run in document order each cadence step: `Diffuse` (toward
the face-neighbour mean, Jacobi-style — snapshot then write), `Decay` (toward
zero), `Transform` (ordered writes where every condition holds), `Emit`
(deposits into a body's coupled cell from a keyed tag row), `Expose` (writes a
keyed tag row from a field comparison at a body's coupled cell), and `Flow`
(mass-conserving downhill transport over a combined terrain height, with an
optional edge spill into a scalar state row). A reaction's own read/write
field and state sets are derived once per install and drive the `world.fields`
read-back's dependency plan — informational only, since `Step` always executes
in document order regardless. `IFieldLatticeHost` is the body-position and
state-row seam a step reaches through, so the kernel never allocates a
per-call delegate.

`TryBodyCellOf` resolves the column a body couples to for `Emit`/`Expose`
and for the `MediumSurface` a medium hold floats against — Y is admitted up
to a DERIVED coupling ceiling (the volume's own top plus the tallest surface
any height-bearing field can raise), not just the raw voxel volume, since a
body standing on a one-layer ground lattice always rests above the half-unit
slab a bare inside test would refuse. `IsInsideMedium`/`IsSegmentInsideMedium`
give navigation's medium domain the same free-surface reach as a point test,
proven over a swept clearance box rather than sample points alone.

`FieldLatticeSolid` (an `IFieldEvaluator`) turns a lattice's height columns
into a contact field — exact within two cells of a column, a conservative
lower bound beyond — and `UnionField` composes it with another field
(a world's authored solids) by nearest distance, so a glacier or a filled pond
is real geometry a body's contact resolve reaches through the ordinary field
seam. `Checkpoint`/`Capture`/`Restore`/`AppendStateHash` and the delta stream
(`TakeDeltas`) are the host's checkpoint/snapshot/hash boundary; `CanInstallInput`/
`InstallInput` let a compatible reaction-only, colour, or paint edit replace
the live plan without migrating cells — a topology, cadence, or field-envelope
change refuses and asks for a host restart.

## 🚀 Basic use

```csharp
using Puck.Maths;
using Puck.Physics;

GravityBody[] bodies = [
    new(
        Position: new FixedVector3(
            X: FixedQ4816.FromInteger(value: -1),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ),
        Mass: FixedQ4816.FromInteger(value: 2)
    ),
    new(
        Position: new FixedVector3(
            X: FixedQ4816.FromInteger(value: 1),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ),
        Mass: FixedQ4816.FromInteger(value: 2)
    ),
];

var accelerations = new FixedVector3[bodies.Length];
IGravitySolver solver = GravitySolvers.Create(GravitySolverKind.AdaptiveFmm);

GravitySolveStatistics work = solver.ComputeAccelerations(
    bodies: bodies,
    accelerations: accelerations,
    parameters: new GravityParameters(
        GravitationalConstant: FixedQ4816.One,
        SofteningLength: FixedQ4816.FromDouble(value: 0.05)
    )
);
```

A zero-mass body is a target but not a source, which makes massless probes
possible. Mass and the gravitational constant must be non-negative. The positive
Plummer softening length prevents a singularity and must have a non-zero
representable square.

`FastMonopoleOptions.OpeningAngle` and `AdaptiveFmmOptions.OpeningAngle` control
the speed/accuracy tradeoff. Smaller values open more cells. Zero delegates to
the pairwise solver and is therefore bit-identical to the oracle. The defaults
are `0.5` and `0.4` respectively, both on `UnitInterval32`'s closed unit
interval.

## 📐 Determinism and workspace reuse

All three solvers use `FixedQ4816`, `FixedVector3`, the vector's checked
single-rounding squared norm, and the deterministic fixed-point square root; the
wide rounded divisions and mixed-scale products come from Puck.Maths'
`FusedArithmetic` and `FixedSymmetricSolve`, so the solvers round exactly the way
every other Maths consumer does. The octrees use a power-of-two root, stable
octant partitioning, exact `Int128` mass moments, and ties-to-even centers of
mass. The FMM keeps local gradients on `FixedQ3232`'s finer grid, narrows only
when applying one to an acceleration, and opens a cell pair instead of failing
when a gradient would leave that grid. If two individually representable local
gradients cannot be combined during L2L, the ancestor expansion is carried to
the leaf and evaluated there; no accepted contribution is discarded. Results and
work statistics are bit-identical for identical ordered inputs.

A zero-mass probe never contributes force, but the two hierarchical solvers treat
it differently in tree construction: `FastMonopoleGravitySolver` builds its
octree from positive-mass sources only, so probes cannot perturb other bodies'
results, while the adaptive FMM partitions every body — adding or moving a probe
there changes which cell pairs are accepted and shifts other bodies' results at
approximation order.

Both hierarchical solvers retain and reuse their index, partition, and node
arrays. A single instance stops allocating after it reaches a workload's
high-water mark, but that mutable workspace means the instance is **not
thread-safe**.

## 📋 Core types

- **Solvers** — `IGravitySolver`, `GravitySolvers`, `GravitySolverKind`,
  `PairwiseGravitySolver`, `FastMonopoleGravitySolver`,
  `AdaptiveFmmGravitySolver`.
- **Inputs and results** — `GravityBody`, `GravityParameters`,
  `GravitySolveStatistics`, `FastMonopoleOptions`, `AdaptiveFmmOptions`.
- **Contact geometry** — `FixedBodyColliderVolume`, `FixedDynamicBodyContacts`,
  `FixedStaticCollider`, `FixedContactPush`.
- **The contact seam** — `IContactField`, `ContactResolution`,
  `FixedStaticContactSolver`, `FixedFieldContactSolver`.
- **Surface-attach query** — `FixedSurfaceQuery`, `FixedSurfaceAttachCandidate`,
  `FixedSurfaceColliderSource`.
- **Rigid solver** — `FixedRigidSolver`, `FixedContactCandidate`.
- **Navigation kernel** — `NavigationRuntime`, `NavigationRuntime.Domain`,
  `NavigationDomainInput`, `NavigationCapacity`, `NavigationSharing`,
  `INavigationMediumField`, `NavigationStatus`, `NavigationSharedCheckpoint`,
  `NavigationTreeCheckpoint`, `NavigationTreeNode`.
- **Multi-body coupling** — `FixedTwoBodyKernel`, `FixedTwoBodyContact`,
  `FixedPairManifoldSlotTable`, `FixedRigidWorld` (shape only, not wired into
  any World tick).

## 🧪 Verification

The [test project](../../tests/Puck.Physics.Tests/README.md) compares Barnes–Hut
and FMM with the exact oracle, pins every solver's repeatability and overflow
fallbacks, exercises the contact kernels, and proves large-population reductions
in evaluated terms without relying on a machine-specific stopwatch. Its
mechanism and sabotage coverage for the rigid solver — soft constraints, warm
starting, persistent manifolds, speculative activation, restitution, and
friction — lives beside the kernel laws in the same project.
