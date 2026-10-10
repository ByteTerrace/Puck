# Puck.Cli

Puck.Cli provides the `puck` developer command line: project compilation,
code formatting, asset synchronization, verification, and packaging tools.
It is the root that composes one command tree from the verb assemblies
(`PuckRootCommand`): `Puck.Cli.Core`, `Puck.Cli.Harness`, `Puck.Cli.Source`,
`Puck.Cli.Format`, `Puck.Cli.Shaders`, `Puck.Cli.Worlds`, `Puck.Cli.Gate`,
`Puck.Cli.Laws`, `Puck.Cli.Worktrees`, `Puck.Cli.Runs`, `Puck.Cli.Bench`,
`Puck.Cli.Release` and `Puck.Cli.Content`. Each references only what its own
verbs need, and the package carries them all.

## Documentation

- [Puck CLI](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/cli.md) — commands, options, workflows, and tool execution.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Cli.Tests/README.md) — the composed tool's suite and each verb assembly's own.
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
