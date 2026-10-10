# Puck.Cli.Core

Puck.Cli.Core holds the plumbing every `puck` verb shares: exit codes and their guard, process and git runs, path display, help, scratch and tree-file safety, the project build every verb that builds goes through, the repository project graph (`ArchitectureModel`), and `CliRoot`, the one invocation a root composes its verbs through. It references no verb and no engine runtime, so every other verb assembly can depend on it.

## Documentation

- [Conventions](../../docs/reference/cli.md#conventions) — exit codes, paths and option rules every verb follows.
- [Puck.Cli.Core.Tests](../../tests/Puck.Cli.Core.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
