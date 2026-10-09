# Puck.Cli.Harness.Tests

These laws hold what describes and stages a World run: the canary response lines, relations and line order, the
refusal census, and the World artifact's closure. `WorldArtifactClosureLawTests` evaluates the World's project graph
with MSBuild and requires every input it names to lie under a keyed path. The suite references `Puck.Cli.Harness`
alone.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Harness.Tests/Puck.Cli.Harness.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Harness](../../src/Puck.Cli.Harness/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
