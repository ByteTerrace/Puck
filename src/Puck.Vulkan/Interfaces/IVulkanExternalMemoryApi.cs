using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points for sharing image memory across backends without a host-memory round trip:
/// importing an external resource's memory (for example a shared Direct3D 12 texture) into a sampleable Vulkan
/// image, and creating a Vulkan image in exportable memory whose shared handle another Vulkan instance imports.
/// </summary>
public interface IVulkanExternalMemoryApi {
    /// <summary>Creates a Vulkan image whose memory is imported from an external shared NT handle.</summary>
    /// <param name="request">The external image import parameters.</param>
    /// <returns>The created image and imported memory handles.</returns>
    VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request);
    /// <summary>Creates a Vulkan image backed by exportable, dedicated device memory and retrieves the shared
    /// opaque Win32 NT handle another Vulkan instance imports to sample it zero-copy.</summary>
    /// <param name="request">The exportable image parameters.</param>
    /// <returns>The created image, its exportable memory, and the exported shared handle.</returns>
    VulkanExternalImageExportResult CreateExportableImage(VulkanExternalImageExportRequest request);
    /// <summary>Imports an opaque Win32 handle produced by <see cref="CreateExportableImage"/> into a sampleable
    /// Vulkan image on this device — the consuming half of a Vulkan-to-Vulkan zero-copy share. Unlike
    /// <see cref="ImportImage"/> (a foreign Direct3D 12 resource), an opaque handle's compatible memory types come
    /// from the image's own requirements rather than a handle-properties query.</summary>
    /// <param name="request">The import parameters; <c>SharedHandle</c> must be an opaque Win32 handle from <see cref="CreateExportableImage"/>.</param>
    /// <returns>The created image and imported memory handles.</returns>
    VulkanExternalImageImportResult ImportOpaqueImage(VulkanExternalImageImportRequest request);
    /// <summary>Destroys an imported image and frees its imported memory.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the image.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle to destroy.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle to free.</param>
    void DestroyImage(nint deviceHandle, nint imageHandle, nint memoryHandle);
}
