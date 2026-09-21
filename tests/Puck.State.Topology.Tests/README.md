# Puck.State.Topology.Tests

This suite exercises the board-query and pattern layer in
`Puck.State.Topology`: what a board query and a ray read answer against a
lattice arena, the arena word a row or ray read mints and the source identity
it carries, and how a pattern's incremental walk resumes across a later start,
a ray direction, and an attribute row. Component and enclosure laws include a
full 19×19 board.

## Running

```powershell
dotnet test tests/Puck.State.Topology.Tests/Puck.State.Topology.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
