using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for importing an external resource's memory (for example a shared Direct3D 12
/// texture) into a sampleable Vulkan image — the zero-copy alternative to copying pixels through host memory.
/// </summary>
public interface IVulkanExternalMemoryApi {
    /// <summary>Creates a Vulkan image whose memory is imported from an external shared NT handle.</summary>
    /// <param name="request">The external image import parameters.</param>
    /// <returns>The created image and imported memory handles.</returns>
    VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request);
    /// <summary>Destroys an imported image and frees its imported memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the image.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to destroy.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to free.</param>
    void DestroyImage(nint deviceHandle, nint imageHandle, nint memoryHandle);
}
