namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a frame's command buffer submission: the command buffer, the queue, the wait and signal
/// semaphores, and the fence signaled on completion.
/// </summary>
/// <param name="CommandBufferHandle">The native <c>VkCommandBuffer</c> handle to execute.</param>
/// <param name="FenceHandle">The native <c>VkFence</c> handle signaled when the submission completes.</param>
/// <param name="GraphicsQueueHandle">The native <c>VkQueue</c> handle the work is submitted to.</param>
/// <param name="ImageAvailableSemaphoreHandle">The native <c>VkSemaphore</c> handle waited on before execution (the acquired image is ready).</param>
/// <param name="RenderFinishedSemaphoreHandle">The native <c>VkSemaphore</c> handle signaled when execution completes (ready to present).</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
public readonly record struct VulkanFrameSubmitRequest(
    nint CommandBufferHandle,
    nint FenceHandle,
    nint GraphicsQueueHandle,
    nint ImageAvailableSemaphoreHandle,
    nint RenderFinishedSemaphoreHandle,
    nint DeviceHandle
);
