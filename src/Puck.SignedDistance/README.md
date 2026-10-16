# Puck.SignedDistance

Puck.SignedDistance provides the signed-distance-function instruction ISA,
packed program representation, authoring builder, deterministic CPU evaluator,
and the baker that samples a program through that evaluator into a mesh,
textures, and an impostor.
The evaluators expose structural line-of-sight work envelopes derived from their
compiled programs and fixed march budgets. These counts carry no cycle price.

`Puck.SignedDistance.Illumination` holds the indirect-light controls the frame
carries (`SdfIndirectTier`, `SdfIndirectSources`, `SdfIndirectGains`) and the
octahedral direction mapping the sky environment map uses (`SdfOctahedral`).
`SdfProgram.Materials` exposes the same snapshotted palette the packed shader
reads, including immutable nested lists.

`SdfProgram.InspectInstance` reads exclusive packed ownership and culling facts for a
live instance. Its count includes instructions and their data/bounds, owned segment,
rigid-leaf and chain-table rows, shape side tables and part bindings. Shared tables stay in the
program-level remainder; inspection never re-emits a prototype.

`SdfProgram.ChainsOf` and `Specialization` read the program's chain table: each
directory segment's chains (`SdfChain`), keyed by op and the header lanes that
select code (`SdfOpRoles.CodeLanesOf`), and the set of distinct chains, shape
types, blends and compose forms (`SdfSpecializationSet`), which no edit to data
changes. Two distinct chains of one program that share a 32-bit identity are refused
by name. SDF program specialization, which would have compiled a world's field
code from that set, is [withdrawn](https://github.com/ByteTerrace/Puck/blob/main/docs/decisions/rendering.md#the-frame-does-not-interpret-sdf-programs).
`BuildDynamicTransformBounds` collects the existing packed bounds by dynamic
slot for presentation-cache invalidation. It reuses finite instance certificates
for unskippable chains and reports an infinite bound for an unsupported dynamic
dependency; an unrelated static plane affects no dynamic slot.

## Documentation

- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model, solid primitives, field evaluator, and Lipschitz analysis.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SignedDistance.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
