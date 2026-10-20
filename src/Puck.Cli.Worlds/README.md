# Puck.Cli.Worlds

Puck.Cli.Worlds holds the world-authoring verbs: `compile`, `decompile`, `lint`, `lsp`, `migrate`, `embed`, `test`, `creation`, `registry` and `vocabulary`, with the machine vocabulary they compose (`CliWorldVocabulary`). It is also an executable of its own, which `puck parity` runs to compile its tree in a process of its own.

## Documentation

- [The `.puck` DSL verbs](../../docs/reference/cli.md#the-puck-dsl-verbs)
- [`puck test`](../../docs/reference/cli.md#puck-testtest-worlds)
- [Generated assets](../../build/README.md) — the shipped worlds' build.
- [Puck.Cli.Worlds.Tests](../../tests/Puck.Cli.Worlds.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
