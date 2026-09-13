# Generated assets

Keep authoritative sources in Git. Shader bytecode (`.spv`, `.dxil`) and its
`.hash` sidecars are ignored local outputs; DXC generates them through
`Shaders.targets`. CI packages the bytecode, so players do not need a compiler.
Missing outputs, including sidecars, invalidate the incremental compile target.

`WorldAssets.targets` lists the world `.puck` parents whose JSON outputs are
ignored. It runs the existing `puck compile` command after the CLI build. The
game and world transpiler tests have build-only references to the CLI, ensuring
those documents exist before copying or reading them. The game declares these
outputs explicitly so a clean build includes files absent at project evaluation.
The CLI is not an assembly or runtime dependency of the game.

When adding another generated world, add its parent to `WorldAssets.targets`,
ignore its output in the root `.gitignore`, and add the output to the game's
explicit generated content items (excluding it from the general asset glob).
Keep JSON without a confirmed parent tracked. Do not force-add generated assets.
`ShippedWorldsParityTests` checks compiled source against the generated documents.

Publish from a normal build: the resulting game content includes precompiled
worlds and shaders. A `--no-build` publish or test requires prior generation.
