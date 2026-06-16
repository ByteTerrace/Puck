using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Supplies the acceleration structures written to an acceleration structure descriptor; chained into a
/// <see cref="VkWriteDescriptorSet"/> via its <c>pNext</c> pointer.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkWriteDescriptorSetAccelerationStructureKHR (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkWriteDescriptorSetAccelerationStructureKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET_ACCELERATION_STRUCTURE_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The number of entries in the <see cref="PAccelerationStructures"/> array; must match the descriptor count of the write it extends.</summary>
    public uint AccelerationStructureCount;
    /// <summary>A pointer to an array of <c>VkAccelerationStructureKHR</c> handles written to the descriptor.</summary>
    public nint PAccelerationStructures;
}
