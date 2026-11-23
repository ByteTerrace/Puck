# Physics kernels

Puck.Physics owns the engine's deterministic fixed-point simulation kernels:
exact and scalable gravitational fields, compound dynamic-body overlap, analytic
static contact geometry, a substepping rigid-contact solver, a bounded A*/
shared-search navigation kernel under `Navigation/`, and—under
`Motion/`—the body motion-program core. Everything here is built on Puck.Maths,
so identical ordered inputs return identical results—bit for bit—on every
machine and backend at a fixed code version. Replaying a simulation also requires
the host to preserve its inputs and authoritative state.

Callers own world-document compilation, authority, gameplay classifications such as walkability, and the
final application of contact results. A kernel returns a correction or an
acceleration and leaves the decision of what to do with it to its caller.

Start with [Basic use](#basic-use) for a complete gravity calculation. Choose
[contact and rigid-body kernels](#contact-and-rigid-body-kernels) for collision
response, [the tether constraint](#the-tether-constraint) for rope motion,
[bounded local perception](#bounded-local-perception) for steering samples,
[navigation](#navigation-kernel-navigation) for routes, or
[field lattices](#field-lattice-fields) for evolving scalar fields. The
[motion-program core](#the-motion-program-core-motion) supplies instructions
that a host executes against its own body state.

## Basic use

Run this complete C# example in a console project referencing Puck.Physics. It
computes acceleration for two equal masses; applying that acceleration to
velocities and positions remains the caller’s next step.

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

Console.WriteLine(accelerations[0].X > FixedQ4816.Zero); // True: toward the right mass
Console.WriteLine(accelerations[1].X < FixedQ4816.Zero); // True: toward the left mass
```

A zero-mass body is a target but not a source, which makes massless probes
possible. Mass and the gravitational constant must be non-negative. The positive
Plummer softening length prevents a singularity and must have a non-zero
representable square.

`OctreeGravityOptions.OpeningAngle`, shared by `FastMonopoleOptions` and `AdaptiveFmmOptions`, controls
the speed/accuracy tradeoff. Smaller values open more cells. Zero delegates to
the pairwise solver and is therefore bit-identical to the oracle. The defaults
are `0.5` and `0.4` respectively, both on `UnitInterval32`'s closed unit
interval.

## Choose a gravity solver

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
Cartesian—the deliberately low-order FMM tier, not an unbounded
spherical-harmonic expansion.

## The motion-program core (`Motion/`)

`Motion/` holds the instruction vocabulary a body advances under and nothing that
reads a document: `BodyMotionOp` (the closed opcode set), `CompiledBodyMotionProgram`
(the compiler that validates a declared name/version/kind/opcode set and its
operations' parameters, such as `ComputePlanarTargetVelocity`'s `FixedPlanarTarget`,
and groups the selection into its intrinsic host phases), the action-state and instruction IR
(`CompiledBodyInstruction`, `CompiledActionStateSlot`, `CompiledActionStateEnvelope`),
and fixed-point tuning primitives. The World schema owns the gate-bearing host records
(`CompiledActionSpec`, `CompiledTrigger`, `CompiledFactTrigger`, `FixedBodyShaping`, `FixedMotionTuning`);
their `Puck.State.Rules.GateToken` programs run through `RuleEvaluation.Decide`, with
`ActionGateOperand` reading the World's `IActionGateFacts`. The remaining tuning primitives are in Physics (`FixedMotionDefaults`, `FixedMotionScalarEnvelope`, `FixedSpeed`, `FixedTurn`,
`FixedUpTurnRates`, `FixedObstructionLatch`).

`FixedTimedMove.Sample` reads cumulative displacement and signed yaw at an
offset in engine ticks. The host takes the difference between successive
displacement samples and sends it through the ordinary body sweep at the
sampled orientation. The samples cover the authored move's translation. The volume
swept by a rotating collider between samples belongs to
[predicted sweep and frontier](../plans/readable-interaction.md#build-order).

`ShapeVelocity` reads the one unified velocity-shaping table,
`FixedMotionTuning.Shaping` (`FixedBodyShaping[]`): the first row whose gate
opens governs—a row with no `Across` facet shapes the whole vector through
the engage/release response law (`FixedShapingAlong`), a row carrying one runs
the anisotropic drive decomposition instead (heading-frame longitudinal/lateral/
residual lanes, each converging at its own rate, the longitudinal one on the
commanded target's advance along the heading), and a row naming `Dynamics`
(a compiled `Puck.Maths.SecondOrderDynamics.SecondOrderStep`, a pole-matched
second-order follower the host steps once per tick) shapes it through that
follower instead of either—exactly one of `Along` or `Dynamics` per row. The
forward target and the steering rate every row's drive decomposition reads are
the tuning's own `FixedSpeed`/`FixedTurn`, not a second spelling; `FixedTurn`'s
own speed-scaled authority curve and the governing row's `TurnScale` apply
identically whichever yaw-writing frame operation (`ResolveDriveFrame`,
`ResolveYawAttitudeAndPlanarFrame`, `IntegrateLocalAttitude`) is selected. A
held low-traction row (a kart's drift) is an ordinary row gated on a `Held`
predicate (`ActionGateOperand.Held`, a live channel-threshold read through `IActionGateFacts`),
authored ahead of the row it overrides. An absent engage/release/reversalRate/lateral
rate compiles to an explicit instant-convergence flag; zero is not overloaded
as either instant or disabled. The vertical channel is a hold row's
own concern (`FixedBodyHold.Gravity`/`.Thrust`, `ResolveHold`/`ApplyHold` in
`BodyHold.cs`)—every Motion-kind kit authors at least one, so a drive kit's
own gravity and MoveUp thrust ride its hold list exactly as any other kit's do.

The translation from an authored world row into these shapes lives with the
authoring vocabulary, in `Puck.World.Schema` (`BodyMotionProgramFactory`,
`BodyActionSpecFactory`, `WorldMotionTuningFactory`). Execution—the per-phase
stages that read and write a body's pose, velocity, and action state—belongs to
the host that owns that state (`Puck.World.Server.WorldBody`); this project supplies
the program it executes, not the body it executes on.

The compiled predicates share the `ExpressionOp` comparisons and `ActionTriggerMode`
with [State and rules](state.md). Authored enums use the strict by-name converter
from Puck.Abstractions. The [project declaration](../../src/Puck.Physics/Puck.Physics.csproj)
and [project map](../project-map.md) own the dependency list.

## Contact and rigid-body kernels

`FixedBodyColliderVolume` is the shared sphere/capsule/oriented-box vocabulary.
`FixedOrientedBox` is the one owner of a box's geometry: its center, unit face
axes and half-extents, with the projection radius, world-axis extent
(`FixedColliderBounds.WorldBounds` reads it), box-local frame, closest point,
containment, sphere test, and the complete fifteen-axis separating-axis test
as a verdict (`Overlaps`) and with depth (`TrySeparation`). A body volume
builds one with `FromVolume`, and a placement's compiled spatial volume carries
one.
`FixedDynamicBodyContacts` supplies a conservative compound broadphase radius and
the deepest pair correction without knowing body identity or mutating state.
Its `Overlaps` is the event feed's overlap sense: a pair overlaps exactly when
the correction's narrowphase reports a positive push at zero margin, so a
collision event and a contact never disagree and a touching pair is neither.
`FixedSweepGrid` is the one broadphase over those radii: an X sweep over
bounding spheres sorted by least X then slot, crossed with a Y/Z grid whose
cells span the largest diameter. A pair search returns, in sweep order, the
later spheres whose bounds meet on all three axes, and charges its candidate
budget only after that cull; an active-only search skips pairs of two inactive
spheres. A box query returns every sphere whose bounds meet a box. Body
contacts, collision-event discovery and the sleeping index all use it, so they
see the same candidates.
Sphere and capsule pairs, dynamic or against a static sphere, answer through
one round-core rule (`TryRoundPush`): the segment witness, the full-width
distance against the summed radii, and an explicit tie axis when the cores
meet exactly. A body's velocity loses its approach to a contact through
`FixedVector3.LimitAlong` with the inward axis and a zero bound, in the
ordinary, field and seam responses alike. A
box-vs-box pair depenetrates by `FixedOrientedBox.TrySeparation`: both boxes'
face axes and their nine edge cross products, each at unit length, so an
edge-edge contact reads its true minimum.
Sphere/box pairs instead use the closest point on the oriented box, including
its faces, edges, and corners. A sphere center inside the box exits through the
nearest face, with ties resolved X, Y, then Z. Static and dynamic sphere/box
queries share this geometry; reversing the moving side reverses the push.
Dynamic capsule/box pairs use the same closest segment/box features as static
capsules, including edge and corner normals; reversing body order reverses
the correction. Compound correction selection compares full-width squared
magnitudes so a shallow overlap is not
lost by rounding its square to the scalar precision.
`FixedStaticCollider.TryGetPush` does the same for analytic spheres,
axis-aligned boxes, oriented boxes, rounded boxes, chamfered boxes, superellipsoids, cylinders, and half-spaces, returning only a
`FixedContactPush`. An oriented box carries its own half-extents and a unit
rotation; the push carries the body into the box's frame, runs the
axis-aligned query there, and turns the push back, so a capsule, sphere, or
box meets the box's true faces and a face push points along that face's
normal. `FixedStaticCollider.Box` builds one from a center, rotation and
half-extents, and keeps the axis-aligned collider whenever the rotation
carries every box axis exactly onto a world axis. Every body volume uses
closest features on sharp boxes too (the rounded-box kernel with zero static
radius), including the complete separating axes when box cores intersect.

Box placements use `FixedStaticCollider.RoundedBox`: an oriented inner box
offset by a sphere. `Extent` carries the inner half-extents and `Radius` the
offset, the shape's authored `rounding` (zero, a sharp box, where the document
authors none), the same fillet the renderer draws; the outer flat faces stay at
the authored extents. Sphere contact uses the inner box with
the combined radii. Capsule and box contact measure their closest convex
features, including edge pairs, rather than a body bound. Intersecting cores
use separating axes with the offset added to their depth. The solver's
substeps, support, hold and tether queries all use this shape in fixed point.
The chase camera already queries the rendered SDF directly.

A chamfered Box compiles to one `FixedStaticCollider.ChamferedBox`, including
deep bevels. Its inner dimensions and chamfer match the drawing builder's
erosion (a chamfer and a rounding never share a shape, so its fillet r is
zero): subtract r from every half-extent and replace b with
max(0, b − r(2 − √2)). Its support map follows the actual lifted XY profile,
including the cap/profile corner and the final scalar offset. At a fixed Z,
the cross-section is a rounded rectangle clipped by the profile bevel;
the extrusion and cap bevel determine its permitted profile distance.
The supporting slice is found on the fixed parameter grid. GJK measures
separation and EPA finds a separating push over that support map for spheres,
capsules and boxes. The same witnesses answer support, hold and tether
queries. `SupportPoint` exposes the finite collider's supporting boundary
for diagnostics. No rounded-box inset or axis-rod approximation remains.

Sphere, Ellipsoid and Superellipsoid placements compile to one
`FixedStaticCollider.Superellipsoid` over their radii, swept by the margin,
including anisotropic radii and rotation; one scaled alike on every axis at
exponent two compiles to a sphere. At exponent two the collider is an
ellipsoid whose distance and normal are exact: `FixedEllipsoidProjection`
measures them, so a ball's push, a hold's nearest point and the diagnostic
distance read the true surface, and other bodies descend its closed-form
support. A spheroid (`SdfSolidGeometry.IsSpheroid`, which also chooses the
revolved ellipse its field emits) is therefore the field's own surface and an
exact piece of a carved creation's partition. A higher exponent uses its Lp
support map with fixed-point powers. The chair seat and back are exponent-4
superellipsoids; they therefore never go through the oriented-bound fallback.
Both convex kinds use Q32 intermediates for small simplex predicates and Q16
witnesses, with deterministic iteration limits and tie order.
Box does not admit a separate authored `rounding` on this vocabulary; that
facet belongs to Prism, Cylinder and Cone.

Circular cylinders and thin discs compile to `FixedStaticCollider.Cylinder`,
including their rotation, radius and half-height. `FixedCylinderContact`
shares the cap, side and rim closest-point geometry with `FixedSurfaceQuery`.
Sphere and capsule contact uses that surface, so the corners of a cylinder's
bounding box carry nothing. Upright capsule queries are analytic; a tilted
capsule uses a bounded fixed-point segment search. Box bodies retain their
bounds in the cylinder frame, tested against the circular cross-section;
tilted box edges are still conservative. A superellipsoid above exponent two
and the carved primitives remain bounded approximations in the analytic
provider, and a triaxial ellipsoid under a margin collides as its true
margined surface while its gauge field dilates farther along its long axes.
Rounded or chamfered circular rims currently use a contained cylinder core.
The diagnostic reports the length of its radial/height contraction as the
maximum inward approximation error. Such an error is a known approximation
within that shape-derived bound and red beyond it; outward error receives
only the arithmetic and authored-margin budget. The remaining fix is an
exact rim contact.

Every contact solve asks its surfaces at the contact skin plus a resting band
(`FixedContactPushMath.RestingBand`): a push leaves a body exactly at the skin,
so walkable ground within the band is a resting contact
(`FixedContactPushMath.Classify`) that grounds the body and clamps its approach
without moving it, and a body standing on or walking level ground stays
grounded through its ordinary sweep. A non-walkable surface in the band is not
a contact, so a body beside a wall is not pressed into it.

A walking body's `motion.groundSnap` is a downward reach, scaled with the
body (0.3 m by default; zero disables it). The controller projects its walking
velocity onto the support it stands on (`FixedStepUp.TryFollowSupport`: one
sweep that does not move the body, and a snap only when nothing holds it there),
then uses `FixedStepUp.TrySnap` where the completed step did not ground: one
support-distance query (`IContactField.TrySupportDistance`), then one landing
sweep down to it, which stands the body within the resting band and must ground.
The same landing serves step-up and snap. The query reads each collider's own push query with its skin
padded by the remaining reach (`FixedSupportDistance`): the separation of two
convex solids is convex along a straight descent, so a Newton step from below
lands on a flat face in one step and closes on a rounded edge in a few, never
passing the first touch. A field collider, which is not convex, is bracketed
against its own push alone. A body step's two snaps share one answer where the
body has not moved between them (`WorldBody`'s `BodySupportMemo`, keyed by the
field, the volumes' derivation, the start, orientation, up and reach). A crest removes the climb's upward velocity; a
descent follows its surface. Jump and upward launch operations release
grounding before integration, and a drop past the reach becomes a fall.
The old inward-speed `groundStick` setting is removed.

Attached kinematic support uses the same narrowphase and walkable-normal
test against the previous tick's geometry. Its change of position and yaw
carries the rider's foot point before the rider advances. A yawed box is
rideable, as are the other supported analytic primitives; body orientation
remains the motion program's responsibility.
The solid must attach to the actual carrier: population ordering can move an
authored numeric body index. Confirm the placement's resolved body and the
attachment together before diagnosing a rider left behind as a contact failure.

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

The analytic solver's `ResolveSweep` splits translation into slices no longer
than half the body's least support radius, resolving each through its existing
contact kernel. Fast movement therefore meets a thin wall on its approach side.
A move no collider's bounds reach (within the contact skin of the box spanning
its start and end bounds) resolves its endpoint alone. Every slice measures the
lateral edge rule from the whole move's starting foot, the foot a step-up
raises from. The fixed-point rate accumulator preserves the exact unobstructed
displacement. It shares the field solver's work budget: exhaustion keeps only
the solved prefix and reports `Unproved`; an unrepresentable slice refuses the
whole move.

`FixedFieldContactSolver.ResolveSweep` proves a moving body's travel before it
solves contact at the endpoint, so a body never passes through geometry,
however fast it moves, however thin the geometry, and however close to it the
step starts. It sweeps every core sphere of every collider volume through
`Puck.Maths`'s `ICertifiedSweepQuery`, a certified conservative advancement:
- a sphere or box sweeps its centre at half its least radius;
- a capsule sweeps one core sphere per piece of its segment, one more piece
  than the segment's length in radii, so the pieces cover the whole core. A
  capsule sweeps at most `MaximumCapsuleSweepPieces` pieces, one per bounds
  query a single core may spend at `DefaultSweepBoundsQueryBudget` (64), so its
  sweep costs at most that budget squared. Collider validation refuses a kit
  capsule whose core needs more, measured at the longest length any body
  orientation rounds it to (`CapsuleCoreFitsSweep`), and any volume whose
  radius or least half extent quantizes under `MinimumColliderRadiusRaws` (2
  raws), whose half-raw core would round to a point (`VolumeCoreSweeps`);
- the least fraction any core proves is the body's, and the body moves that far.
  The fractions compare on the sweep's own 2⁻³² grid (`UnitInterval32`), since
  a long step's cores can differ by less than 2⁻¹⁶ of it;
- the endpoint solve then runs unchanged, so a body that would have crossed a
  face stops on it and resolves against the face it reached.

A step the sweep cannot run is refused before anything moves, by the same rule
for a program's solids and a lattice's alike: a displacement, or a core
sphere's start, that the Q48.16 carrier cannot hold, and a capsule core past its
piece ceiling (which only a body's runtime scale reaches once validation has
admitted it). The resolution names it in `ContactRefusal`, the body keeps the
position it started the step from, its velocity comes back as it came, and no
sweep or endpoint push runs; nothing is wrapped or clamped. In the World a
refused sweep is a full block: the body does not move this tick. `WorldBody`
captures everything the step writes before the step begins and restores it on a
refusal, for a walking, rigid or carried body alike: its whole transfer state
and integration residue (velocities, attitude, frame, rate accumulators, input
tape, timers, action state, sleep, hold and tether) with its pose and contact
facts, so a field added to either record is covered without a change. A
walking body's refused step also withdraws the effects, designations and
generator firings it emitted, so a tick the body did not take fires nothing.
An arriving body keeps the pose and velocity it was installed with. A body
refused this tick stays fixed until the tick ends. In a positional pair
correction, its partner takes the full displacement. In the rigid solver, the
refused body supplies zero velocity and zero inverse mass and inertia, and
the authored push-out limit governs the rigid partner's response. A tether
cannot pull a refused body, and its refusal passes no correction to its carrier.
A carrier is corrected only by
a proven contact: a sweep that held its body for want of proof (out of budget,
or at a box its field could not bound) marks the resolution `Unproved`, and
hands the carrier nothing either. The population narrates each transition once
on the `body.sweep` channel, refused and recovered, and `body.where` reads
`sweep=refused(...)` while the refusal holds.
A body approaching the carrier's end is ordinarily held a step sooner by its
sweep, whose box reaches the end before any core's start does and proves no
ground there; the refusal is met by a body already past that point, posed or
arriving there.

`FixedStepUp.TryStep` lifts a grounded body over a riser its ordinary step met
as a wall. It runs every move through the caller's own `IFixedContactStep`, a
sweep bound to one body's orientation, volumes and up axis, so it works over
either provider. It raises the start by the step height, moves the step's
planar displacement, and lowers onto support through `TrySnap`, no lower than
where the original step would have ended along up. Landing a sliver past the
support distance finds a shallow top contact rather than resolving a deeply
embedded endpoint through the nearer side.
`FixedContactPush.IsEdge` distinguishes a box edge/fillet or cylinder rim from
a face. `FixedStaticContactSolver.ResolveSweep` projects an approached edge's
push across up without lifting or converting forward drive into upward
velocity. Vertical landings and resting queries retain the measured rounded
surface normal, and a sloped face remains a ramp. The blocked sweep reports
`RequiredStepHeight`: the lift from the pre-move body's bottom to the touched
edge, including skin. Rounded boxes charge the inner edge plus its full fillet
radius; the coordinate along a tilted edge stays local to the contact, so a
ramp's distant apex cannot wall its low end. A surface witness at or below the
pre-move foot remains support. `FixedStepUp` requires the reported clearance;
repeated partial landings cannot turn a kerb into a ramp.
The raised across move must be free of obstruction and make more progress
than the blocked step. The landing must ground on a walkable surface, no higher
than the step height above the start and higher than the blocked step. A wall costs
two sweeps; an accepted step costs four sweeps and one support-distance query. The World calls
it from the kit's `motion.stepHeight`, zero by default.

Exact chamfered-box and superellipsoid contacts use GJK followed by EPA. GJK
reduction computes triangle barycentrics from oriented areas, avoiding the
cancellation in a squared-area Gram determinant. Closest points and their
barycentric weights stay in Q32 through exact squared-length comparison and
search-direction normalization; rounding a shallow residual to Q16 first can
aim the next support query back at a discarded vertex. The contact tolerance and iteration
budget do not compensate for that precision loss.
The EPA bootstrap expands the GJK simplex along its edge/face normals into one
tetrahedron, then expands its boundary faces. A support plane at zero depth
ends a touching query; no search through support-point subsets is needed.
A contact query stops GJK as soon as one support plane stands more than a
sixteenth of a metre behind the origin: every point of the difference lies at
or below its support, so no later simplex could come within tolerance, and the
query answers separated with the same verdict the full descent reaches. A body
standing beside a large curved collider (the valley's terrain masses at the
overlook) otherwise spent about 22 support evaluations converging on a
distance it never used. The nearest-point query (`FixedConvexContact.Nearest`)
still runs the full descent, since it reads the converged closest point.
Faces are oriented from a known interior point even when the origin lies on
their boundary. `FixedConvexContact.TryPush` also reports support-map evaluations
for deterministic work measurements: at most 65 GJK queries, two bootstrap
queries, 97 EPA queries and 26 face-normal recovery queries.

Each sweep takes an explicit bounds-query budget
(`DefaultSweepBoundsQueryBudget`, 64) and the contact skin as its contact
tolerance, so a body pressed against a wall spends a handful of queries a tick.
A sweep that runs out of budget, reaches a box the field cannot bound (one past
its frame), or is refused outright keeps only the ground it proved: a body never
moves through space no sweep proved clear. A solver built with no sweep, for a
field with no geometry, resolves at the endpoint alone. `FixedContactSweepWork`
counts the sweeps, their bounds queries, and how many ended in contact or
exhausted, as the deterministic `physics.sweep` work source; the World registers
its `Process` ledger, and a law hands its solver a ledger of its own. The
analytic solver counts into `FixedStaticContactWork.Process`, the
`physics.contact` source: resolves, exact collider queries, the convex queries
past their bounds rejection with their support evaluations, and pushes.

A step-sampling sweep has two tunnelling classes the certified sweep closes. A
field that overstates its distance (a gradient above one) lets a sample-trusting
step jump a surface. A body that starts within its radius of a thin wall and
moves less than its radius is only resolved at its endpoint, already past the
wall. A step long enough that its fraction rounds to the Q16 grid also jumps a
thin wall between samples. `CertifiedContactSweepLawTests` holds each with a
radius stepper as its red leg, along with the budget's prefix property, the
frame, the capsule core and its piece ceiling, a compound body whose cores'
fractions agree on the Q16 grid, the lattice's bounds out to the carrier's ends,
and a lattice step refused at them with its state unchanged;
`CapsuleSweepCeilingLawTests` holds the validation refusal; the
quarantined `experimental/tests/Puck.World.Canaries/thin-wall-sweep` script
describes the running-World check but is not selected by `puck canary`.

`FixedSurfaceQuery` is the nearest-surface-point primitive over the same
collider vocabulary—the analytic anchor query climbing (surface attach) and
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

### The rigid solver

`FixedRigidWorld` is the one rigid contact solver, for the World and for any
caller: a soft-step sequential-impulse solve over temporal substeps (integrate
velocities, warm start, biased solve, integrate positions, unbiased relax; then
restitution and the friction store), with the softness formed at the substep
width and a stiffer, less-damped softness for a contact with a static side. It
reads frequency, damping ratio and push-out speed from its options; the World
compiles them from `collision.bodyContacts`, with the standard package choosing
30 Hz, 10 and 3 m/s. A contact's authored rolling lever also serves as a patch
radius for a torsional row about its normal. Both angular resistance bounds
scale with the contact's normal impulse, so neither slows a body without load.
It owns no body or pair registry. A caller hands each `Step` the bodies to
integrate (`FixedRigidBody`, whose `Acceleration` is its own gravity) and the
`FixedRigidManifold`s to solve, both in canonical order; a manifold names its
two bodies (a static surface is a zero-mass `BodyA` with zero anchors), its
friction and restitution, and a `FixedPairManifoldSlotTable` whose slots carry
the warm-start impulses from step to step. The caller owns each manifold for as
long as its pair touches, so the slots are continuation state it checkpoints.

Contact geometry enters once a step. `FixedRigidContacts` turns one contact a
geometry query found into manifold candidates (`FixedTwoBodyContact`): a body
against a surface takes every support point of its face toward the surface
(`FixedRigidWitness.SupportManifold`: a box's four face corners named by corner,
a capsule's two caps, an ellipsoid's support point and a ring of four tilted
points), each with its own separation, so a lifted corner is a speculative
contact rather than a missing one; two boxes take the B face's corners clamped
onto the A box; a curved body or a kinematic side takes one witness point. The
substeps and passes re-derive every separation from the displacement the solve
accumulated, so no geometry query runs inside the solve. A manifold's points
are solved together (each takes an equal share of what it asks for from the
same velocities), and restitution likewise, so a flat impact never spins a body
on visiting order; friction runs in the relax pass only, along two tangents
clamped as one vector to the Coulomb cone. Every mixed-scale product goes
through the refusing face of the fused kernels and is counted
(`RefusalCount`). The manifold slot table associates by source, feature, normal
and nearest anchors, ages a missed slot by an idle count rather than an
absolute step, and evicts by a total key.

`FixedTwoBodyKernel` holds the scalar kernels a contact between two named
bodies needs: the combined effective mass, the relative normal velocity and
the impulse application, either body possibly static.

## The tether constraint

`FixedTetherConstraint` is a distance-CAP (never a distance-PIN) constraint
between a body and a resolved anchor point: a no-op—bit for bit—while the
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

## Navigation kernel (`Navigation/`)

`NavigationRuntime` is a deterministic, bounded route-search kernel over a set
of finite grids a host compiles once from its own document boundary into
`NavigationDomainInput`—the kernel parses no document and holds no Schema or
Server reference; a host's live-medium field implements the narrow
`INavigationMediumField` seam instead of the kernel depending on that
representation. `Puck.World.Server.WorldPopulation` is today's host: it
compiles `navigation.domains` rows and bridges the field lattice below
through that seam (see [its README](../../src/Puck.World.Server/README.md#navigation)).

A domain reads the host's static contact geometry, the `IContactField` its
bodies collide with without the solids that move each tick, through the same
resolve, swept resolve and support-distance queries a body's own step runs, so
a route the kernel plans is one a body of the domain's agent can walk. When the
geometry is an `IRegionalContactField` (`FixedStaticContactField`), each domain
reads only the part its footprint's region can reach (`Within`), and that part
identifies the domain's bake. A domain's constructor only sizes its search
arrays; its occupancy sweep and edge bake (`NavigationDomainBake`) run once,
on the thread pool after `BeginBakes`, or on the reader's thread the first
time a route request, an off-grid locomotion check, a `WalkableCellCount` read
or a host's `EnsureBaked` call needs it while no bake is in flight. A reader
that needs a bake still in flight waits for it, and the bake reads only the
domain's tuning and its geometry part, so no answer depends on which thread
baked or when. A host calls `EnsureBaked` when it restores a stored route or
search result, including a failed search with an empty path: the recorded
domain, goal, and status depend on a bake the kernel cannot see. Restoring a
shared-navigation checkpoint with no resident trees preserves the deferral.
Scheduler and empty-slot shape checks still run; a resident tree triggers the
bake to validate its recorded successor edges.

A `Surface` domain stands its agent as a capsule of its `agentHeight` and
`agentRadius`. A cell's ground is where a small probe sphere lowered from its
probe's start (origin height plus `probeUp`, so an author keeps that start under
the lintels a walker passes beneath) first comes to rest; the cell is walkable
where the capsule standing on that ground, its underside raised by
`maxStepHeight` (the step rule admits anything lower, so a stair's next riser
does not refuse a tread's cell), meets nothing. An edge passes the slope and
step limits and then sweeps the same raised capsule between the two cells at the
higher cell's ground, so it proves the room a body standing on either end needs
over the step it may climb; admitted edges are stored in one 26-bit mask per
cell. `Volume` and `Medium` domains prove a sphere of `agentRadius` at each cell
and swept between cells in three dimensions. A `Medium` domain additionally
resolves its field name to an ordinal once and checks the agent clearance
volume at each live node plus half-cell-or-shorter swept boxes on search and
before following the next cached edge, through `INavigationMediumField`; every
intersected voxel's free surface is checked, including dry caps between wet
layers, so field evolution never needs rebaking static solids.

Without `Shared`, search is bounded A* over reused arrays: integer costs
(1000/1414/1732), stable `(f, h, nodeOrdinal)` ties, and the domain's own
expansion/path ceilings. Domain search workspace allocates once at
construction; per-body route storage is a host concern, so a steady-state
search allocates nothing here.

When a host replaces its document, a domain keeps its workspace when its tuning
and capacity are unchanged and its geometry part compares equal to the one it
replaces. Equal parts answer every
query the bake asked identically, so the bake (finished or in flight) carries
over without sampling anything, and so does every goal field whose network keeps
the same domains in the same order. Medium domains
also require the same field revision. A domain whose tuning is unchanged but whose
geometry part changed in part (a door's response swap) takes its predecessor's
finished bake when the two parts bound their difference
(`IRegionalContactField.TryDifference`). `Domain.TryBeginRebakeFrom` schedules a
worker to copy the cells and edges and prove again only the cells whose sample or
edge sweeps the changed colliders could reach and the edges of their neighbours,
which gives the bake a whole bake would (`world.navigation` counts these as
`rebaked=`). A newer swap can use the same finished bake as its starting point
while that worker is still running. A route read waits for the current bake.
Any other domain compiles fresh, and a prior domain no runtime keeps has its
bake in flight cancelled.

Bodies navigating a domain marked `Shared` follow goal fields instead
(`NavigationRuntime.GoalField`, `NavigationRuntime.GoalStep`). A goal field
holds, for every cell of the goal's network that can reach the goal, the step a
body standing there takes on a cheapest route: a neighbour in its domain, a
junction into another domain at the same place, or the goal itself, one byte a
cell (`NavigationGoalField`). It is derived whole the first time a goal is read:
a reverse Dijkstra from the goal over the network's cells and junctions, each
step costed exactly as `FindRoute` costs it, and then each cell's step chosen
from the settled costs alone (the first forward step, in neighbour order and
then junctions in ascending node order, that reaches a cell whose cost plus the
step equals the cell's own; a junction only to the end settled first, so two
coincident cells never point at each other). The choice reads final costs, never
the order the search settled ties in, so a field is a function of its goal and
the baked domains. Many bodies heading to one place read one field a step at a
time and hold no route; a body pushed off its line reads the step from where it
now stands, so a crowd never replans. A tick costs a step read or two per
follower, and a field derivation per new goal or changed domain, never a search
per body.

A field is a cache, not state: which fields are resident decides work and
memory, never a step. The runtime keeps resident fields within
`NavigationCapacity.GoalFieldBytes` and drops the least recently read past it;
a dropped field is derived again with the same steps, and a field over a medium
domain is derived again once the live medium's revision moves. Nothing
checkpoints or hashes fields, so a restored authority with no field resident
steps exactly as one that kept them. A goal moving cell to cell (a body chasing
another) derives a field per cell it enters, which suits many bodies chasing one
target; a few bodies each chasing their own are cheaper on independent A*. The
many-agents/one-destination approach is informed by
[Emerson's crowd pathfinding chapter](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf),
with the network's whole domains in place of that chapter's tiles and portal
graph. Bottleneck reservations, local avoidance at crowd density and a portal
graph for networks too large to field whole are not provided.

Cells carry area types, and a cost profile prices them per kind of agent, as a
navmesh's area costs do. A runtime's `NavigationAreaSet` holds the types (ordinals
its host names; the kernel gives none a meaning) and the marked volumes in
precedence order: a cell takes the type of the last volume holding the point a
body standing on it stands at (`Domain.Foot`), else its domain's own
`NavigationDomainInput.Area`, else none. A `NavigationCostProfile` multiplies each
step's cost by the fixed-point multiplier of the area it steps onto (rounded down
once), costs an unmarked cell and an unnamed type one, and never steps onto a type
it excludes; a body already standing on one may step off it. `FindRoute`,
`Domain.FindPath` and `GoalField` take the profile; a route search scales its
distance heuristic by the profile's least multiplier
(`NavigationCostProfile.Minimum`, at most one), so the heuristic stays a lower
bound. A goal field is keyed by its goal cell and its profile, which compare by
their costs, so bodies that price areas alike share a field and bodies that do
not read their own. Areas price routes and never change a bake: each domain derives
its cells' types once per bake and area set (`Domain.AreaOf`), and a replacement
runtime whose area set is equal keeps its goal fields. The neutral profile
(`NavigationCostProfile.Neutral`) charges every step its length, the search
without areas.

`NavigationSearchWork` counts the work (`physics.navigation` in
`world.counters`): independent searches and their expansions, cross-domain
routes and theirs, goal fields derived and the cells they settled, steps read
and fields dropped. `world.navigation` reports the resident fields, their bytes
and the last tick's builds, cells and steps.

`NavigationRuntime.FindRoute` searches one route across the start domain's
network: a bounded A* over the union of its domains' cells, each step's cost
its length in millimetres, and on a surface one cell's length more for each cell
short of `RouteClearanceCells` that the cell stepped onto stands from the edge of
the walkable ground, so a route keeps off walls and passes a doorway through its
middle; the heuristic is the horizontal Chebyshev distance to the goal, which
never exceeds the cost left. `Straighten` cuts a route to the corners a walker
turns at, keeping each junction hop: from each kept node, the farthest later node
of its domain it can reach in a straight line. `Domain.IsStraightClear` checks
the baked cells and clearance margin, then sweeps the agent along the actual
line between samples. `TryLocateNear` also sweeps between the body's exact foot
point and the candidate surface cell, so a cell across a wall cannot start the
route. Two domains of one network (`NavigationDomainInput.Network`, the
authored `network`; unnamed domains share one network) join at a
junction, a pair of walkable cells whose route positions share X and Z
exactly and, between two surfaces, whose feet differ in height by no more than the lower
step height (two volume cells must coincide; a medium domain joins none), so a
route crosses between domains only where both prove the same ground and the
hop holds no unproven segment. Junctions exist only where grids align: cell
sizes exact in fixed point and multiples of each other, origins on the finer
grid. A search joins a pair the first time it settles a cell inside the second
domain's footprint and, for a surface, its probe's height band
(`Domain.CoversRouteHeight`), so it bakes only the domains it reaches.
`TryLocate` names the walkable cell a position stands on. The workspace
(`RouteWorkspaceBytes`) allocates on the first route search. This is the
multi-storey answer the single-ground surface grid lacks: a building whose
stairwell repeats every storey is a domain per level and per storey's
stairwell, joined on the treads and landings they share
(`worlds/debug/navigation.puck`). Route searches and goal fields share the
junctions, each node's partners kept in ascending node order.

## Field lattice (`Fields/`)

Lattice rows marked `simulationOnly` may keep a `heightScale` for leveling against a bed. They publish no surface and take no part in body contact or medium coupling.

`FieldLattice` is the reaction integrator behind a world's live field-lattice
state—the other half of the seam `FixedFieldContactSolver` sits on: it
measures a body against a scalar field, and this evolves one. It parses no
document: it builds and reinstalls from a plain `FieldLatticeInput` (topology,
field envelopes, reactions, and paint, all in fixed point, fields addressed
by ordinal) a host compiles once from its own document boundary, the same
seam `NavigationDomainInput` crosses for the navigation kernel.
`Puck.World.Server.WorldPopulation` is today's host—see
[its README](../../src/Puck.World.Server/README.md#field-lattice).

Six reaction kinds run in document order each cadence step: `Diffuse` (toward
the face-neighbour mean, Jacobi-style—snapshot then write), `Decay` (toward
zero), `Transform` (ordered writes where every condition holds), `Emit`
(deposits into a body's coupled cell from a keyed tag row), `Expose` (writes a
keyed tag row from a field comparison at a body's coupled cell), and `Flow`
(mass-conserving downhill transport over a combined terrain height, with an
optional edge spill into a scalar state row). A reaction's own read/write
field and state sets are derived once per install and drive the `world.fields`
read-back's dependency plan—informational only, since `Step` always executes
in document order regardless. The ordering test itself is
`ReadWriteHazard.Conflicts`, the one door the document-side field-program
compiler in `Puck.World.Schema` also resolves its dependency edges through, so
an edge means the same thing on either side. `IFieldLatticeHost` is the body-position and
state-row seam a step reaches through, so the kernel never allocates a
per-call delegate.

A transform Add may name `takeFrom`: the same raw fixed-point quantity is
debited from that cell's donor field and credited to the destination, bounded
by both envelopes. A retained rate cannot create supply after the donor is
empty. Flow rounds directional shares down and limits each pair by available
supply and destination capacity. `FieldLattice.ProcessWork` reports executed
steps and kernel cell/body visits through `world.counters`, outside state hashes.
Whole-document reload/reset calls `Reseed` alongside ordinary state reset;
live program edits and checkpoint restoration retain their separate semantics.

`TryBodyCellOf` resolves the column a body couples to for `Emit`/`Expose`
and for the `MediumSurface` a medium hold floats against—Y is admitted up
to a DERIVED coupling ceiling (the volume's own top plus the tallest surface
any height-bearing field can raise), not just the raw voxel volume, since a
body standing on a one-layer ground lattice always rests above the half-unit
slab a bare inside test would refuse. `IsInsideMedium`/`IsSegmentInsideMedium`
give navigation's medium domain the same free-surface reach as a point test,
proven over a swept clearance box rather than sample points alone.

`FieldLatticeSolid` (an `IFieldEvaluator` and an `IFieldBounds`) turns a
lattice's height columns into a contact field—exact within two cells of a
column, a conservative lower bound beyond—and encloses its answers over a box
by the nearest column within reach, so a certified sweep proves a body clear of
it. Its column indices are taken in a wide type and clamped before they narrow
to the lattice's own index, and the point query and its bounds read every
column's box and gap in one exact wide arithmetic, so a coordinate anywhere in
the carrier, its ends included, reads the columns it lies near and neither
answer wraps where the other does not. A lattice whose cell puts the solid's
two-cell reach past the carrier (`FieldLatticeSolid.CellFits`) is refused by
world validation, and the solid's constructor throws for one that reaches it
unvalidated.
`UnionField` composes it with another field
(a world's authored solids) by nearest distance, so a glacier or a filled pond
is real geometry a body's contact resolve reaches through the ordinary field
seam. `Checkpoint`/`Capture`/`Restore`/`AppendStateHash` and the delta stream
(`TakeDeltas`) are the host's checkpoint/snapshot/hash boundary; `CanInstallInput`/
`InstallInput` let a compatible reaction-only, colour, or paint edit replace
the live plan without migrating cells—a topology, cadence, or field-envelope
change refuses and asks for a host restart.

## Determinism and workspace reuse

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
results, while the adaptive FMM partitions every body—adding or moving a probe
there changes which cell pairs are accepted and shifts other bodies' results at
approximation order.

Both hierarchical solvers retain and reuse their index, partition, and node
arrays. A single instance stops allocating after it reaches a workload's
high-water mark, but that mutable workspace means the instance is **not
thread-safe**.

## Core types

- **Solvers**—`IGravitySolver`, `GravitySolvers`, `GravitySolverKind`,
  `PairwiseGravitySolver`, `FastMonopoleGravitySolver`,
  `AdaptiveFmmGravitySolver`.
- **Inputs and results**—`GravityBody`, `GravityParameters`,
  `GravitySolveStatistics`, `OctreeGravityOptions` (the shared leaf/depth/angle
  validation both octree option sets derive their controls from),
  `FastMonopoleOptions`, `AdaptiveFmmOptions`.
- **Contact geometry**—`FixedBodyColliderVolume`, `FixedOrientedBox`,
  `FixedColliderBounds`, `FixedDynamicBodyContacts`, `FixedStaticCollider`,
  `FixedContactPush`, `FixedSweepGrid`, `FixedSweepSphere`.
- **The contact seam**—`IContactField`, `ContactResolution`,
  `FixedStaticContactSolver`, `FixedFieldContactSolver`, `FixedStepUp`,
  `IFixedContactStep`, `FixedStepUpResult`.
- **Surface-attach query**—`FixedSurfaceQuery`, `FixedSurfaceAttachCandidate`,
  `FixedSurfaceColliderSource`.
- **Rigid solver**—`FixedRigidWorld`, `FixedRigidManifold`, `FixedRigidContacts`,
  `FixedTwoBodyContact`, `FixedPairManifoldSlotTable`, `FixedTwoBodyKernel`.
- **Navigation kernel**—`NavigationRuntime`, `NavigationRuntime.Domain`,
  `NavigationDomainInput`, `NavigationCapacity`, `NavigationGoalField`,
  `INavigationMediumField`, `NavigationStatus`, `NavigationSearchWork`,
  `NavigationRouteNode`, `INavigationRouteGoal`.

## Verification

The [test project](../../tests/Puck.Physics.Tests/README.md) compares Barnes–Hut
and FMM with the exact oracle, pins every solver's repeatability and overflow
fallbacks, exercises the contact kernels, and proves large-population reductions
in evaluated terms without relying on a machine-specific stopwatch. Its
mechanism and sabotage coverage for the rigid solver—soft constraints, warm
starting, persistent manifolds, speculative activation, restitution, and
friction—lives beside the kernel laws in the same project.

## Further reading

- [Deterministic numerics](maths.md) — numeric types, rounding, and state boundaries.
- [State and rules](state.md) — shared predicates and mutation vocabulary.
- [API reference](../api/index.md) — member-level contracts.
- [Reference](README.md) — other library and schema guides.
