using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a write into a descriptor set performed by <c>vkUpdateDescriptorSets</c>: the destination set,
/// binding, and array element, and the resources written, supplied through exactly one of the resource arrays.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkWriteDescriptorSet (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkWriteDescriptorSet {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The descriptor set being updated (a <c>VkDescriptorSet</c> handle).</summary>
    public nint DstSet;
    /// <summary>The binding number within <see cref="DstSet"/> being updated.</summary>
    public uint DstBinding;
    /// <summary>The first array element within the binding being updated.</summary>
    public uint DstArrayElement;
    /// <summary>The number of descriptors to update; also the length of the relevant resource array.</summary>
    public uint DescriptorCount;
    /// <summary>The type of the descriptors being updated, as a <c>VkDescriptorType</c> value.</summary>
    public uint DescriptorType;
    /// <summary>A pointer to an array of <c>VkDescriptorImageInfo</c> structures for image, sampler, or combined image-sampler descriptors; otherwise ignored.</summary>
    public nint PImageInfo;
    /// <summary>A pointer to an array of <c>VkDescriptorBufferInfo</c> structures for buffer descriptors; otherwise ignored.</summary>
    public nint PBufferInfo;
    /// <summary>A pointer to an array of <c>VkBufferView</c> handles for texel buffer descriptors; otherwise ignored.</summary>
    public nint PTexelBufferView;
}
