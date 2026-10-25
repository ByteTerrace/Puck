# Puck.Physics.Tests

`NavigationRuntimeLawTests` also checks checkpoint restoration with a counting
query: empty shared-navigation state makes no geometry calls, malformed scheduler
state still refuses, and the first later route matches uninterrupted navigation.
Resident trees require geometry validation and refuse a forged successor edge.
The same counting query shows an unrouted domain retained across a query
replacement without sampling either provider, then baking against the
replacement; a routed domain still rebuilds when that geometry changed.

These tests keep the Physics kernels honest through separate evidence:

- the exact solver proves two-body direction, source/target semantics, refusals, and the bit-identical oracle path;
- the fast solver proves repeatability after workspace reuse, a measured error envelope against that oracle, and a
  structural-work reduction at 4,096 bodies;
- the adaptive FMM proves its M2L and L2L passes actually execute, stays inside an oracle-measured error envelope beside
  Barnes–Hut, remains bit-deterministic and allocation-free after workspace warm-up, and reduces 4,096-body interaction
  work below the quadratic baseline; overflow fixtures pin both rejected M2L pairs and deferred L2L expansions;
- the contact cases pin dynamic compound correction direction and the policy-free analytic static push contract;
- `ConvexContactWorkLawTests` probes shallow overlap and separation at support-surface offsets across chamfered
  and rounded boxes, the thin exponent-4 chair and cylinder rims, using both sphere and capsule ends;
- `SharpBoxContactLawTests` checks thin rotated boxes against sharp static corners and dynamic capsule/box
  corner corrections, including reversed body order, local volume offsets and clearance after correction;
- `SphereBoxContactLawTests` checks shared static/dynamic sphere-box geometry at faces, edges, corners, interior
  centers, and rotated faces, plus one-raw-unit overlaps and zero warmed allocations;
- `FixedSpatialNeighborhoodTests` compares complete grid queries with an independent wide-integer distance oracle,
  and pins bounded candidate work, independent cell/occupant rotation (including one-inspection budgets), zero steady-state allocation, and
  coordinate-extreme behavior at 4,096 points;
- `FixedFlockPipelineStressTests` combines perception and steering for 4,096 moving creatures, including initially
  coincident crowds. It checks bounded work, zero warmed allocations, and identical trajectories under reversed
  body-update order. Its reported timings exclude world rules, collision, navigation, and rendering;
- `FixedSurfaceQueryTests` pins the nearest-surface-point query's per-kind exactness (box face/edge/corner, sphere,
  half-space), the unit-outward-normal contract, reach inclusion at the boundary, the `(Source, ColliderIndex)`
  tie-break under 1,000 shuffled rebuilds of a set containing an exact tie, and the directed variant's
  angle-before-distance candidate ordering;
- the two-body kernel cases pin what is exact (impulse sign antisymmetry, a mass-symmetric pair's velocity deltas)
  against what is only bounded (an asymmetric pair's momentum residue, counted in applications and shown sensitive to
  a restored gravity term), the candidate canonical-order key's coverage of the second anchor, and the checked
  inverse-mass sum's overflow refusal;
- the `FixedRigidWorld` ordering cases pin candidate-order permutation invariance at fixed body ids, single-pair
  relabelling invariance, and tombstoned body-id retirement;
- the `FixedRigidSolver`/`FixedManifoldSlotTable` mechanism battery (`Fixtures/`, `Geometry/`, the root-level
  `*LawTests.cs` files) drives six deliberately non-planar fixtures through soft constraints at a substep width,
  sequential impulses with warm starting, persistent manifold slots, speculative activation, bounded deep-overlap
  recovery, restitution, and coupled-tangent friction—each law paired with the solver option that sabotages it, so
  the red run is a change of mechanism rather than a change of expectation;
- `Measurements/` holds the report-not-assert facts: softness coefficients, iteration budgets, field-sample budgets,
  sabotage outcomes, and the two-dynamic-body precision floor (mass/inertia placement windows measured against real
  `FixedMassProperties` refusal, an accuracy floor against a `BigInteger`-rational oracle, and a settle-jitter sweep
  across mass ratios)—every fact asserts against its own measured numbers, never a guessed threshold, and every
  measurement fact carries the `Measurements/MeasurementCollection` collection so the shared report file's sections
  stay in a stable, non-interleaved order without serializing the rest of the assembly;
- `TetherConstraintLawTests` proves `FixedTetherConstraint`'s distance-cap contract: determinism, the exact slack
  no-op, the taut projection never exceeding the rope length, exact preservation of every non-radial velocity
  component at the taut transition, no net energy injection from the constraint over a pendulum swing, the one-way
  body-anchored drag (the anchor body is provably untouched), and reel-in's monotonic, rate-accurate shrink.

The work assertion counts source evaluations and accepted monopoles rather than using a wall-clock threshold. Runtime
measurements remain machine- and workload-specific; the count is the deterministic fact that distinguishes a real tree
walk or cell-translation pass from an accidentally quadratic implementation.

## Layout

| Path | Holds |
|---|---|
| `../../src/Puck.Physics/FixedRigid*.cs` | The production solver, body/options/contact API, mixed-scale arithmetic, and substep loop. |
| `../../src/Puck.Physics/FixedSoftConstraint.cs` | The soft-constraint coefficient chain, formed at the substep width `h = 1/(rateHz·n)`. |
| `../../src/Puck.Physics/FixedManifoldSlotTable.cs` | Persistent slots and deterministic association, matching, and eviction. |
| `../../src/Puck.Physics/FixedSurfaceQuery.cs` | The nearest-surface-point query—the anchor primitive climbing and grappling both resolve against. |
| `Geometry/SpikeGeometry.cs` | Shapes, absolute placement, and the three candidate generators (half-space, slab, signed-distance field). |
| `Fixtures/` | The `SpikeWorld` harness, `SpikeBodies`, and the six scenario fixtures. |
| `Measurements/` | The measurement facts (`MeasurementTests`, `ProbeTests`) and their shared sink (`MeasurementReport`, `physics-measurements.txt`). |
| `*LawTests.cs` (root) | The mandatory laws—kernel-level, world-ordering, the `FixedRigidSolver` mechanism/sabotage battery, and `FixedTetherConstraint`'s distance-cap laws. |

## Contracts worth knowing before editing

- **The solver never sees an absolute position.** `FixedRigidBody` carries velocities and a per-step displacement; the
  absolute placement lives on `BodyPose`, which only the candidate generators read. A separation is re-derived inside a
  step from the displacement the solver itself accumulated.
- **`h` enters only through the product `hω`.** The softness chain forms that product before anything is squared. A
  bare `h²` is a defect, not a shortcut.
- **Ordering is part of the result.** Candidates are canonically ordered by a total key, slots are an ordered array,
  and eviction picks by `(lastTouchedStep, accumulatedImpulse, slotIndex)`. No hash container is read anywhere.
- **Iteration budgets are never cut short by a tolerance.** The solve runs its whole budget; `IterationsToConverge`
  reads the recorded residual profile afterwards, so a measurement cannot change the trajectory it measures.
- **Every mixed-scale product uses the refusing kernel face.** A result that leaves its carrier is counted in
  `FixedRigidSolver.RefusalCount`; every fixture asserts that count is zero.
- **A Restitution=0 twin IS the pre-restitution state.** `ApplyRestitution` reads `FixedRigidSolverOptions.Restitution`
  nowhere before its own body, so a second world built identically but with `Restitution = FixedQ4816.Zero` is
  byte-identical to the tested run through the end of the substep loop—the technique `RestitutionLawTests` uses to
  hand an independent oracle measured inputs without re-deriving the trajectory that produced them.
- **A Friction=0 twin works the same way, with one difference.** `SolveFriction`'s own delta computation runs
  unconditionally regardless of the coefficient—only the friction cone (`Friction · NormalImpulseRaw`) differs, and
  at `Friction = FixedQ4816.Zero` that cone has zero radius, so the accumulated tangential impulse is always clamped
  back to exactly zero. The twin's post-Step state is therefore what the tested run's own friction call saw, valid
  only for a fixture with exactly one Constraint slot: a second slot's own normal and friction impulses run after the
  first's, in the same relax pass, and would otherwise be baked into what a twin-based measurement reads back.
- **Friction is per slot, which is per contact point today.** `FixedManifoldSlot` carries exactly one anchor, so
  there is no manifold-level grouping to weight a shared centroid across—a box lying flat applies one friction cone
  per corner rather than one cone for the whole face. `FixedManifoldSlot.FrictionImpulse` persists as a world-space
  vector (not two scalars against the tangent basis) because that basis, `Tangent1`/`Tangent2`, is rebuilt from
  `Normal` every `Prepare()`.

## Running

```text
dotnet test tests/Puck.Physics.Tests/Puck.Physics.Tests.csproj -c Release
```

The measurement file lands at `bin/<configuration>/net11.0/physics-measurements.txt`.

## Documentation

📚 [Physics kernels](../../docs/reference/physics.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
