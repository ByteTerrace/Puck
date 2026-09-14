# Authoring content

Choose the kind of content you want to change. Puck's authoring tools produce
ordinary documents that the runtime validates, so editing through a tool does
not bypass the document's rules.

| Task | Guide and example |
|---|---|
| Author a world in the Puck DSL | [World vocabulary](../../src/Puck.World.Transpiler/README.md), with [language syntax](../../src/Puck.Transpiler/README.md) for expressions, templates and collections. |
| Inspect or edit a running world | [World console](../../src/Puck.World/README.md), including document mutations, reload and saved state. |
| Understand document fields | [World schema](../../src/Puck.World.Schema/README.md). |
| Write one shader or a multi-pass effect | [Live shader workflow](../../src/Puck.World/README.md#shader-pipelines), then the [pipeline contract](../../src/Puck.Shaders/README.md#shader-pipelines-and-live-development). |
| Build a cartridge | [Cartridge DSL](../../src/Puck.GamingBricks.Transpiler/README.md) and [forge workflow](../../src/Puck.GamingBricks.Forge/README.md). |
| Author shapes or audio | [World authoring library](../../src/Puck.World.Authoring/README.md) and [Example documents](../examples/README.md). |

## Source, document and running state

The DSL is an authoring language that lowers to JSON. Templates and generated
collections make the source easier to maintain; the runtime receives the
resulting document. Decompilation can recover an equivalent document source,
but cannot reconstruct the author's original templates or comments.

The document's **vocabulary** defines what its fields mean. A world and a
cartridge share language syntax while having different validation rules.
Compilation checks the source; vocabulary validation checks whether the
resulting content is admitted. Native cartridge compilation and shader
compilation add their own target requirements.

Keep a runnable example small while learning. Make one change, validate it,
then inspect the result in the actual host. A successful source compilation
alone does not prove that a shader renders correctly or that a cartridge plays
correctly. The linked guides provide the appropriate run and verification steps.

For upcoming authoring features and release work, see the
[plans](../plans/README.md). Current command syntax belongs in the project
guides above, so it has one place to stay synchronized with the implementation.
