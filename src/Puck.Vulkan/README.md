# Puck.Vulkan

Puck.Vulkan provides the self-contained Vulkan graphics backend substrate:
P/Invoke bindings, memory allocation seams, and device/swapchain abstractions.

`VulkanGpuTimestampFactory` provides optional timestamp query pools using the
recording queue's valid-bit width and physical device timestamp period. A queue
without timestamp support returns no pool. Consumers reset and resolve written
ranges, then wait for the submission fence before reading or disposing them.

## Documentation

- [Vulkan backend substrate](https://github.com/ByteTerrace/Puck/blob/main/docs/rendering/vulkan.md) — loader interop, factory pattern, physical device selection, and frame presenter.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Parity/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
