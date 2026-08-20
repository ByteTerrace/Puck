# Puck.Shaders

A shader set is data: one HLSL source (or a vertex+fragment pair) and one
`puck.shader.v1` manifest beside it. The manifest declares everything the
engine needs to run the set — its stages, its descriptor bindings, the
configuration a document may author for it, and the push-constant block and
where each field's value comes from. Nothing else is written: no C# node, no
config parser, no registration. Ship the manifest beside the compiled
bytecode and a document selects the set by id.

## ✨ What a user writes

A post-process pass over the rendered world is two files in a project's
`Assets/Shaders/` tree:

```
Assets/Shaders/Sdf/sdf-film-grain.frag.hlsl
Assets/Shaders/Sdf/sdf-film-grain.puck.shader.json
```

```json
{
  "$schema": "puck.shader.v1",
  "name": "sdf-film-grain",
  "description": "Film grain: a per-pixel integer-hashed offset added over the rendered output.",
  "stages": { "vertex": "fullscreen.vert", "fragment": "sdf-film-grain.frag" },
  "bindings": [
    { "kind": "sampledImage", "vulkanBinding": 0, "directXRegister": "t0" }
  ],
  "targetFloor": { "vulkan": "1.3", "shaderModel": "6.6" },
  "config": {
    "intensity": { "type": "float", "default": 0.05, "min": 0, "max": 1, "description": "The peak per-channel offset." },
    "size":      { "type": "float", "default": 1, "min": 1, "description": "The grain cell size, in pixels." },
    "seed":      { "type": "uint",  "default": 0 },
    "flickerHz": { "type": "uint",  "default": 24 }
  },
  "pushConstants": {
    "stages": ["fragment"],
    "fields": [
      { "name": "intensity",  "type": "float", "source": "config.intensity" },
      { "name": "size",       "type": "float", "source": "config.size" },
      { "name": "grainFrame", "type": "uint",  "source": "tick", "quantizeHz": "config.flickerHz" },
      { "name": "seed",       "type": "uint",  "source": "config.seed" }
    ]
  }
}
```

The build compiles the HLSL to SPIR-V and DXIL, writes a `.hash` sidecar per
bytecode file, and ships the manifest beside the bytecode. A world document
then authors:

```json
"render": { "extensions": [ { "id": "sdf-film-grain", "config": { "intensity": 0.08, "seed": 7 } } ] }
```

The id is the manifest's file stem. The engine finds the manifest under the
deploy's `Assets/Shaders` tree (`ShaderSetCatalog`), validates the config
against the manifest's schema — an unknown field, a value out of range, a
missing required field, or a `flickerHz` that does not divide the engine
tick rate refuses by field name — and runs the set as one fullscreen pass over
the world's output (`FullscreenPassNode`). `puck schema` emits every shipped
manifest's config schema into the world-document JSON Schema, so
`render.extensions[].config` also validates by id in an editor.

## 📋 The manifest

| Key | Meaning |
|-----|---------|
| `$schema` | `puck.shader.v1`. |
| `name` | The set's id; must equal the file stem before `.puck.shader.json`. |
| `description` | Carried into the emitted config JSON Schema. |
| `stages` | `{ "vertex", "fragment" }` (a graphics set) or `{ "compute" }`; each a sibling `<stem>.hlsl` compiled to `<stem>.spv` and `<stem>.dxil`. |
| `bindings[]` | `{ kind, vulkanBinding, directXRegister, count }`; `kind` is `storageBuffer`, `sampledImage`, `storageImage`, or `accelerationStructure`. Authored by hand and cross-checked against the pipeline description built for the set. A fullscreen pass declares exactly one `sampledImage` (the inner surface). |
| `targetFloor` | `{ vulkan, shaderModel }` the bytecode was compiled against. |
| `config` | Name → `{ type, default, min, max, description }`. `type` is an HLSL spelling: `float`, `float2..4`, `uint`, `uint2..4`, `int`, `int2..4`; a vector's document value is an array of that many numbers. A field without `default` is required. `min`/`max` are inclusive, per component. |
| `pushConstants` | `{ stages: ["vertex"\|"fragment"\|"compute"], fields: [ { name, type, source, quantizeHz } ] }` in the shader struct's declaration order. |

Push-constant `source` values:

| Source | Fills | Type |
|--------|-------|------|
| `config.<field>` | The bound config value, once. | The field's own type. |
| `tick` | `FrameContext.ElapsedTicks` (the fixed-step simulation clock), integer-divided by `EngineTicks.PerSecond / quantizeHz` when `quantizeHz` is present. `quantizeHz` is a positive integer literal or `config.<uint field>`; it must divide `EngineTicks.PerSecond`. Every frame inside one period sees the same value on every run, machine, and backend. | `uint` (low 32 bits) or `uint2` (low, high). |
| `resolution` | The pass's width and height in pixels. | `float2` or `uint2`. |
| `frame` | The pass's own produced-frame counter — pacing-dependent, presentation only. | `uint`. |

### Push-constant packing

Offsets are computed under HLSL constant-buffer packing, which is what DXC
emits for a `ConstantBuffer<T>` push constant on both targets — Direct3D 12
root constants, and Vulkan push constants under DXC's default SPIR-V layout:
fields sit in declaration order, every field starts on a 4-byte boundary, and
a vector that would straddle a 16-byte boundary is bumped to the next 16-byte
row. There is no other padding; the block's size is the end of its last field.
`ShaderPushConstantLayout.ComputeOffsets` is the rule; the law in
`Puck.Shaders.Tests` reads the offsets DXC assigned out of the compiled
fixture's SPIR-V (`OpMemberDecorate … Offset`) and asserts the computed layout
matches, and pins the DXIL offsets `dxc -Fc` prints for the same source.

### Freshness

The manifest carries no hashes. The build writes a `<bytecode>.hash` sidecar
(source-plus-includes hash and bytecode hash) on every recompile and, on every
build, recomputes both from what is on disk and refuses a stale pair
(`build/Shaders.targets`, `PuckValidateShaderBytecodeFresh`). `Load` therefore
checks only that each stage's `.spv` exists and that every present bytecode
file is well-formed (`ShaderBytecode.ValidateFormat`); the sidecars do not
ship, and a runtime re-check would duplicate the build's gate.

## 🚀 API

```csharp
using Puck.Shaders;

var catalog = ShaderSetCatalog.Scan(rootDirectory: Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders"));
ShaderSetManifest manifest = catalog.Load(id: "sdf-film-grain");   // throws on an unshipped id
ShaderConfigValues config = manifest.BindConfig(config: entry.Config); // throws naming the field

IRenderNode pass = new FullscreenPassNode(
    inner: worldNode, manifest: manifest, config: config,
    services: fullscreenPassServices, hostsOnDirectX: false, width: 1920, height: 1080);
```

`IFullscreenPassServices` is the GPU seam a composition root resolves from
its one registered backend (command recorder, render-target factory,
descriptor allocator, device context, graphics pipeline factory, queue
submitter, shader-module factory, surface-transfer factory, vertex-buffer
factory). The pass is an `ICaptureRequestTarget`: an armed capture reads
back the pass's own render target — the composed result — and prints
`[capture] <set name> -> <path>` on stderr; a frame the pass passes through
untouched forwards the request to its inner node instead.
`ShaderSetManifest.ConfigJsonSchema()` emits the config schema as a JSON
Schema object; `manifest.TryBindConfig(config, out values, out reason)` is
the non-throwing bind. `IShaderModuleLoader`/`ShaderModuleLoader` load and
validate one shader stage's bytes from an `IAssetSource`, cached by content
hash, for a caller building its own pipelines.

| Type | Role |
|------|------|
| `ShaderSetManifest` | A parsed, validated `puck.shader.v1` document with its resolved `PushConstantLayout` and load `Directory`. |
| `ShaderSetCatalog` | The shipped sets under a directory tree, by id. |
| `ShaderConfigField` / `ShaderConfigValues` | One config schema field; a document's bound values. |
| `ShaderPushConstantBlock` / `ShaderPushConstantField` / `ShaderPushConstantLayout` | The authored block; one field; the resolved offsets and parsed sources. |
| `ShaderValueType` | `float`…`int4`, with component count and kind. |
| `FullscreenPassNode` / `IFullscreenPassServices` | The node that runs a graphics set as one pass over an inner `IRenderNode`; its GPU seam. |
| `IShaderModuleLoader` / `ShaderModuleLoader` / `ShaderStageInfo` / `ShaderStage` | Per-stage bytecode loading with content-hash caching. |

## 🧪 Verification

`dotnet test tests/Puck.Shaders.Tests -c Release`: the packing law against
the compiled fixture, manifest loading and refusals, config binding and
schema emission, catalog lookup. `puck parity` holds the film-grain pass to
cross-backend agreement on the real windowed game.

## 📦 Packaging

`ByteTerrace.Puck.Shaders` depends on `Puck.Abstractions`, `Puck.Assets`, and
`Puck.Hosting` (`IRenderNode`, `FrameContext`, `EngineTicks`). It carries no
GPU, windowing, or shader-compiler dependency of its own. The package also
ships `build/Shaders.targets` under
`buildTransitive/ByteTerrace.Puck.Shaders.targets` — the shared HLSL-to-
SPIR-V/DXIL compile recipe every in-repo shader project imports, which also
ships each project's manifests beside its bytecode. A consumer that authors
its own shaders needs the DirectX Shader Compiler (`dxc`, from the Vulkan
SDK or Windows SDK) on `PATH`, or must pass `/p:DxcCommand="path\to\dxc"`;
a consumer with no shader items of its own never invokes `dxc` at all.
