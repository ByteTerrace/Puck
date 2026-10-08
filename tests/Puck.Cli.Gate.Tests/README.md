# Puck.Cli.Gate.Tests

These laws hold `puck affected`, `puck gate`, `puck host` and `puck baselines`: selection over memory trees and
scratch checkouts, non-executing edits, recording, revision export, suite scheduling, the gate's batch and step order,
host admission and load lines. `RunDirectoryLawTests` holds the one run-directory policy: a passing run leaves no
directory, a failing or unfinished run keeps its own and names its absolute path, a directory that holds no evidence is
deleted whatever the verdict, and the age sweep removes only stale directories of its own prefix. It also checks that a
recording keeps its inner canary transcript when that run fails, and that a law's `TemporaryDirectory` outlives its
disposal until the law's verdict.

The suite composes its verbs over a `GateComposition` that names no other verb (`SuiteRoot`). The laws that need
the verbs composed beside the gate (the commands it prints for other verbs, the GPU-work grammars, the schema stand-ins)
live in [`Puck.Cli.Tests`](../Puck.Cli.Tests/README.md).

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Gate.Tests/Puck.Cli.Gate.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Gate](../../src/Puck.Cli.Gate/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
