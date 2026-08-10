# Puck.Dynamics.Spike.Tests

A throwaway-tolerant SPIKE. It proves rigid-body solver mechanics — soft constraints at a substep width, sequential
impulses with warm starting, persistent manifold slots, speculative activation, and bounded deep-overlap recovery — in
fixed point, on deliberately non-planar fixtures. Nothing here is production code, no world document is touched, and
no engine project depends on it.

## Layout

| Path | Holds |
|---|---|
| `Core/SpikeArithmetic.cs` | Mixed-scale dot, reciprocal, exact rational rounding, and the directed-bound wrappers. |
| `Core/SoftConstraint.cs` | The soft-constraint coefficient chain, formed at the substep width `h = 1/(rateHz·n)`. |
| `Core/SolverTypes.cs` | Scale placement, solver options (each sabotage is one named option), the body, the contact candidate and its canonical order. |
| `Core/ManifoldSlotTable.cs` | The persistent slots and the deterministic association, matching and eviction. |
| `Core/RigidSolver.cs` | The substep loop: integrate, warm start, biased solve, integrate positions, relax, restitution. |
| `Geometry/SpikeGeometry.cs` | Shapes, absolute placement, and the three candidate generators (half-space, slab, signed-distance field). |
| `Fixtures/` | The harness and the six scenarios. |
| `*Tests.cs` | The laws, and the measurement facts that write `spike-measurements.txt` beside the test assembly. |

## Contracts worth knowing before editing

- **The solver never sees an absolute position.** `SpikeBody` carries velocities and a per-step displacement; the
  absolute placement lives on `BodyPose`, which only the candidate generators read. A separation is re-derived inside a
  step from the displacement the solver itself accumulated.
- **`h` enters only through the product `hω`.** The softness chain forms that product before anything is squared. A
  bare `h²` is a defect, not a shortcut.
- **Ordering is part of the result.** Candidates are canonically ordered by a total key, slots are an ordered array,
  and eviction picks by `(lastTouchedStep, accumulatedImpulse, slotIndex)`. No hash container is read anywhere.
- **Iteration budgets are never cut short by a tolerance.** The solve runs its whole budget; `IterationsToConverge`
  reads the recorded residual profile afterwards, so a measurement cannot change the trajectory it measures.
- **Every mixed-scale product uses the refusing kernel face.** A result that leaves its carrier is counted in
  `RigidSolver.RefusalCount`; every fixture asserts that count is zero.

## Running

```text
dotnet test tests/Puck.Dynamics.Spike.Tests/Puck.Dynamics.Spike.Tests.csproj -c Release
```

The measurement file lands at `bin/<configuration>/net10.0/spike-measurements.txt`.
