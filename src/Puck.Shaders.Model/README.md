# Puck.Shaders.Model

Puck.Shaders.Model provides the model Puck's shader declarations are generated
from: pass interfaces and their generated HLSL, the frame block every pass
reads, the config binder, the pipeline document's records, and the engine's
render-graph package catalog. It also holds the one shader compiler
(`ShaderCompiler`, with its include closure, compile identity and
content-addressed cache) and the build's shader build (`ShaderBuild`). It
compiles no shader itself, so a kernel's generated declarations can always be
written, and the compiler built, before any kernel builds.

## Documentation

- [Shader manifests, pipelines, and compilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/shaders.md) — pass interfaces, the frame block, and generated declarations.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Shaders.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
