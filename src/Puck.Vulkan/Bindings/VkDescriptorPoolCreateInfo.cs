using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a descriptor pool to be created with <c>vkCreateDescriptorPool</c>: the maximum
/// number of sets it can allocate and the per-type descriptor capacity.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorPoolCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorPoolCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkDescriptorPoolCreateFlagBits</c> specifying behavior of the pool.</summary>
    public uint Flags;
    /// <summary>The maximum number of descriptor sets that can be allocated from the pool.</summary>
    public uint MaxSets;
    /// <summary>The number of entries in the <see cref="PPoolSizes"/> array.</summary>
    public uint PoolSizeCount;
    /// <summary>A pointer to an array of <c>VkDescriptorPoolSize</c> structures giving the per-type descriptor capacity of the pool.</summary>
    public nint PPoolSizes;
}
