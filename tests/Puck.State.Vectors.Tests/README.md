# Puck.State.Vectors.Tests

This suite exercises the arena vector transforms and the typed column view in
`Puck.State.Vectors`. Its law cases cover what `mix` and `mean` write against
the signed-byte kernels' own answer, how `nearest` ranks a keyed vector table
into each destination kind its shape admits, when `remember` stores a vector
and when a near duplicate makes it skip, the catalogued code every refusal
reports, and the journal scope that carries a refused transform's partial
writes back.

## Running

```powershell
dotnet test tests/Puck.State.Vectors.Tests/Puck.State.Vectors.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
