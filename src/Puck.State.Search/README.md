# Puck.State.Search

Puck.State.Search runs game-tree search over a state arena. It enumerates
candidate moves, judges each one with compiled rules inside a journal scope,
and compares futures with negamax or tree search, spreading the work across
ticks.

## Documentation

- [Search](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/search.md) — jobs, candidate shapes, scoring, chance, and work limits.
- [State and rules overview](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the model a search judges over.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Search.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
