# Puck.Overlays

Puck.Overlays builds the presentation layer for heads-up displays, console
panels, binding bars, cursors, markers, wheels, and toast messages. It combines
overlay stores and writers with frame construction, glyph data, and a shared
render node. These are presentation components; authoritative world state
belongs to the simulation.

## Structure

Stores retain the state each overlay needs, writers populate it, and
`OverlayFrameBuilder` assembles a frame for `UnifiedOverlayNode`. The project
uses [Puck.Text](../Puck.Text/README.md) for the shared font atlas and layout
data, with a local prepacked glyph artifact for overlay rendering.

`Assets/Shaders/` contains the HLSL sources and committed bytecode. The shared
shader build compiles fragment shaders for Vulkan and DirectX. Compute
shaders currently have a Vulkan consumer and are compiled to SPIR-V only.
See the [project file](Puck.Overlays.csproj) for shader inputs and the shared
[shader build](../../build/Shaders.targets) for their build contract.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Development](../../docs/development/README.md)
