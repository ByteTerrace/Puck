namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a present operation: the swapchain image to present and the semaphore waited on before it is displayed.
/// </summary>
/// <param name="ImageIndex">The index of the swapchain image to present.</param>
/// <param name="PresentQueueHandle">The native <c>VkQueue</c> handle the present is submitted to.</param>
/// <param name="RenderFinishedSemaphoreHandle">The native <c>VkSemaphore</c> handle waited on before presentation.</param>
/// <param name="SwapchainHandle">The native <c>VkSwapchainKHR</c> handle to present from.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="PresentId">A monotonic present id chained as <c>VkPresentIdKHR</c> for later <c>vkWaitForPresentKHR</c>; <c>0</c> chains nothing (the default — present-timing feedback off).</param>
public readonly record struct VulkanPresentRequest(
    uint ImageIndex,
    nint PresentQueueHandle,
    nint RenderFinishedSemaphoreHandle,
    nint SwapchainHandle,
    nint DeviceHandle,
    ulong PresentId = 0UL
);
