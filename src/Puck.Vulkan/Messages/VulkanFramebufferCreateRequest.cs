namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a framebuffer to create: its dimensions, the single image view bound as its attachment, and
/// the render pass it is compatible with.
/// </summary>
/// <param name="DeviceHandle">The native <c>VkDevice</c> handle.</param>
/// <param name="Width">The width, in pixels, of the framebuffer.</param>
/// <param name="Height">The height, in pixels, of the framebuffer.</param>
/// <param name="ImageViewHandle">The native <c>VkImageView</c> handle bound as the framebuffer's attachment.</param>
/// <param name="RenderPassHandle">The native <c>VkRenderPass</c> handle the framebuffer is compatible with.</param>
public readonly record struct VulkanFramebufferCreateRequest(
    nint DeviceHandle,
    uint Width,
    uint Height,
    nint ImageViewHandle,
    nint RenderPassHandle
);
