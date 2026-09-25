using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Bundles the handles and render area needed to record a render pass into a command buffer.
/// </summary>
/// <param name="CommandBufferHandle">The native <c>VkCommandBuffer</c> handle being recorded.</param>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="FramebufferHandle">The native <c>VkFramebuffer</c> handle the render pass renders into.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle to begin.</param>
/// <param name="Width">The width, in pixels, of the render area.</param>
/// <param name="Height">The height, in pixels, of the render area.</param>
/// <param name="ClearValues">One clear value per attachment, in attachment order, or <see langword="null"/> for a
/// render pass with one color attachment that clears to opaque black.</param>
/// <param name="X">The left edge, in pixels, of the render area.</param>
/// <param name="Y">The top edge, in pixels, of the render area.</param>
public readonly record struct VulkanCommandBufferRecordRequest(
    nint CommandBufferHandle,
    VulkanDeviceCommands Device,
    nint FramebufferHandle,
    nint RenderPassHandle,
    uint Width,
    uint Height,
    IReadOnlyList<VkClearValue>? ClearValues = null,
    int X = 0,
    int Y = 0
);
