# Puck.SignedDistance

Puck.SignedDistance provides the signed-distance-function instruction ISA,
packed program representation, authoring builder, deterministic CPU evaluator,
and the baker that samples a program through that evaluator into a mesh,
textures, and an impostor.
The evaluators expose structural line-of-sight work envelopes derived from their
compiled programs and fixed march budgets. These counts carry no cycle price.

`Puck.SignedDistance.Illumination` holds the radiance cache's CPU reference: an
irradiance estimator over the evaluator, and a model of the cache's transport
and schedule that the GPU cache is held to. The reference attributes first-hit
analytic light, emission, sky and screens independently from later feedback.
`EstimateIncidentSources` fixes the first ray to a captured origin and direction,
then uses the same finite-bounce path fold and independent Halton reflections.
It accepts one through 256 paths and zero through nine reflections; the ordinary
hemisphere estimator retains its larger sampling counts. This reference reads
the field directly and is independent of the renderer's twelve-query Near budget.
A required launch that fails is unresolved. Exactly zero diffuse reflectance
skips direct and screen queries and later reflections that cannot contribute;
any nonzero component retains those queries and the requested depth. Point-specific reflectance can attenuate reflected
light without attenuating self-emission. SdfProgram.Materials exposes the same
snapshotted palette the packed shader reads, including immutable nested lists.

`SdfProgram.InspectInstance` reads exclusive packed ownership and culling facts for a
live instance. Its count includes instructions and their data/bounds, owned segment
and rigid-leaf rows, shape side tables and part bindings. Shared tables stay in the
program-level remainder; inspection never re-emits a prototype.
`BuildDynamicTransformBounds` collects the existing packed bounds by dynamic
slot for presentation-cache invalidation. It reuses finite instance certificates
for unskippable chains and reports an infinite bound for an unsupported dynamic
dependency; an unrelated static plane affects no dynamic slot.

## Documentation

- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model, solid primitives, field evaluator, and Lipschitz analysis.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SignedDistance.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
