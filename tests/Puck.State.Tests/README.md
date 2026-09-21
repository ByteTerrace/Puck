# Puck.State.Tests

This suite exercises deterministic state documents in `Puck.State`. Its law
cases cover expression spelling, functions and constant folding, cost and
reduction bounds, graph and hex topology, live zones, row-version scheduling,
write sets, the arena and its journal scopes, `CellValue`'s kind-admission
refusal, and the reference schedule's evidence manifest — its pinned targets,
instruction service, kernel prices, memory profile, and the coverage of both
registered vocabularies. Cell-set algebra is checked against a scalar oracle
across the 256-cell inline boundary through 19×19 and 33×18 boards.

The pool laws cover typed defaults, generation-checked lifetimes, strict
snapshots, protected generated rows, pair identity and cascades, acyclic nested
pairs, relayout continuation, generation exhaustion, and warmed allocation-free
claim/write/release/rewind paths. Fixed-slot laws cover sparse ordinary and pair
identities across occupancy-word boundaries, journal cost independent of live
count, hole-aware reads, relayout, pending-turn persistence, and refusal of undo
entries that place a member at another identity slot.

## Running

```powershell
dotnet test tests/Puck.State.Tests/Puck.State.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
