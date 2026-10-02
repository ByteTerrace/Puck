# Puck.SignedDistance

Puck.SignedDistance provides the signed-distance-function instruction ISA,
packed program representation, authoring builder, deterministic CPU evaluator,
and the baker that samples a program through that evaluator into a mesh,
textures, and an impostor.
The evaluators expose structural line-of-sight work envelopes derived from their
compiled programs and fixed march budgets. These counts carry no cycle price.

`Puck.SignedDistance.Illumination` holds the radiance cache's CPU reference: an
irradiance estimator over the evaluator, and a model of the cache's transport
and schedule that the GPU cache is held to.

`SdfProgram.InspectInstance` reads exclusive packed ownership and culling facts for a
live instance. Its count includes instructions and their data/bounds, owned segment
and rigid-leaf rows, shape side tables and part bindings. Shared tables stay in the
program-level remainder; inspection never re-emits a prototype.

## Documentation

- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model, solid primitives, field evaluator, and Lipschitz analysis.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SignedDistance.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
