# Puck.World.Vocabulary

Puck.World.Vocabulary holds the vocabulary a world document is validated
against, below [`Puck.World.Client`](../Puck.World.Client/README.md):

- `WorldAffordances` checks a binding document's commands, channels and
  context rows. The composition root installs the live command registry once
  its container exists; until then the command half is skipped and the rest
  still runs.
- `WorldContextFamilies` names the built-in per-seat context families and
  their states.
- `WorldSchemaVocabularyHooks.Install` is the one wiring of
  [`Puck.World.Schema`](../Puck.World.Schema/README.md)'s injection seams.
  `Puck.World`, `Puck.World.Silo`, `Puck.Cli.Worlds` and the test suites all
  call it, so no process wires a seam another leaves open.

It references `Puck.Input` (the physical source vocabulary), `Puck.Shaders`
(the render graph package catalog a `views.post` row is checked against) and
`Puck.World.Protocol` (the mutation-kind catalog). Its lane profile in
`build/Architecture.props` keeps out `Puck.World.Client` and
`Puck.World.Server`.

The world compile the game's build runs (`Puck.Cli.Worlds`, see
`build/WorldAssets.targets`) validates through this project and never loads
the client, so an edit to `Puck.World.Client` does not rerun it
([edit to running](../../docs/development/contributing.md#edit-to-running)).
