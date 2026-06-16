using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// References an attachment of a render pass from within a subpass, by index, together with the image
/// layout the attachment is expected to be in during that subpass.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkAttachmentReference (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAttachmentReference {
    /// <summary>The index of the attachment in the render pass's attachment array, or <c>VK_ATTACHMENT_UNUSED</c>.</summary>
    public uint Attachment;
    /// <summary>The layout the attachment is in during the subpass, as a <c>VkImageLayout</c> value.</summary>
    public uint Layout;
}
