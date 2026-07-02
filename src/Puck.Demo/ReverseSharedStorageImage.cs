using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Demo;

/// <summary>
/// The inverted cross-backend shared image for the reverse present path (Direct3D 12 host, Vulkan content). It wraps
/// ONE shared texture whose two faces live on different backends:
/// <list type="bullet">
///   <item>the <em>owner</em> is the Direct3D 12 HOST (only its <c>CreateSharedHandle</c> NT handle opens on both
///   backends), so the host creates the exportable image and blits it to its own swapchain;</item>
///   <item>the <em>producer-facing</em> view is the bespoke Vulkan device's WRITABLE import of that same memory
///   (handle type <c>D3D12_RESOURCE</c>), which the neutral <see cref="WorldProducerNode"/> binds and dispatches into.</item>
/// </list>
/// Implementing <see cref="IGpuExportableStorageImage"/> puts the producer in export mode: it transitions the Vulkan
/// import to <see cref="GpuImageLayout.External"/> (Vulkan GENERAL — the cross-API resting layout) as its last
/// recorded barrier, then calls <see cref="FinalizeForExport"/>, which drains the Vulkan producer queue (the two
/// backends share no timeline). The producer's emitted surface is discarded by the host node — which owns the
/// swapchain and presents <see cref="HostImage"/> directly — so this carries no <see cref="SharedHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class ReverseSharedStorageImage : IGpuExportableStorageImage {
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IGpuExportableStorageImage m_hostImage;
    private readonly VulkanLogicalDevice m_logicalDevice;
    private readonly nint m_importedMemory;
    private bool m_disposed;
    private nint m_importedImage;
    private nint m_importedView;

    /// <summary>Initializes a new instance of the <see cref="ReverseSharedStorageImage"/> class.</summary>
    /// <param name="hostImage">The Direct3D 12 host-owned exportable storage image (the swapchain side of the share).</param>
    /// <param name="externalMemoryApi">The Vulkan external-memory API that destroys the import on dispose.</param>
    /// <param name="framebufferSetApi">The Vulkan API that destroys the import's image view on dispose.</param>
    /// <param name="logicalDevice">The bespoke Vulkan device the import lives on; drained in <see cref="FinalizeForExport"/>.</param>
    /// <param name="importedImage">The Vulkan <c>VkImage</c> aliasing the host image's shared memory.</param>
    /// <param name="importedMemory">The Vulkan <c>VkDeviceMemory</c> backing the import.</param>
    /// <param name="importedView">The Vulkan <c>VkImageView</c> the producer binds as the storage-image descriptor.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ReverseSharedStorageImage(
        IGpuExportableStorageImage hostImage,
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        VulkanLogicalDevice logicalDevice,
        nint importedImage,
        nint importedMemory,
        nint importedView
    ) {
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(hostImage);
        ArgumentNullException.ThrowIfNull(logicalDevice);

        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_hostImage = hostImage;
        m_importedImage = importedImage;
        m_importedMemory = importedMemory;
        m_importedView = importedView;
        m_logicalDevice = logicalDevice;
    }

    /// <inheritdoc/>
    /// <remarks>The producer-facing Vulkan import handle the compositor kernel writes (NOT the Direct3D 12 resource).</remarks>
    public nint ImageHandle => m_importedImage;
    /// <inheritdoc/>
    /// <remarks>The producer-facing Vulkan import view bound as the output storage-image descriptor.</remarks>
    public nint ImageViewHandle => m_importedView;
    /// <inheritdoc/>
    public uint Height => m_hostImage.Height;
    /// <inheritdoc/>
    public uint Width => m_hostImage.Width;
    /// <inheritdoc/>
    /// <remarks>Zero: the host owns the swapchain and presents <see cref="HostImage"/> directly, so no shared handle
    /// leaves the producer (unlike the forward path, where the producer hands the host a foreign handle to import).</remarks>
    public nint SharedHandle => 0;

    /// <summary>Gets the Direct3D 12 host-owned exportable image the presenter blits — the same shared memory the
    /// Vulkan producer just wrote, read back zero-copy on the owning device.</summary>
    public IGpuExportableStorageImage HostImage => m_hostImage;

    /// <inheritdoc/>
    public void FinalizeForExport() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        // The producer recorded the GENERAL handoff transition and submitted; drain the Vulkan device so the Direct3D
        // 12 host samples completed pixels (the backends share no timeline — the coarse producer-queue block, drained
        // on EVERY frame, exactly as the forward path's exportable images do).
        m_logicalDevice.WaitIdle();
    }

    /// <summary>Releases the Vulkan import (view, image, memory) then the Direct3D 12 host image. Safe to call more
    /// than once. The Vulkan device must already be idle (the producer drains it on teardown).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_importedView) {
            m_framebufferSetApi.DestroyImageView(
                deviceHandle: m_logicalDevice.Handle,
                imageViewHandle: m_importedView
            );
            m_importedView = 0;
        }

        if (0 != m_importedImage) {
            m_externalMemoryApi.DestroyImage(
                deviceHandle: m_logicalDevice.Handle,
                imageHandle: m_importedImage,
                memoryHandle: m_importedMemory
            );
            m_importedImage = 0;
        }

        m_hostImage.Dispose();
    }
}
