using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a swapchain image acquisition: the swapchain to acquire from and the synchronization primitives
/// signaled when the image becomes available.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="ImageAvailableSemaphoreHandle">The native <c>VkSemaphore</c> handle signaled when the acquired image is ready, or zero.</param>
/// <param name="InFlightFenceHandle">The native <c>VkFence</c> handle signaled when the acquired image is ready, or zero.</param>
/// <param name="SwapchainHandle">The native <c>VkSwapchainKHR</c> handle to acquire from.</param>
/// <param name="TimeoutNanoseconds">The maximum time to wait for an image, in nanoseconds.</param>
public readonly record struct VulkanFrameAcquireRequest(
    VulkanDeviceCommands Device,
    nint ImageAvailableSemaphoreHandle,
    nint InFlightFenceHandle,
    nint SwapchainHandle,
    ulong TimeoutNanoseconds
);
