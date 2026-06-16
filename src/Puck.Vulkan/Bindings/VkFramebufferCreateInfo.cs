using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a framebuffer to be created with <c>vkCreateFramebuffer</c>: the render pass it is
/// compatible with, the image views bound as its attachments, and its dimensions.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkFramebufferCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkFramebufferCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkFramebufferCreateFlagBits</c> specifying additional parameters of the framebuffer.</summary>
    public uint Flags;
    /// <summary>The render pass the framebuffer is compatible with (a <c>VkRenderPass</c> handle).</summary>
    public nint RenderPass;
    /// <summary>The number of entries in the <see cref="PAttachments"/> array.</summary>
    public uint AttachmentCount;
    /// <summary>A pointer to an array of <c>VkImageView</c> handles bound as the framebuffer's attachments.</summary>
    public nint PAttachments;
    /// <summary>The width, in pixels, of the framebuffer.</summary>
    public uint Width;
    /// <summary>The height, in pixels, of the framebuffer.</summary>
    public uint Height;
    /// <summary>The number of layers in the framebuffer.</summary>
    public uint Layers;
}
