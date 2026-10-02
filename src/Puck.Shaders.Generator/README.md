# Puck.Shaders.Generator

Puck.Shaders.Generator is the build-time host of the shader declaration
generator (`ShaderDeclarations` in `Puck.SdfVm.Model`). Every project whose
kernels include a declaration the C# model owns references it as a build-only
project reference, so its build writes each declaration whose text the model
has changed before those kernels compile. It is never referenced as an
assembly and never ships.

## Documentation

- [Shader manifests, pipelines, and compilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/shaders.md#generated-declarations) — the generated declarations and their build order.
- [The `puck shaders generate` verb](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/cli.md#puck-shadersshader-compilation) — writing and checking the same list by hand.
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
