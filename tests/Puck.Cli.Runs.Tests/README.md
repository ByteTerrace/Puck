# Puck.Cli.Runs.Tests

These laws hold the verbs that boot a World and judge it: canary accounting, plans, legs and manifests, counters
reports, parity comparison, determinism, qualification verdicts, release profiles and the World artifact build.

`CountersDetailLawTests` holds detail identity through readings and report comparisons, including skipped rows.
Reports require explicit detail keys and pass detail labels.

Quarantined: the laws that read the canary corpus, the parity documents, the determinism manifest, the release
profile or the verdict worlds (`DeterminismLawTests`, most of `ParityBindingReferenceLawTests`, and the shipped-corpus
laws of the canary, determinism-attestation and release-profile classes) live in
`experimental/tests/Puck.Cli.Runs.Tests/` beside those files.

`MotionClearanceLawTests` resolve the debug world's own motion cameras on the CPU and hold the studio's open floor clear
and a camera in a wall or behind a pole named. `MotionLawTests`, `MotionCollectorLawTests` and `MotionCommandLawTests` hold `puck motion`'s CPU parts: the scenario
file's strict parse and the shipped file's validity, the plan's ticks and the order of its lines, the sheet layout, the
transcript parsers, what the collector makes of a World's lines (each failure named) and the manifest and reproduce
reports. The verb's World run needs a GPU (`build/CoverageHardwareOnly.txt`).

`WorldArtifactBuildLawTests` count builds of the stored `Puck.World` artifact over small git checkouts of their own,
with a counting builder in place of `dotnet build`. Two resolutions of an unchanged tree build once, and concurrent
resolutions of one source state share one build. A one-byte uncommitted change under the World's closure produces a
new key, while documentation and unreferenced projects do not. A publish that loses the race keeps the winner's build.
Pruning keeps the most recently used builds and every leased one, and a killed run's leftover directories and lock
files are removed only after six hours. `CanaryListenerLawTests` checks that the canary port probe hands out UDP
ports. Quarantined: its law that a World refusing its listener is classified as an infrastructure failure rather than
unsupported boots a verdict world, and lives in `experimental/tests/Puck.Cli.Runs.Tests/CanaryListenerLawTests.cs`.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Runs.Tests/Puck.Cli.Runs.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Runs](../../src/Puck.Cli.Runs/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
