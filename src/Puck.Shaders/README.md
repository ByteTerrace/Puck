# Puck.Shaders

Puck.Shaders loads already-compiled shader bytecode and hands the two GPU
backends a validated, content-addressed handle to it. It compiles nothing
itself: a vertex/fragment pair's bytes arrive from an `IAssetSource`, get
identified and validated as SPIR-V (Vulkan) or a DXBC/DXIL container
(Direct3D 12) from their leading magic, and are cached by content hash so
loading the same bytes twice returns the same buffer.

## ✨ Key features

- *One loader, both backends:* `ShaderModuleLoader` reads a shader's bytes once
  and classifies them by magic — no backend-specific loading path.
- *Format-aware validation:* SPIR-V bytes must be a positive multiple of 4 and
  at least 20 bytes; a DXBC/DXIL container's declared total size must match
  the file's actual length. A file that matches neither magic is rejected
  before it ever reaches a backend.
- *Content-addressed caching:* validated bytes are keyed by
  `Puck.Assets`'s `AssetContentHash` in a bounded LRU
  (`ContentAddressedLruCache`), so the same shader referenced from several
  pipelines is read and hashed once.

## 🚀 Quick start

```csharp
using Puck.Assets;
using Puck.Shaders;

IShaderModuleLoader loader = new ShaderModuleLoader(assetSource: new FileSystemAssetSource());

ValidatedShaderSet shaders = loader.ValidateShaderSet(shaderSet: new ShaderSet(
    VertexShaderPath: "shaders/unlit.vert.spv",
    FragmentShaderPath: "shaders/unlit.frag.spv"
));

// shaders.Vertex.Content / shaders.Fragment.Content -> the validated bytecode,
// ready for IVulkanShaderModuleApi or IDirectXGpuShaderModuleFactory.
```

`ValidateShader(stage, path)` validates one stage on its own; `ValidateShaderSet`
validates a vertex/fragment pair in one call. Both throw (`FileNotFoundException`,
`InvalidDataException`, `ArgumentException`) rather than returning a null or
partially valid result — a missing, empty, or unrecognized shader file is a
loud failure at load time, not a silent one at draw time.

## 📋 Core types

| Type | Role |
|------|------|
| `IShaderModuleLoader` / `ShaderModuleLoader` | Validates one stage or a vertex/fragment set from an `IAssetSource`, with content-hash caching. |
| `ShaderStage` | `Vertex`, `Fragment`, or `Compute`. |
| `ShaderStageInfo` | One validated stage: its stage, path, byte length, content hash, and cached bytes. |
| `ShaderSet` | A vertex-path/fragment-path pair to validate together. |
| `ValidatedShaderSet` | The validated `ShaderStageInfo` pair `ShaderModuleLoader.ValidateShaderSet` returns. |

## 🧪 Verification

There is no dedicated `Puck.Shaders.Tests` project today; the loader is
exercised indirectly wherever `Puck.Vulkan` or `Puck.DirectX` load a shader
module. Puck.Cli's `puck architecture` and `dotnet build Puck.slnx -c Release`
are what gate this project directly.

## 📦 Packaging

`ByteTerrace.Puck.Shaders` depends on `Puck.Assets` (the `IAssetSource` bytes
are read through, and `AssetContentHash`/`ContentAddressedLruCache` for
caching). `Puck.Vulkan` and `Puck.Vulkan.Presentation` reference it for
compiled SPIR-V; it carries no GPU, windowing, or shader-compiler dependency
of its own.
