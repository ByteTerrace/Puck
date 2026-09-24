using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native swapchain (<c>VkSwapchainKHR</c>) handle, along with the image format and extent it was
/// created with, and destroys it when disposed.
/// </summary>
public sealed class VulkanSwapchain : IDisposable {
    private readonly IVulkanSwapchainApi m_swapchainApi;

    private bool m_disposed;

    /// <summary>Gets the command table of the logical device that owns the swapchain.</summary>
    public VulkanDeviceCommands Device { get; }
    /// <summary>Gets the native <c>VkSwapchainKHR</c> handle, or zero once the swapchain has been disposed.</summary>
    public nint Handle { get; private set; }
    /// <summary>Gets the height, in pixels, of the swapchain images.</summary>
    public uint ImageExtentHeight { get; }
    /// <summary>Gets the width, in pixels, of the swapchain images.</summary>
    public uint ImageExtentWidth { get; }
    /// <summary>Gets the format of the swapchain images, as a <c>VkFormat</c> value.</summary>
    public uint ImageFormat { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanSwapchain"/> class, taking ownership of an existing native swapchain handle.</summary>
    /// <param name="swapchainHandle">The native <c>VkSwapchainKHR</c> handle to own.</param>
    /// <param name="device">The command table of the logical device that owns the swapchain.</param>
    /// <param name="imageFormat">The format of the swapchain images, as a <c>VkFormat</c> value.</param>
    /// <param name="imageExtentWidth">The width, in pixels, of the swapchain images.</param>
    /// <param name="imageExtentHeight">The height, in pixels, of the swapchain images.</param>
    /// <param name="swapchainApi">The API used to destroy the swapchain on disposal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="swapchainApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="swapchainHandle"/> is zero.</exception>
    public VulkanSwapchain(
        nint swapchainHandle,
        VulkanDeviceCommands device,
        uint imageFormat,
        uint imageExtentWidth,
        uint imageExtentHeight,
        IVulkanSwapchainApi swapchainApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: swapchainApi);

        VulkanArgument.RequireHandle(
            handle: swapchainHandle,
            handleDescription: "swapchain",
            paramName: nameof(swapchainHandle)
        );

        ArgumentNullException.ThrowIfNull(argument: device);

        Handle = swapchainHandle;
        Device = device;
        ImageFormat = imageFormat;
        ImageExtentWidth = imageExtentWidth;
        ImageExtentHeight = imageExtentHeight;
        m_swapchainApi = swapchainApi;
    }

    /// <summary>Destroys the owned swapchain handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_swapchainApi.DestroySwapchain(
            device: Device,
            swapchainHandle: Handle
        );
        Handle = 0;
        m_disposed = true;
    }
}
