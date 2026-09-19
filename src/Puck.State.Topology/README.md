# Puck.State.Topology

Puck.State.Topology holds the board-query and pattern layer over a
`Puck.State` arena: `BoardQuery`/`BoardQueries`/`BoardCombination` read a ray
or a board shape off arena columns through one span kernel, `CompiledPattern`/
`PatternRow`/`PatternMemo` walk a compiled automaton over an arena-read word
with incremental resume, and `ArenaBoards` is the arena-facing door both run
through. Every entry point addresses a row by catalog ordinal, so a query or a
pattern walk answers the same over an arena as over a document row.

The compiled lattice topology itself — `LatticeTopology`, `TopologyCompilation`,
`CompiledTopology`, `TilingGenerator` — stays in `Puck.State`: the arena's own
layout compiler resolves a document row's declared lattice into a
`CompiledTopology` while it lays out `StateArena`'s columns, so the compiled
topology has to be visible from core. `ArenaWord`/`WordSource`, the pair a
pattern word read carries its own identity in, stay there for the same
reason — `StateArena`'s own word reads mint them, and a partial class cannot
split across assemblies. `ArenaBoards.TryReadRay`, in this project, mints one
too; its constructor is `public` rather than `internal` so a reader outside
`Puck.State` can call it (the accessibility ruling: widen the member, not the
assembly).

## Documentation

- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the row, cell, and rule model these reads run against.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Topology.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
