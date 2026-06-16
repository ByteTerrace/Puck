using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes an execution and memory dependency between two subpasses (or between a subpass and the
/// commands outside the render pass), as a scope of source and destination stages and accesses.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSubpassDependency (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSubpassDependency {
    /// <summary>The index of the source subpass, or <c>VK_SUBPASS_EXTERNAL</c> for work before the render pass.</summary>
    public uint SrcSubpass;
    /// <summary>The index of the destination subpass, or <c>VK_SUBPASS_EXTERNAL</c> for work after the render pass.</summary>
    public uint DstSubpass;
    /// <summary>A bitmask of <c>VkPipelineStageFlagBits</c> giving the source stages of the dependency.</summary>
    public uint SrcStageMask;
    /// <summary>A bitmask of <c>VkPipelineStageFlagBits</c> giving the destination stages of the dependency.</summary>
    public uint DstStageMask;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the source access scope of the dependency.</summary>
    public uint SrcAccessMask;
    /// <summary>A bitmask of <c>VkAccessFlagBits</c> giving the destination access scope of the dependency.</summary>
    public uint DstAccessMask;
    /// <summary>A bitmask of <c>VkDependencyFlagBits</c> describing the dependency (for example, whether it is by-region).</summary>
    public uint DependencyFlags;
}
