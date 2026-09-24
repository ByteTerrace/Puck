using Puck.Vulkan.Interop;
namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a framebuffer to create: its dimensions, the image views bound as its attachments, and the render pass it
/// is compatible with.
/// </summary>
/// <param name="Device">The command table of the logical device.</param>
/// <param name="Width">The width, in pixels, of the framebuffer.</param>
/// <param name="Height">The height, in pixels, of the framebuffer.</param>
/// <param name="ImageViewHandles">The native <c>VkImageView</c> handles bound as the framebuffer's attachments, one per
/// attachment of the render pass and in its order: the color attachments, then the depth attachment.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle the framebuffer is compatible with.</param>
public readonly record struct VulkanFramebufferCreateRequest(
    VulkanDeviceCommands Device,
    uint Width,
    uint Height,
    IReadOnlyList<nint> ImageViewHandles,
    nint RenderPassHandle
);
