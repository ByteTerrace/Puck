# Puck.Cli.Core.Tests

These laws hold the plumbing every `puck` verb shares: process runs and their deadlines on a virtual clock, git runs,
path display, the project build, scratch directories and a root's invocation. `CliProcessHandshakeTests` checks
that output can release a final stdin command without closing input early, and that early exit and timeout remain
bounded. `ProcessTerminationTests` checks that an interrupted verb runs its cleanup before its invocation returns.
The suite references `Puck.Cli.Core` alone.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Core.Tests/Puck.Cli.Core.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Core](../../src/Puck.Cli.Core/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
