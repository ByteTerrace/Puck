# Puck.State.Search.Tests

This suite exercises the arena search in `Puck.State.Search`. Its law cases
cover what a candidate scope does to the arena, what a job lands after a
suspension and a checkpoint round trip, how a chance node averages and how a
tree job draws, how a per-seat max-n level folds a seat vector over scopes,
which rows a position key reaches, and the admission and arm-queueing contract
of a judge that runs real compiled rules.

Every case runs one authored position and one authored rule set through the
arena search and reads the writes it lands. An average, a max-n choice or a
best move is compared against the value hand-computed beside the case, never
against a second implementation.

## Running

```powershell
dotnet test tests/Puck.State.Search.Tests/Puck.State.Search.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
