# Puck.Cli.Worktrees.Tests

These laws hold `puck worktree-report`: refs, worktree listings, status and git files survive the report; patch-equivalent
cherry-picks and merged history count as landed; one unlanded commit blocks removal.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Worktrees.Tests/Puck.Cli.Worktrees.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Worktrees](../../src/Puck.Cli.Worktrees/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
