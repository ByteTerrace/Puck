# Puck.Cli.Gate.Tests

These laws hold `puck affected`, `puck gate`, `puck host`, `puck baselines` and
`puck locks`: selection over memory trees and scratch checkouts, the `.github/`
readers each declaring that tree, non-executing edits, recording, revision export, suite scheduling, the gate's
batch and step order, host admission and load lines, and the locked restore
against a real scratch solution (`GateLockFileLawTests`,
`LockFileRecordLawTests`). It also checks that a recording keeps its inner
canary transcript when that run fails, and that a law's `TemporaryDirectory`
outlives its disposal until the law's verdict.

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
