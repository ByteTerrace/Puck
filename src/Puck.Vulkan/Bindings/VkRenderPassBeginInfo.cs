using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters supplied to <c>vkCmdBeginRenderPass</c> when a render pass instance begins: the render pass and
/// framebuffer, the affected render area, and the clear values for attachments that are cleared on load.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkRenderPassBeginInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkRenderPassBeginInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The render pass to begin (a <c>VkRenderPass</c> handle).</summary>
    public nint RenderPass;
    /// <summary>The framebuffer containing the attachments used with the render pass (a <c>VkFramebuffer</c> handle).</summary>
    public nint Framebuffer;
    /// <summary>The region of the framebuffer affected by the render pass instance.</summary>
    public VkRect2D RenderArea;
    /// <summary>The number of entries in the <see cref="PClearValues"/> array.</summary>
    public uint ClearValueCount;
    /// <summary>A pointer to an array of <c>VkClearValue</c> structures supplying clear values for attachments with a clear load operation.</summary>
    public nint PClearValues;
}
