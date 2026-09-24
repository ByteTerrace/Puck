using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a render pass to create: its color attachments, its optional depth attachment, and the subpass
/// dependencies that synchronize them. The attachments are numbered colors first, then depth.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="ColorAttachments">The color attachment descriptions of the render pass.</param>
/// <param name="Dependencies">The dependencies that synchronize access to the attachments.</param>
/// <param name="DepthAttachment">The depth attachment description, or <see langword="null"/> for none.</param>
public readonly record struct VulkanRenderPassCreateRequest(
    VulkanDeviceCommands Device,
    IReadOnlyList<VkAttachmentDescription> ColorAttachments,
    IReadOnlyList<VkSubpassDependency> Dependencies,
    VkAttachmentDescription? DepthAttachment = null
);
