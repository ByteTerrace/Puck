# Puck.State.Generators

Puck.State.Generators holds the authored-randomness engine of a `Puck.State`
arena: `GeneratorEngine` draws a site's next value from the Markov,
weighted-numeric, symmetry-orbit, and uniform-range source families it and
`PrivateDraw` share, `TableDocument`/`CompiledTable` carry a generator's
declared table data, and `ArenaDraws` is the arena-facing door a draw site's
cursor and drawn masks read and write through. `PenrosePatch`'s seeded patch
draw lives here too — it needs both `TilingGenerator` (from `Puck.State`) and
`GeneratorEngine`'s seed ladder, and only this project can see both.

The authored `Draw` facet and `StateGenerator` declaration stay in
`Puck.State`: `StateRow.Draw` types on `Draw` directly, and `Draw.Generator`
types on `StateGenerator`, so the document row schema needs them visible from
core. `StateGenerator.Exhausts`/`MaskCount` stay beside that declaration for
the same reason — the arena's own layout compiler sizes a draw site's mask
words from them while it lays out `StateArena`'s columns, before a document's
generator ever reaches this project.

## Documentation

- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the row, cell, and rule model these draws run against.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Generators.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
