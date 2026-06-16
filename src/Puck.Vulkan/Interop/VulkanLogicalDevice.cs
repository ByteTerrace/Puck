using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native logical device (<c>VkDevice</c>) handle and its graphics and present queues, and destroys
/// the device when disposed.
/// </summary>
public sealed class VulkanLogicalDevice : IDisposable {
    private bool m_disposed;
    private readonly IVulkanLogicalDeviceApi m_logicalDeviceApi;

    /// <summary>Gets the graphics queue of the device.</summary>
    public VkQueue GraphicsQueue { get; }
    /// <summary>Gets the native <c>VkDevice</c> handle.</summary>
    public nint Handle { get; }
    /// <summary>Gets the physical device this logical device was created from.</summary>
    public VkPhysicalDevice PhysicalDevice { get; }
    /// <summary>Gets the present queue of the device. May be the same queue as <see cref="GraphicsQueue"/>.</summary>
    public VkQueue PresentQueue { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanLogicalDevice"/> class, taking ownership of an existing native device handle.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle to own.</param>
    /// <param name="physicalDevice">The physical device the logical device was created from.</param>
    /// <param name="graphicsQueue">The graphics queue.</param>
    /// <param name="presentQueue">The present queue.</param>
    /// <param name="logicalDeviceApi">The API used to destroy the device and wait for it to idle.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logicalDeviceApi"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> is zero.</exception>
    public VulkanLogicalDevice(
        nint deviceHandle,
        VkPhysicalDevice physicalDevice,
        VkQueue graphicsQueue,
        VkQueue presentQueue,
        IVulkanLogicalDeviceApi logicalDeviceApi
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDeviceApi);

        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        Handle = deviceHandle;
        PhysicalDevice = physicalDevice;
        GraphicsQueue = graphicsQueue;
        PresentQueue = presentQueue;
        m_logicalDeviceApi = logicalDeviceApi;
    }

    /// <summary>Destroys the owned device handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_logicalDeviceApi.DestroyDevice(deviceHandle: Handle);
        m_disposed = true;
    }
    /// <summary>Blocks until all queues on the device have completed their outstanding work.</summary>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="VulkanException">The underlying <c>vkDeviceWaitIdle</c> call failed.</exception>
    public void WaitIdle() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var result = m_logicalDeviceApi.WaitIdle(deviceHandle: Handle);

        result.ThrowIfFailed(operation: "vkDeviceWaitIdle");
    }
}
