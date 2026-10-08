# Generated assets

Keep authoritative sources in Git. Shader bytecode (`.spv`, `.dxil`) and its
`.hash` sidecars are ignored local outputs. `Shaders.targets` runs each shader
project's build through the build-only `Puck.Shaders.Generator`, which compiles
with `ShaderCompiler`, the compiler the runtime runs, so the options are
`ShaderCompiler.StepsOf`'s alone. CI packages the bytecode, so players do not
need a compiler.

Every output compiles into one content-addressed cache, by default `shaders`
under the per-user Puck directory, or the directory
`-p:PuckShaderCacheDirectory=<dir>` names. An output's key hashes its stage
source's include closure, its options and its `dxc`, not where the checkout
lies, so a shader any checkout on the machine has compiled is published from the
cache and runs no compiler, and an edited include recompiles only the outputs
that reach it. Missing outputs compile concurrently on cores MSBuild grants
through its own core budget, the longest first. The
[shader reference](../docs/reference/shaders.md#freshness) owns the details.

Kernel projects build their declaration generator before compiling shaders.
The generator reconciles on every build and leaves equal files untouched.
CI runs `puck shaders generate --check` with its candidate CLI before the
solution build; because a build may already have reconciled the tree, the check
also refuses a generated file whose staged copy differs from the model's.
Packing with `--no-build` compiles nothing: it checks each output against the
sidecar its current closure and options would publish, refuses a missing or
stale one, and collects the rest.

`WorldAssets.targets`, imported by the game, hands every `.puck` source and
`.world.json` document under `src/Puck.World/Assets/worlds` to one
`puck compile --tree` run into the game's `obj/` directory. The run writes the
documents the game ships, their compiled worlds, and the one bake pack holding
the creation bakes those compiled worlds name there, and reports what it
wrote (`--written`); the build copies exactly the reported files into the
output's `Assets/worlds`. The run reads and keeps its bakes in the
content-addressed `bakes` cache under the per-user Puck directory
(`--bake-cache`), which every configuration and every checkout shares with
`puck parity` and the World, and `dotnet clean` keeps, so a run bakes only the
creations whose keys no build on the machine has baked: a new worktree bakes
nothing another has baked, an engine change that reruns the compile bakes
nothing, and an edited prototype bakes that prototype alone. A bake's key
carries the bake code's fingerprint (`DerivationFingerprint.Bake`), so two
checkouts whose bake code differs never share a key. `-p:PuckWorldBakeCache=<dir>`
names another cache. The run's one incremental output is the
report, which every run writes last; a reported file that is gone removes the
report, so the next build runs again. Only a compile knows which sources emit documents: a
module library emits none, so a `.world.json` named like it ships, while a
`.world.json` beside a world source of its name does not. The sources themselves
are not copied. No build step writes a world document into `src/` or `worlds/`,
so there is nothing to ignore there: an untracked `*.world.json` in either tree
is an error that `WorldDocumentOutputLawTests` names. The tree run also removes any
document in its output that no source compiles to any more, so a renamed or
deleted source leaves nothing behind. A copy never deletes, so after copying,
the build also removes every file in the output's `Assets/worlds/packages`
store that it no longer deploys, with the directories that leaves empty
(`PuckRemoveStaleWorldPackages.cs`), and prints one line per file. The CLI is a build-time tool of the game,
never an assembly or runtime dependency.

A new world needs no build edit: add its `.puck` source under
`Assets/worlds`. A document reference names the document (`games/go`), and
resolves to the `.puck` source that emits that name and to the `.world.json`
document otherwise, so the source tree and the built output resolve the same
names.

Publish from a normal build: the resulting game content includes precompiled
worlds and shaders. A `--no-build` publish or test requires prior generation.
