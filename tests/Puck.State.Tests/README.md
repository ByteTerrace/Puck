# Puck.State.Tests

This suite exercises deterministic state documents in `Puck.State`. Its law
cases cover expression spelling, functions and constant folding, cost and
reduction bounds, graph and hex topology, live zones, row-version scheduling,
write sets, the arena and its journal scopes, `CellValue`'s kind-admission
refusal, and the reference schedule's evidence manifest — its pinned targets,
instruction service, kernel prices, memory profile, and the coverage of both
registered vocabularies.

## Running

```powershell
dotnet test tests/Puck.State.Tests/Puck.State.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
