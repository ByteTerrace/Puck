using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for sharing image memory across backends without a host-memory round trip:
/// importing an external resource's memory (a shared Direct3D 12 texture) into a Vulkan image.
/// </summary>
public interface IVulkanExternalMemoryApi {
    /// <summary>Creates a Vulkan image whose memory is imported from an external shared NT handle.</summary>
    /// <param name="request">The external image import parameters.</param>
    /// <returns>The created image and imported memory handles.</returns>
    VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request);
    /// <summary>Destroys an imported image and frees its imported memory; a zero handle is skipped.</summary>
    /// <param name="device">The command table of the logical device that owns the image.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to destroy.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to free.</param>
    void DestroyImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle);
}
