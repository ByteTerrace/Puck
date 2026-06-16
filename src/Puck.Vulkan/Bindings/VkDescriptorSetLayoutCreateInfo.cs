using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a descriptor set layout to be created with <c>vkCreateDescriptorSetLayout</c>: the
/// set of bindings that make up the layout.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDescriptorSetLayoutCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDescriptorSetLayoutCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkDescriptorSetLayoutCreateFlagBits</c> specifying additional properties of the layout.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PBindings"/> array.</summary>
    public uint BindingCount;
    /// <summary>A pointer to an array of <c>VkDescriptorSetLayoutBinding</c> structures defining the bindings of the layout.</summary>
    public nint PBindings;
}
