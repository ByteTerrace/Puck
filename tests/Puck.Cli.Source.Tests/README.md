# Puck.Cli.Source.Tests

These laws hold the source and repository verbs: derivation keys, `docs links` and `docs citations`, the
ratchet ledgers' canonical form, and `references` over a project with an unresolved analyzer. The suite references
`Puck.Cli.Source` alone.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Source.Tests/Puck.Cli.Source.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Source](../../src/Puck.Cli.Source/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
