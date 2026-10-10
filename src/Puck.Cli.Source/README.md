# Puck.Cli.Source

Puck.Cli.Source holds the source and repository verbs: `search`, `scan`, `references`, `declarations`, `derivations`, `architecture`, the ratchet ledgers (`lengths`, `comment-smells`) and the `docs` family. They read the tree through Roslyn and the analyzers, never through the engine.

## Documentation

- [`puck search`](../../docs/reference/cli.md#puck-searchcontent-search)
- [`puck references`](../../docs/reference/cli.md#puck-referencessemantic-symbol-queries) and [`puck declarations`](../../docs/reference/cli.md#puck-declarationsdeclaration-inventory)
- [The ratchet ledgers](../../docs/reference/cli.md#puck-lengths-and-puck-comment-smellsratchet-ledgers)
- [`puck docs`](../../docs/reference/cli.md#puck-docsthe-documentation-family)
- [Puck.Cli.Source.Tests](../../tests/Puck.Cli.Source.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
