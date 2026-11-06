# Puck.SignedDistance

Puck.SignedDistance provides the signed-distance-function instruction ISA,
packed program representation, authoring builder, deterministic CPU evaluator,
and the dual contouring that turns a sampled lattice into the frame's baked
meshes (`Baking`: `SdfSampledLattice`, `SdfDualContouring`).
The evaluators expose structural line-of-sight work envelopes derived from their
compiled programs and fixed march budgets. These counts carry no cycle price.

`Puck.SignedDistance.Illumination` holds the indirect-light controls the frame
carries (`SdfIndirectTier`, `SdfIndirectSources`, `SdfIndirectGains`) and the
octahedral direction mapping the sky environment map uses (`SdfOctahedral`).
A program is a field joined to an appearance (`SdfProgram.Of`). The field
constructor derives every word the field interpreter reads (`SdfProgram.FieldWords`)
from the field's inputs and the appearance's outline (`SdfAppearanceOutline`: the
material count, the glass materials, the declared screens) alone; `SdfAppearance`
validates and packs the materials, paints and screen surfaces into the stream's
tail. The mesh bake's identity is rooted at the field constructor, so no
appearance value or packing code reaches it. `SdfAppearance.Materials` exposes the
same snapshotted palette the packed shader reads, including immutable nested lists.

`SdfProgram.InspectInstance` reads exclusive packed ownership and culling facts for a
live instance. Its count includes instructions and their data/bounds, owned segment and
rigid-leaf rows, shape side tables and part bindings. Shared tables stay in the
program-level remainder; inspection never re-emits a prototype.
`BuildDynamicTransformBounds` collects the existing packed bounds by dynamic
slot for presentation-cache invalidation. It reuses finite instance certificates
for unskippable chains and reports an infinite bound for an unsupported dynamic
dependency; an unrelated static plane affects no dynamic slot.

The fixed-point superellipsoid's factored powers use a process-shared, bounded
exact-result memo (`SdfFieldEvaluator.PowerMemoRetainedBytes`). Each atomically
published cell holds the complete input key and result; a collision or concurrent
replacement causes a miss, which calls `FixedQ4816.Pow`. It changes no field
answer, shape exponent, interval certificate or march policy. Code generations
replace the memo, and it allocates no table per placement or evaluator. The
`sdf.point` counters distinguish hits, misses, bypasses and fallback work from
instructions visited and cumulative payload allocation; `world.budget` reports
the current generation's shared managed payload separately from physical memory.

## Documentation

- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model, solid primitives, field evaluator, and Lipschitz analysis.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SignedDistance.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
