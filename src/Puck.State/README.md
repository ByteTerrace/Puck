# Puck.State

Puck.State provides named state rows, compiled rules, and hypothetical evaluation
for deterministic simulations. The application supplies the host that applies
mutations and persists state.

Records declare typed fields and defaults. Pools instantiate one record into a
bounded set of generation-checked handles; pair pools identify bounded relations
between live handles. The catalog expands them into protected rows for live
membership, persistent generations, and fields. Claims choose deterministic
slots, releases invalidate stale handles, and releasing an endpoint atomically
releases every dependent pair through the arena journal.
Runtime handles are bound to the catalog that minted them. Arenas sharing that
catalog may exchange handles; a relayout with a replacement catalog requires
handles to be resolved again. Persisted references store pool name, slot, and
generation; the loader resolves the pool name and asks the current catalog to
mint the runtime handle.

Undo-enabled rule groups configure a bounded retained journal in the arena. One
segment spans the group's whole logical run even when it crosses ticks; ordinary
firing scopes still close within their tick. Checkpoints carry both closed
segments and a pending segment, relayout clears them, and later writes to a
retained row make the older segment unrewindable rather than overwriting newer
state. Pool selectors close over membership, generations, fields, and dependent
pair pools. The shared key ledger keeps its arena-lifetime reservations when a
turn rewinds.

Pool identity capacity and live capacity are separate limits. An ordinary pool's
identity universe is its declared capacity. A pair pool's universe is the product
of its endpoint capacities and must fit one row; `MaxLive` limits how many of those
identities may be present. Pair endpoints may themselves be pair pools when the
dependency graph is acyclic. Snapshots carry every slot generation and every
field of every live instance. `ToPools()` and `ToPairPools()` export those two
authored declaration families without exposing their generated rows.

Pool rows reserve one fixed position per identity slot and mark live cells with
presence bits. Claim, release, and rewind touch the selected instance's fields
without shifting other cells or rebuilding key indexes. Handle reads check the
live generation and address the field directly. Free-slot selection and snapshots
scan occupancy words; cascading release still visits dependent pairs. `CellCount`
reports how many cells a row holds. `TryNextCell` visits those held cells in
ascending physical-slot order without exposing pool capacity or holes. Positional
reads are for authored ordered, keyed, slot, lattice, and ring rows; generated pool
storage is handle- and cursor-addressed. See [the storage contract](../../docs/plans/records-and-pools.md).

Ring cursors visit physical slots; `ReadWord` supplies chronological history.
Ring slot names are compiled addresses, not retained key reservations. Exporting
and rebuilding a ring preserves its key ledger and state hash.

## Documentation

- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — start with a small rule, then explore the state model and host contracts.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
