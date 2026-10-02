# Puck.SdfVm.Model

Puck.SdfVm.Model provides the model the SDF engine's kernel declarations are
generated from: the instruction set's HLSL declaration and fingerprint, the
visibility record's shared words, and the kernels' pass interfaces. Its
`ShaderDeclarations` is the one list of every HLSL declaration the C# model
owns, which `puck shaders generate` writes and checks and the kernel builds
write before their kernels compile. It compiles no shader.

## Documentation

- [Shader manifests, pipelines, and compilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/shaders.md#generated-declarations) — how generated declarations reach the kernels.
- [SDF renderer and field reference](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/sdf/README.md) — program model and frame rendering.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.SdfVm.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
