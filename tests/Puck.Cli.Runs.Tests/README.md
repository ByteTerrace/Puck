# Puck.Cli.Runs.Tests

These laws hold the verbs that boot a World and judge it: canary accounting, plans, legs and manifests, counters and
their ceilings, parity comparison, determinism, qualification verdicts, release profiles and the World artifact build.

`CountersDetailLawTests` holds detail identity through readings and report comparisons, including skipped rows,
device-aware ceilings and required zeros. Reports require explicit detail keys and pass detail labels. Ceilings retain
those identities in compact measurement layouts. `CountersCeilingsCompactLawTests` compares every shipped ledger
against fixed reports and exact checker messages in `Assets/counters-ceilings-equivalence.zip`, including missing and
unexpected zero rows, changed classes, exceeded budgets, and device and driver changes. It also holds the writer's fixed
point, kind defaults and device differences.

`WorldArtifactBuildLawTests` count builds of the stored `Puck.World` artifact over small git checkouts of their own,
with a counting builder in place of `dotnet build`. Two resolutions of an unchanged tree build once, and concurrent
resolutions of one source state share one build. A one-byte uncommitted change under the World's closure produces a
new key, while documentation and unreferenced projects do not. A publish that loses the race keeps the winner's build.
Pruning keeps the most recently used builds and every leased one, and a killed run's leftover directories and lock
files are removed only after six hours. `CanaryListenerLawTests` checks that the canary port probe hands out UDP
ports, and that a World refusing its listener is classified as an infrastructure failure rather than unsupported.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Runs.Tests/Puck.Cli.Runs.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Runs](../../src/Puck.Cli.Runs/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
