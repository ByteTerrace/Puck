# Puck.SignedDistance

Puck.SignedDistance provides the signed-distance-function instruction ISA,
packed program representation, authoring builder, deterministic CPU evaluator,
and the baker that samples a program through that evaluator into a mesh,
textures, and an impostor.
The evaluators expose structural line-of-sight work envelopes derived from their
compiled programs and fixed march budgets. These counts carry no cycle price.

`SdfProgram.InspectInstance` reads exclusive packed ownership and culling facts for a
live instance. Its count includes instructions and their data/bounds, owned segment
and rigid-leaf rows, shape side tables and part bindings. Shared tables stay in the
program-level remainder; inspection never re-emits a prototype.

`SdfSkyRuns` partitions an already resolved active layer stack into consecutive
field, point and screen runs without reordering it. `SdfSkyAffine` composes the
per-channel scale and offset of a field run. These CPU primitives prepare the
ordered sky renderer; runtime layer-table and pass integration remain in P18-8.
Their image and texel counts are structural gauges for one run refresh, not
cumulative work counters.

## Documentation

- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model, solid primitives, field evaluator, and Lipschitz analysis.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SignedDistance.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
