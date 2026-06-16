using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single attachment of a render pass: its format and sample count, the load/store operations
/// applied to its color/depth and stencil aspects, and its initial and final layouts.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkAttachmentDescription (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAttachmentDescription {
    /// <summary>A bitmask of <c>VkAttachmentDescriptionFlagBits</c> specifying additional properties of the attachment.</summary>
    public uint Flags;
    /// <summary>The format of the attachment's image view, as a <c>VkFormat</c> value.</summary>
    public uint Format;
    /// <summary>The number of samples of the attachment, as a <c>VkSampleCountFlagBits</c> value.</summary>
    public uint Samples;
    /// <summary>How the color or depth aspect is treated at the start of the first subpass that uses it, as a <c>VkAttachmentLoadOp</c> value.</summary>
    public uint LoadOp;
    /// <summary>How the color or depth aspect is treated at the end of the last subpass that uses it, as a <c>VkAttachmentStoreOp</c> value.</summary>
    public uint StoreOp;
    /// <summary>How the stencil aspect is treated at the start of the first subpass that uses it, as a <c>VkAttachmentLoadOp</c> value.</summary>
    public uint StencilLoadOp;
    /// <summary>How the stencil aspect is treated at the end of the last subpass that uses it, as a <c>VkAttachmentStoreOp</c> value.</summary>
    public uint StencilStoreOp;
    /// <summary>The layout the attachment is in when the render pass instance begins, as a <c>VkImageLayout</c> value.</summary>
    public uint InitialLayout;
    /// <summary>The layout the attachment is transitioned to when the render pass instance ends, as a <c>VkImageLayout</c> value.</summary>
    public uint FinalLayout;
}
