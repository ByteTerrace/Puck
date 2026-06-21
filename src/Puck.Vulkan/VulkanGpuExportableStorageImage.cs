using System.Runtime.InteropServices;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuExportableStorageImage"/>: a STORAGE + SAMPLED compute-writable image whose backing
/// memory lives in exportable, dedicated device memory. It is the compute-dispatch counterpart of
/// <see cref="VulkanGpuExportableRenderTarget"/> — beyond the normal storage-image handles, it exposes an opaque
/// Win32 NT handle (<see cref="SharedHandle"/>) another Vulkan instance imports to sample the result zero-copy.
/// (Unlike a Direct3D 12 shared texture, an opaque-Vulkan handle is not importable by Direct3D 12, so this is a
/// Vulkan-to-Vulkan capability.)
/// <para>
/// The producer transitions the image to <see cref="GpuImageLayout.External"/> (Vulkan GENERAL) as its final
/// recorded barrier and submits through the neutral queue; <see cref="FinalizeForExport"/> only drains the device
/// so the importing instance samples completed writes.
/// </para>
/// </summary>
public sealed partial class VulkanGpuExportableStorageImage : IGpuExportableStorageImage {
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly VulkanLogicalDevice m_logicalDevice;
    private readonly nint m_memoryHandle;
    private bool m_disposed;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_sharedHandle;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuExportableStorageImage"/> class.</summary>
    /// <param name="externalMemoryApi">The API that destroys the exportable image and frees its memory on dispose.</param>
    /// <param name="framebufferSetApi">The API that destroys the image view on dispose.</param>
    /// <param name="logicalDevice">The logical device the resources live on; drained in <see cref="FinalizeForExport"/>.</param>
    /// <param name="imageHandle">The native <c>VkImage</c> handle.</param>
    /// <param name="memoryHandle">The native <c>VkDeviceMemory</c> handle backing the image.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle bound as the storage descriptor.</param>
    /// <param name="sharedHandle">The exported opaque Win32 NT handle.</param>
    /// <param name="width">The image width, in pixels.</param>
    /// <param name="height">The image height, in pixels.</param>
    public VulkanGpuExportableStorageImage(
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        VulkanLogicalDevice logicalDevice,
        nint imageHandle,
        nint memoryHandle,
        nint imageViewHandle,
        nint sharedHandle,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(logicalDevice);

        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_imageHandle = imageHandle;
        m_imageViewHandle = imageViewHandle;
        m_logicalDevice = logicalDevice;
        m_memoryHandle = memoryHandle;
        m_sharedHandle = sharedHandle;
        Height = height;
        Width = width;
    }

    /// <inheritdoc/>
    public nint ImageHandle => m_imageHandle;
    /// <inheritdoc/>
    public nint ImageViewHandle => m_imageViewHandle;
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public uint Width { get; }

    /// <inheritdoc/>
    public void FinalizeForExport() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        // The producer already recorded the GENERAL handoff transition and submitted; drain the device so the
        // importing instance samples completed writes. Drains on EVERY call (NOT once-only): a per-frame producer
        // re-submits and re-finalizes this image each frame, and the neutral submit path carries no fence — matching
        // the unconditional per-frame fence on the Direct3D 12 exportable storage image.
        m_logicalDevice.WaitIdle();
    }

    /// <summary>Releases the image view, the exportable image and its memory, and closes the exported shared
    /// handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_imageViewHandle) {
            m_framebufferSetApi.DestroyImageView(
                deviceHandle: m_logicalDevice.Handle,
                imageViewHandle: m_imageViewHandle
            );
            m_imageViewHandle = 0;
        }

        if (0 != m_imageHandle) {
            m_externalMemoryApi.DestroyImage(
                deviceHandle: m_logicalDevice.Handle,
                imageHandle: m_imageHandle,
                memoryHandle: m_memoryHandle
            );
            m_imageHandle = 0;
        }

        if (0 != m_sharedHandle) {
            _ = CloseHandle(handle: m_sharedHandle);
            m_sharedHandle = 0;
        }
    }

    // The handle from vkGetMemoryWin32HandleKHR (OPAQUE_WIN32) is a fresh NT handle this image owns and closes.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
