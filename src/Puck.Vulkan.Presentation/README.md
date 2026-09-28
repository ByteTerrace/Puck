# Puck.Vulkan.Presentation

Puck.Vulkan.Presentation connects `Puck.Vulkan` to Puck's rendering contracts.
It provides the Vulkan surface presenter, command-buffer recording and compute
service registration. It presents a surface through the display encode that
`Puck.Shaders` ships (`SurfaceEncoder`) and carries no shader of its own. The
project owns this presentation seam; Vulkan loading and resource APIs stay in
`Puck.Vulkan`.
`VulkanRenderer` reports its device to backend-neutral code as the device
command table's token, as described in [the Vulkan backend](../../docs/rendering/vulkan.md#command-tables).

## Usage

Register the Vulkan presentation services from the composition root after a
Vulkan device and surface binding are selected.

## Verification

Build with `dotnet build src/Puck.Vulkan.Presentation -c Release`. GPU and
cross-backend checks use the rendering parity workflow in the development guide.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
