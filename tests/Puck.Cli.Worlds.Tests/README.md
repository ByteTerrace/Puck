# Puck.Cli.Worlds.Tests

These laws hold the world-authoring verbs. `ShippedSourceLintLawTests` runs `puck lint --strict` over every tracked
source under `worlds`, `src/Puck.World/Assets` and `tests/Puck.World.Verdicts`, so an error or warning in a
shipped world or cartridge fails the suite with the report the verb printed. `FormatProjectionLawTests` holds every
tracked source outside `experimental` to what `puck format` prints, and holds that formatting leaves its compiled
document unchanged.

`CompileBatchTests` verifies ordered compilation, independent source bindings, byte parity with single-source
compilation, stopping on failure, and refusal of batch requests with a shared output path or watch mode.
`CompositionCompileCommandTests` verifies that one source can publish several named world documents, that a refused
member publishes none of them, and that `--output` names their directory. It also covers explicit asset-lock updates,
the semantic-validation gate on an update, stale-byte refusal without output replacement, and a document written away
from its source naming its asset and graph files from where it lands.

`TestCommandLawTests`, the schedule laws and `CompositionBootLawTests` boot the real `Puck.World` executable out
of its own Release output, so the suite builds that project first; nothing here links against it.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Worlds.Tests/Puck.Cli.Worlds.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Worlds](../../src/Puck.Cli.Worlds/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
