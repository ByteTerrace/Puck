using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuStorageImage"/>: a storage-usage color image plus its 2D view, destroyed through the
/// offscreen-image and framebuffer-set APIs on dispose.
/// </summary>
public sealed class VulkanGpuStorageImage : IGpuStorageImage {
    private readonly nint m_deviceHandle;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly nint m_memoryHandle;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private bool m_disposed;
    private nint m_imageHandle;
    private nint m_imageViewHandle;

    /// <summary>Initializes a new instance of the <see cref="VulkanGpuStorageImage"/> class.</summary>
    public VulkanGpuStorageImage(
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        nint deviceHandle,
        nint imageHandle,
        nint memoryHandle,
        nint imageViewHandle,
        uint width,
        uint height
    ) {
        m_deviceHandle = deviceHandle;
        m_framebufferSetApi = framebufferSetApi;
        m_imageHandle = imageHandle;
        m_imageViewHandle = imageViewHandle;
        m_memoryHandle = memoryHandle;
        m_offscreenImageApi = offscreenImageApi;
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
    public uint Width { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_imageViewHandle) {
            m_framebufferSetApi.DestroyImageView(deviceHandle: m_deviceHandle, imageViewHandle: m_imageViewHandle);
            m_imageViewHandle = 0;
        }

        if (0 != m_imageHandle) {
            m_offscreenImageApi.DestroyColorImage(deviceHandle: m_deviceHandle, imageHandle: m_imageHandle, memoryHandle: m_memoryHandle);
            m_imageHandle = 0;
        }
    }
}
