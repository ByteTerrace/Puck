using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single subpass of a render pass: its pipeline bind point and the input, color, resolve,
/// depth/stencil, and preserve attachments it references.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSubpassDescription (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSubpassDescription {
    /// <summary>A bitmask of <c>VkSubpassDescriptionFlagBits</c> specifying usage of the subpass.</summary>
    public uint Flags;
    /// <summary>The pipeline type supported by the subpass, as a <c>VkPipelineBindPoint</c> value.</summary>
    public uint PipelineBindPoint;
    /// <summary>The number of entries in the <see cref="PInputAttachments"/> array.</summary>
    public uint InputAttachmentCount;
    /// <summary>A pointer to an array of <c>VkAttachmentReference</c> structures naming the attachments read as input within the subpass.</summary>
    public nint PInputAttachments;
    /// <summary>The number of entries in the <see cref="PColorAttachments"/> array.</summary>
    public uint ColorAttachmentCount;
    /// <summary>A pointer to an array of <c>VkAttachmentReference</c> structures naming the color attachments written by the subpass.</summary>
    public nint PColorAttachments;
    /// <summary>A pointer to an array of <see cref="ColorAttachmentCount"/> <c>VkAttachmentReference</c> structures for multisample resolve, or <see langword="null"/>.</summary>
    public nint PResolveAttachments;
    /// <summary>A pointer to a <c>VkAttachmentReference</c> naming the depth/stencil attachment, or <see langword="null"/> if none is used.</summary>
    public nint PDepthStencilAttachment;
    /// <summary>The number of entries in the <see cref="PPreserveAttachments"/> array.</summary>
    public uint PreserveAttachmentCount;
    /// <summary>A pointer to an array of attachment indices whose contents must be preserved across the subpass although they are not otherwise used.</summary>
    public nint PPreserveAttachments;
}
