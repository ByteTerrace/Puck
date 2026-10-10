# Puck.Shaders.Generator

Puck.Shaders.Generator is the build-time host of the kernel builds. With the
repository root as its argument it runs the shader declaration generator
(`ShaderDeclarations` in `Puck.SdfVm.Model`), so a build writes each
declaration whose text the model has changed before any kernel compiles. With
`compile` or `check` it runs one project's shader build (`ShaderBuild` in
`Puck.Shaders.Model`) for `build/Shaders.targets`: every output compiles through
`ShaderCompiler` and its per-user cache on cores MSBuild grants, or, for a pack
that skips the build, is checked against its sources. Every shader project
references it as a build-only project reference. It is never referenced as an
assembly and never ships.

## Documentation

- [Shader manifests, pipelines, and compilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/shaders.md#generated-declarations) — the generated declarations and their build order.
- [Freshness](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/shaders.md#freshness) — the shader build, its cache and its publication rules.
- [The `puck shaders generate` verb](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/cli.md#puck-shadersshader-compilation) — writing and checking the same list by hand.
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.