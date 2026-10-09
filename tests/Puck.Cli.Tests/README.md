# Puck.Cli.Tests

This suite tests the composed `puck` tool, [Puck.Cli](../../src/Puck.Cli/README.md):
the laws about how the verb assemblies fit together, and the repository-wide
convention laws. Each verb assembly's own laws live in its own suite, which
references that assembly alone, so a change reaches the laws of the verbs it can
change:

| Suite | Verb assembly |
|---|---|
| [Puck.Cli.Core.Tests](../Puck.Cli.Core.Tests/README.md) | [Puck.Cli.Core](../../src/Puck.Cli.Core/README.md) |
| [Puck.Cli.Harness.Tests](../Puck.Cli.Harness.Tests/README.md) | [Puck.Cli.Harness](../../src/Puck.Cli.Harness/README.md) |
| [Puck.Cli.Source.Tests](../Puck.Cli.Source.Tests/README.md) | [Puck.Cli.Source](../../src/Puck.Cli.Source/README.md) |
| [Puck.Cli.Format.Tests](../Puck.Cli.Format.Tests/README.md) | [Puck.Cli.Format](../../src/Puck.Cli.Format/README.md) |
| [Puck.Cli.Shaders.Tests](../Puck.Cli.Shaders.Tests/README.md) | [Puck.Cli.Shaders](../../src/Puck.Cli.Shaders/README.md) |
| [Puck.Cli.Worlds.Tests](../Puck.Cli.Worlds.Tests/README.md) | [Puck.Cli.Worlds](../../src/Puck.Cli.Worlds/README.md) |
| [Puck.Cli.Gate.Tests](../Puck.Cli.Gate.Tests/README.md) | [Puck.Cli.Gate](../../src/Puck.Cli.Gate/README.md) |
| [Puck.Cli.Laws.Tests](../Puck.Cli.Laws.Tests/README.md) | [Puck.Cli.Laws](../../src/Puck.Cli.Laws/README.md) |
| [Puck.Cli.Worktrees.Tests](../Puck.Cli.Worktrees.Tests/README.md) | [Puck.Cli.Worktrees](../../src/Puck.Cli.Worktrees/README.md) |
| [Puck.Cli.Runs.Tests](../Puck.Cli.Runs.Tests/README.md) | [Puck.Cli.Runs](../../src/Puck.Cli.Runs/README.md) |
| [Puck.Cli.Bench.Tests](../Puck.Cli.Bench.Tests/README.md) | [Puck.Cli.Bench](../../src/Puck.Cli.Bench/README.md) |
| [Puck.Cli.Release.Tests](../Puck.Cli.Release.Tests/README.md) | [Puck.Cli.Release](../../src/Puck.Cli.Release/README.md) |
| [Puck.Cli.Content.Tests](../Puck.Cli.Content.Tests/README.md) | [Puck.Cli.Content](../../src/Puck.Cli.Content/README.md) |

The suites share their fixtures from [`tests/Shared/Cli`](../Shared/Cli/README.md).

## What this suite holds

`CliConventionLawTests` holds every verb of the composed tree to the CLI
conventions, with its exemptions in `CliConventionExemptions.json`, and checks
that help loads no compiler, build engine, benchmark or GPU assembly.
`SchemaBootstrapLawTests` holds the schema bootstrap's root, and
`LedgerDriftTests` holds the architecture, registry and schema ledgers to what
their verbs generate.

The composition laws hold the contracts between verb assemblies: every command
`puck affected` and `puck gate` print for another verb parses through the root
(`AffectedSelectionLawTests`, `GatePlanLawTests`, `AffectedManifestCheckLawTests`);
the composed grammars decide which running processes are GPU work
(`HostGpuWorkLawTests`); a recorded counters workload becomes one gate step
(`CountersGateStepLawTests`); generated schema files stand for the sources of
their types (`AffectedStandInsLawTests`); and an ordinary verb refuses a
misspelled option beside one that forwards its tokens (`MisspelledOptionLawTests`).
`McpInteropTests` and `WorldReleaseRefusalLawTests` run the real `puck` process.

The repository-wide laws read the whole tree: `JsonNewlineSpellingLawTests`,
`MsBuildPathSpellingLawTests`, `LockFileOwnershipLawTests`,
`SchemaTokenOwnershipLawTests` and `SourceRevisionLawTests`.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test --project tests/Puck.Cli.Tests/Puck.Cli.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

A successful run reports passing tests and exits with code zero. Use the
framework's test filter to narrow an investigation. The
[CLI reference](../../docs/reference/cli.md) owns command syntax and
operational prerequisites.

## Documentation

📚 [Puck.Cli](../../src/Puck.Cli/README.md) · 🛠️ [Development](../../docs/development/README.md)
