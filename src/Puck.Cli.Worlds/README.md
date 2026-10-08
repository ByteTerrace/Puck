# Puck.Cli.Worlds

Puck.Cli.Worlds holds the world-authoring verbs: `compile`, `decompile`, `lint`, `lsp`, `migrate`, `embed`, `test`, `creation`, `registry` and `vocabulary`, with the machine vocabulary they compose (`CliWorldVocabulary`). It is also an executable of its own: the game build runs it to compile the shipped worlds (`build/WorldAssets.targets`), so a change to another verb assembly never reruns that compile or changes the World artifact's key.

## Documentation

- [The `.puck` DSL verbs](../../docs/reference/cli.md#the-puck-dsl-verbs)
- [`puck test`](../../docs/reference/cli.md#puck-testtest-worlds)
- [Generated assets](../../build/README.md) — the shipped worlds' build.
- [Puck.Cli.Worlds.Tests](../../tests/Puck.Cli.Worlds.Tests/README.md) — the laws that hold this assembly's verbs.
- [Puck.Cli](../Puck.Cli/README.md) — the `puck` root that composes every verb assembly.
