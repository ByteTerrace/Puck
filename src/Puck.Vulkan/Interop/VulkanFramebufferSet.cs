using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns the per-swapchain-image resources of a framebuffer set — the framebuffers and image views (the
/// swapchain images themselves are owned by the swapchain) — and destroys the framebuffers and image views
/// when disposed.
/// </summary>
public sealed class VulkanFramebufferSet : IDisposable {
    private bool m_disposed;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;

    /// <summary>Gets the native <c>VkDevice</c> handle that owns the resources.</summary>
    public nint DeviceHandle { get; }
    /// <summary>Gets the native <c>VkFramebuffer</c> handles, one per swapchain image. Empty once disposed.</summary>
    public IReadOnlyList<nint> FramebufferHandles { get; private set; }
    /// <summary>Gets the native <c>VkImage</c> handles of the swapchain images (not owned by this set). Empty once disposed.</summary>
    public IReadOnlyList<nint> ImageHandles { get; private set; }
    /// <summary>Gets the native <c>VkImageView</c> handles, one per swapchain image. Empty once disposed.</summary>
    public IReadOnlyList<nint> ImageViewHandles { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="VulkanFramebufferSet"/> class, taking ownership of the framebuffers and image views.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle that owns the resources.</param>
    /// <param name="imageHandles">The native <c>VkImage</c> handles of the swapchain images (not owned by this set).</param>
    /// <param name="framebufferHandles">The native <c>VkFramebuffer</c> handles to own.</param>
    /// <param name="imageViewHandles">The native <c>VkImageView</c> handles to own.</param>
    /// <param name="framebufferSetApi">The API used to destroy the framebuffers and image views on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="imageHandles"/>, <paramref name="framebufferHandles"/>, <paramref name="imageViewHandles"/>, or <paramref name="framebufferSetApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> is zero.</exception>
    public VulkanFramebufferSet(
        nint deviceHandle,
        IReadOnlyList<nint> imageHandles,
        IReadOnlyList<nint> framebufferHandles,
        IReadOnlyList<nint> imageViewHandles,
        IVulkanFramebufferSetApi framebufferSetApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: imageHandles);
        ArgumentNullException.ThrowIfNull(argument: framebufferHandles);
        ArgumentNullException.ThrowIfNull(argument: imageViewHandles);
        ArgumentNullException.ThrowIfNull(argument: framebufferSetApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        DeviceHandle = deviceHandle;
        ImageHandles = imageHandles;
        FramebufferHandles = framebufferHandles;
        ImageViewHandles = imageViewHandles;
        m_framebufferSetApi = framebufferSetApi;
    }

    /// <summary>Destroys the owned framebuffers and image views (the swapchain images are left untouched). Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        foreach (var framebufferHandle in FramebufferHandles) {
            if (0 != framebufferHandle) {
                m_framebufferSetApi.DestroyFramebuffer(
                    deviceHandle: DeviceHandle,
                    framebufferHandle: framebufferHandle
                );
            }
        }

        foreach (var imageViewHandle in ImageViewHandles) {
            if (0 != imageViewHandle) {
                m_framebufferSetApi.DestroyImageView(
                    deviceHandle: DeviceHandle,
                    imageViewHandle: imageViewHandle
                );
            }
        }

        FramebufferHandles = [];
        ImageViewHandles = [];
        ImageHandles = [];
        m_disposed = true;
    }
}
