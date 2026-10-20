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
lies. A valid entry with matching inputs can be published in another checkout
without a compiler run, and an edited include invalidates only the outputs
that reach it. Entries are checked against their key and bytecode digest before
reuse; damaged entries compile again. Missing outputs compile concurrently on cores MSBuild grants
through its own core budget, the longest first. Each project takes one initial
grant and returns unused cores; it makes no blocking follow-up engine request. The
[shader reference](../docs/reference/shaders.md#freshness) owns the details.

Kernel projects build their declaration generator before compiling shaders.
The generator reconciles on every build and leaves equal files untouched.
CI runs `puck shaders generate --check` with its candidate CLI before the
solution build; because a build may already have reconciled the tree, the check
also refuses a generated file whose staged copy differs from the model's.
Packing with `--no-build` compiles nothing: it checks each output against the
sidecar its current closure and options would publish, refuses a missing or
stale one, and collects the rest.

The engine's build compiles no world. A world is an asset package
(`worlds/<id>/`) that `Puck.World --world <folder>` boots from its sources, and
a release publish compiles it beside the published engine with
`puck compile --tree <package> --output <publish>/worlds/<id>`, which writes its
documents, compiled worlds and shader package store and copies every other file
of the package ([asset packages](../docs/architecture/worlds.md#asset-packages)).
No build step writes a world document into `src/` or `worlds/`, so there is
nothing to ignore there: an untracked `*.world.json` in either tree is an error
that `WorldDocumentOutputLawTests` names.

Publish from a normal build: the resulting engine includes precompiled shaders.
A `--no-build` publish or test requires prior generation.
