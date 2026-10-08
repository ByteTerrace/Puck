# Puck.Cli.Bench.Tests

These laws hold `puck bench`. `StartupBenchmarkTests` checks that incomplete or failed samples cannot produce a
corpus average, pending or unrelated captures cannot prove rendered readiness, missing overlays fail rendered samples,
and process output carries elapsed observation times. Actual startup performance requires running
`puck bench startup` against the built World executable. `StateEvidenceCommandTests`, `StateKernelMatrixLawTests`
and `ReferenceWalkerLawTests` hold the State evidence inventory and the reference capture.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Bench.Tests/Puck.Cli.Bench.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Bench](../../src/Puck.Cli.Bench/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
