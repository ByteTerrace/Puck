# Puck.Vulkan.Presentation

Puck.Vulkan.Presentation connects `Puck.Vulkan` to Puck's rendering contracts.
It provides the Vulkan surface presenter, command-buffer recording, compute
service registration, and the small blit shaders used to present a surface.
The project owns this presentation seam; Vulkan loading and resource APIs stay
in `Puck.Vulkan`, and shader compilation follows the shared build targets.

## Usage

Register the Vulkan presentation services from the composition root after a
Vulkan device and surface binding are selected. The project carries SPIR-V for
its presenter shaders and deliberately disables DXIL for this Vulkan-only
presentation project.

## Verification

Build with `dotnet build src/Puck.Vulkan.Presentation -c Release`. GPU and
cross-backend checks use the rendering parity workflow in the development guide.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
