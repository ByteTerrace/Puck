namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a present operation: the swapchain image to present and the semaphore waited on before it is displayed.
/// </summary>
/// <param name="ImageIndex">The index of the swapchain image to present.</param>
/// <param name="PresentQueueHandle">The native <c>VkQueue</c> handle the present is submitted to.</param>
/// <param name="RenderFinishedSemaphoreHandle">The native <c>VkSemaphore</c> handle waited on before presentation.</param>
/// <param name="SwapchainHandle">The native <c>VkSwapchainKHR</c> handle to present from.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
public readonly record struct VulkanPresentRequest(
    uint ImageIndex,
    nint PresentQueueHandle,
    nint RenderFinishedSemaphoreHandle,
    nint SwapchainHandle,
    nint DeviceHandle
);
