using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Chained onto a <c>VkImageCreateInfo</c> to declare that the image's memory will be imported from an external
/// handle of the given types (for example a Direct3D 12 resource).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkExternalMemoryImageCreateInfo {
    /// <summary>The type of this structure (<c>VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkExternalMemoryHandleTypeFlagBits</c> the image's memory may be imported from.</summary>
    public uint HandleTypes;
}
