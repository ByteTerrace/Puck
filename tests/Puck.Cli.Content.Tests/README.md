# Puck.Cli.Content.Tests

These laws hold the checked-in content generators: branding asset synchronization and `puck firmware`.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Content.Tests/Puck.Cli.Content.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Content](../../src/Puck.Cli.Content/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
