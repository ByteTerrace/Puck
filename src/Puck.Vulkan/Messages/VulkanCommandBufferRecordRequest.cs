namespace Puck.Vulkan.Messages;

/// <summary>
/// Bundles the handles and render area needed to record a render pass into a command buffer.
/// </summary>
/// <param name="CommandBufferHandle">The native <c>VkCommandBuffer</c> handle being recorded.</param>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="FramebufferHandle">The native <c>VkFramebuffer</c> handle the render pass renders into.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle to begin.</param>
/// <param name="Width">The width, in pixels, of the render area.</param>
/// <param name="Height">The height, in pixels, of the render area.</param>
public readonly record struct VulkanCommandBufferRecordRequest(
    nint CommandBufferHandle,
    nint DeviceHandle,
    nint FramebufferHandle,
    nint RenderPassHandle,
    uint Width,
    uint Height
);
