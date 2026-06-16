using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing the descriptor sets to allocate with <c>vkAllocateDescriptorSets</c>: the pool to
/// allocate from and the layout of each set.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorSetAllocateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorSetAllocateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The descriptor pool the sets are allocated from (a <c>VkDescriptorPool</c> handle).</summary>
    public nint DescriptorPool;
    /// <summary>The number of descriptor sets to allocate; also the length of the <see cref="PSetLayouts"/> array.</summary>
    public uint DescriptorSetCount;
    /// <summary>A pointer to an array of <c>VkDescriptorSetLayout</c> handles, one describing each set to allocate.</summary>
    public nint PSetLayouts;
}
