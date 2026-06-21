using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a host-visible <see cref="VulkanStorageBuffer"/> of a given size.
/// </summary>
public interface IVulkanStorageBufferFactory {
    /// <summary>Creates a storage buffer and allocates its backing memory.</summary>
    /// <param name="vulkanInstance">The Vulkan instance, used to resolve memory and buffer-address support.</param>
    /// <param name="logicalDevice">The logical device the buffer is created on.</param>
    /// <param name="sizeBytes">The size, in bytes, of the buffer.</param>
    /// <param name="deviceLocal">When <see langword="true"/>, back the buffer with device-local (not host-visible) memory — a GPU-written/read storage buffer that cannot be mapped for host writes; the default is host-visible.</param>
    /// <param name="indirectArgs">When <see langword="true"/>, also add <c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c> so the buffer can back an indirect dispatch/draw.</param>
    /// <returns>A new, owning <see cref="VulkanStorageBuffer"/>.</returns>
    VulkanStorageBuffer Create(
        VulkanInstance vulkanInstance,
        VulkanLogicalDevice logicalDevice,
        ulong sizeBytes,
        bool deviceLocal = false,
        bool indirectArgs = false
    );
}
