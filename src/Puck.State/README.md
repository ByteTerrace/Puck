# Puck.State

Puck.State is the core of Puck's state system: the data model (rows, cells,
records, and pools), the catalog and the columnar state arena with its journal
scopes, compiled topologies, the expression language, and the authored rule
model. An application supplies the host that runs rules and persists state.

## Documentation

- [State and rules overview](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — what the state system does and how its projects fit together.
- [Quickstart: compile and run a rule](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/quickstart.md) — a ten-minute tour in a console project.
- [Rows, cells, and values](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/data-model.md) — the data model this project defines.
- [The state arena](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/arena.md) — storage, journal scopes, hashing, and persistence.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
