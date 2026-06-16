using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a render pass to create: its color attachments and the subpass dependencies that synchronize them.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="ColorAttachments">The color attachment descriptions of the render pass.</param>
/// <param name="Dependencies">The dependencies that synchronize access to the attachments.</param>
public readonly record struct VulkanRenderPassCreateRequest(
    nint DeviceHandle,
    IReadOnlyList<VkAttachmentDescription> ColorAttachments,
    IReadOnlyList<VkSubpassDependency> Dependencies
);
