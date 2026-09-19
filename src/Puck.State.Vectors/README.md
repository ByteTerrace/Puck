# Puck.State.Vectors

Puck.State.Vectors holds the vector half of `Puck.State`: `VectorColumn`, the
typed view over one row's vector column beside `CellValue.Vector`'s opaque
payload, and `ArenaVectorTransforms`, the `mix`, `mean`, `nearest`, and
`remember` transforms over a `StateArena`. Because the transforms run against
the arena, they run wherever it runs.

Three properties hold for all four transforms:

- **One journal scope.** A transform opens a scope before its first write and
  rewinds it on any later refusal, so a refused transform leaves the arena
  exactly as it found it.
- **One admission door.** Every vector a transform writes is admitted through
  `StateVector.TryCreate` first, and every score decides through its
  destination row's own envelope.
- **One source of arithmetic.** Scoring, normalization, and ranking are
  `Puck.State.VectorTransforms` over `Puck.Maths`' signed-byte kernels; this
  project addresses rows, walks members, and journals writes.

## Documentation

- [Embedding spaces and vectors](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/data-model.md#embedding-spaces-and-vectors) — how a vector row is declared and what it can carry.
- [Vector transforms](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/rules.md#vector-transforms) — the authored spelling of the four transforms.
- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the row, cell, and rule model these transforms write into.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Vectors.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
