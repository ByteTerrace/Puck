# Generated assets

Keep authoritative sources in Git. Shader bytecode (`.spv`, `.dxil`) and its
`.hash` sidecars are ignored local outputs; DXC generates them through
`Shaders.targets`. CI packages the bytecode, so players do not need a compiler.
Missing outputs, including sidecars, invalidate the incremental compile target.

`WorldAssets.targets`, imported by the game, hands every `.puck` source and
`.world.json` document under `src/Puck.World/Assets/worlds` to one
`puck compile --tree` run into the game's `obj/` directory. The run writes the
documents the game ships, their compiled worlds, and the one bake pack holding
the creation bakes those compiled worlds name there, and reports what it
wrote (`--written`); the build copies exactly the reported files into the
output's `Assets/worlds`. Only a compile knows which sources emit documents: a
module library emits none, so a `.world.json` named like it ships, while a
`.world.json` beside a world source of its name does not. The sources themselves
are not copied. No build step writes a world document into `src/` or `worlds/`,
so there is nothing to ignore there: an untracked `*.world.json` in either tree
is an error that `WorldDocumentOutputLawTests` names. The tree run also removes any
document in its output that no source compiles to any more, so a renamed or
deleted source leaves nothing behind. The CLI is a build-time tool of the game,
never an assembly or runtime dependency.

A new world needs no build edit: add its `.puck` source under
`Assets/worlds`. A document reference names the document (`games/go`), and
resolves to the `.puck` source that emits that name and to the `.world.json`
document otherwise, so the source tree and the built output resolve the same
names.

Publish from a normal build: the resulting game content includes precompiled
worlds and shaders. A `--no-build` publish or test requires prior generation.
