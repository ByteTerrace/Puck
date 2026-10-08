# Puck.Cli.Format.Tests

These laws hold `puck format` (selection, whitespace scope, trailing commas, named arguments and their closure
deadline, static initialization order, `.puck` sources), `puck formats` and its ledger, and the pull-request
formatter's submission policy (`FormatSubmissionTests`). The suite references `Puck.Cli.Format` alone; the law that
formatting every tracked `.puck` source leaves its compiled document unchanged needs the compiler and lives in
[`Puck.Cli.Worlds.Tests`](../Puck.Cli.Worlds.Tests/README.md). A format's shape compiles the sources against the
assemblies the computing process trusts, and the puck tool records the ledger, so the laws that hold the shipped ledger
to the shipped source run in [`Puck.Cli.Tests`](../Puck.Cli.Tests/README.md), whose host loads the tool's assemblies.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Format.Tests/Puck.Cli.Format.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Format](../../src/Puck.Cli.Format/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
