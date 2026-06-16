using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a render pass to be created with <c>vkCreateRenderPass</c>: its attachments,
/// subpasses, and the dependencies between them.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkRenderPassCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkRenderPassCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkRenderPassCreateFlagBits</c> specifying additional parameters of the render pass.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PAttachments"/> array.</summary>
    public uint AttachmentCount;
    /// <summary>A pointer to an array of <c>VkAttachmentDescription</c> structures describing the render pass attachments.</summary>
    public nint PAttachments;
    /// <summary>The number of entries in the <see cref="PSubpasses"/> array.</summary>
    public uint SubpassCount;
    /// <summary>A pointer to an array of <c>VkSubpassDescription</c> structures describing the subpasses.</summary>
    public nint PSubpasses;
    /// <summary>The number of entries in the <see cref="PDependencies"/> array.</summary>
    public uint DependencyCount;
    /// <summary>A pointer to an array of <c>VkSubpassDependency</c> structures describing dependencies between subpasses.</summary>
    public nint PDependencies;
}
