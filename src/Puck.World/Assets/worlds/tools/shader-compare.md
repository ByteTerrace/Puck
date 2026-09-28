# Shader Compare Tool

[shader-compare.puck](shader-compare.puck) provides a side-by-side comparison studio between a procedural pipeline shader (e.g. an HLSL raymarched SDF character or screen shader) and a native Puck SDF model/character.

Moth is configured as the default comparison pair, but the tool is generic: it can be redirected to compare any pipeline shader against any native SDF model.

## Quick Start

Run from the repository root:

```powershell
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/tools/shader-compare.puck --exit-after-seconds 0
```

With an existing Release build:

```powershell
dotnet src/Puck.World/bin/Release/net10.0/Puck.World.dll --world src/Puck.World/Assets/worlds/tools/shader-compare.puck --exit-after-seconds 0
```

## How It Works

- The **left slot** (50% width) shows the pipeline shader (`compare-pipeline`, by default [moth.hlsl](../../pipelines/moth.hlsl)). Each shader view is a `graph "…"` row inside `views`, and a slot shows it with `instance: "…"`; the root render graph places it over the SDF world at the slot's rect.
- The **right slot** (50% width) renders the native SDF model from [moth.puck](../avatars/moth.puck).
- Both slots read the identical paired camera, maintaining synchronized perspective, framing, and field of view.

## Controls

### Comparison Viewpoints

| Key | Layout | Description |
|---|---|---|
| `1` | `compare` | Three-quarter perspective comparison (default) |
| `2` | `compare-front` | Front perspective comparison |
| `3` | `compare-rear` | Rear perspective comparison |
| `4` | `compare-flight` | Wide flight-stage comparison |
| `5` | `compare-face` | Close-up face comparison |

### Interactive Console / MCP Commands

In the running Puck console or via the MCP operator interface:

- **Live Reload / Watch**:
  ```text
  pipeline.watch compare-pipeline on
  ```
  Watches the underlying `.hlsl` shader file and recompiles automatically whenever saved.

- **Manual Recompile**:
  ```text
  pipeline.reload compare-pipeline
  ```

- **Swap Pipeline Shader**:
  ```text
  pipeline.load compare-pipeline <path-to-shader.hlsl> [camera]
  ```
  Replaces the `compare-pipeline` row's source, so the comparison slot shows any other pipeline shader.

- **Check Status**:
  ```text
  pipeline.status
  world.view.state
  ```
  `world.view.state` prints a shader slot as `instance:<name>`, or `instance:<name>:missing` when the renderer has no such instance.

- **Switch Layouts**:
  ```text
  view.override layout compare
  view.override layout pipeline-only
  view.override layout sdf-only
  ```

## Customizing the Comparison

To point `shader-compare.puck` at a different default pair:

1. Update `basis:` to point to the desired `.puck` world or avatar definition.
2. Update `let pipelineSource = "..."` to point to the desired `.hlsl` shader.
3. Run `puck compile src/Puck.World/Assets/worlds/tools/shader-compare.puck --validate`.
