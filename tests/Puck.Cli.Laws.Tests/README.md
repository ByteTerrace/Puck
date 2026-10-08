# Puck.Cli.Laws.Tests

These laws hold `puck laws prove`: a proof withholds its fix in a persistent clone of its own, proves the law red and
green, refuses a failed build, a skipped test and inconsistent legs, and leaves the caller's checkout as it found it.
`LawBuildLawTests` runs real incremental builds of a small project, and `ReferenceAssemblyRecoveryLawTests` holds the
corrupt-reference recovery the project build and the proof runner share. A proof-tree law builds shaders in the clone
through `ShaderBuildFixture` and the shipped shader targets, so the suite builds `Puck.Shaders.Generator` first.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Laws.Tests/Puck.Cli.Laws.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Laws](../../src/Puck.Cli.Laws/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
