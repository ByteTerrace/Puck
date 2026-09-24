using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// A buffer <see cref="Interfaces.IVulkanBufferApi.Create"/> made: the native buffer, the memory bound to it, and the
/// device that owns both. <see cref="Interfaces.IVulkanBufferApi.Destroy"/> releases them.
/// </summary>
/// <param name="Device">The command table of the logical device that owns the buffer and its memory.</param>
/// <param name="Buffer">The native <c>VkBuffer</c> handle.</param>
/// <param name="Memory">The native <c>VkDeviceMemory</c> handle bound to <paramref name="Buffer"/>.</param>
public readonly record struct VulkanBufferHandles(
    VulkanDeviceCommands Device,
    nint Buffer,
    nint Memory
);
