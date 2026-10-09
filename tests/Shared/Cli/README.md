# CLI test fixtures

These are the fixtures the `puck` suites share, in the `Puck.Cli.Testing`
namespace. They are linked into each suite as source rather than built as a
library, so a suite compiles only the fixtures its laws use and references only
the verb assembly it tests. A change to one of these files selects exactly the
suites that link it ([`puck affected`](../../../docs/reference/cli.md#puck-affectedthe-checks-a-change-needs)).

| Fixture | What it gives a law |
|---|---|
| `ConsoleCapture.cs` | A verb's standard output and error, captured per async flow so concurrent laws never see each other's output. |
| `GitScratchCheckout.cs` | A small git checkout the law owns, with automatic maintenance off. |
| `ThreadPoolFloor.cs` | A raised thread-pool minimum, so laws that block on child processes never starve the in-process servers beside them. |
| `SchedulingCohorts.cs` | Cohorts of workers held at barriers, so a scheduling law counts admissions rather than timing them. |
| `LedgerMergeProbe.cs` | A three-way `git merge-file` of two ledger versions over their ancestor, so a law shows concurrent changes collide without a repository. |
| `TrackedPuckSources.cs` | Every tracked `.puck` source outside `experimental/`, as theory data. |
| `ScheduledWorldBoot.cs` | One authored test world booted headless through the real `Puck.World` executable with a piped script. |
| `AffectedMemoryTree.cs` | An in-memory tree for `puck affected`'s document and stand-in laws. |
| `ShaderBuildFixture.cs` | An isolated project over the shipped shader build targets, with a CPU-only stand-in for DXC that records, holds or fails each compile. |

Each suite composes the verbs its laws invoke in its own `SuiteRoot`, the way the
`puck` root composes them. The laws about how the verb assemblies fit together
live in [`Puck.Cli.Tests`](../../Puck.Cli.Tests/README.md).

## Documentation

📚 [The puck command line](../../../docs/reference/cli.md) · 🛠️ [Contributing to Puck](../../../docs/development/contributing.md)
