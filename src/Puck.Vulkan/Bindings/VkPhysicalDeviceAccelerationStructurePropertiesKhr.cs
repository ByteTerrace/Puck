using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the acceleration-structure limits of a physical device; chained into the
/// properties query via <c>pNext</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPhysicalDeviceAccelerationStructurePropertiesKHR (vulkan_core.h, SDK 1.4): byte-identical
/// layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPhysicalDeviceAccelerationStructurePropertiesKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_PROPERTIES_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The maximum number of geometries in a bottom-level acceleration structure.</summary>
    public ulong MaxGeometryCount;
    /// <summary>The maximum number of instances in a top-level acceleration structure.</summary>
    public ulong MaxInstanceCount;
    /// <summary>The maximum total number of primitives across all geometries in a bottom-level acceleration structure.</summary>
    public ulong MaxPrimitiveCount;
    /// <summary>The maximum number of acceleration structure descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorAccelerationStructures;
    /// <summary>The maximum number of update-after-bind acceleration structure descriptors accessible to a single shader stage.</summary>
    public uint MaxPerStageDescriptorUpdateAfterBindAccelerationStructures;
    /// <summary>The maximum number of acceleration structure descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetAccelerationStructures;
    /// <summary>The maximum number of update-after-bind acceleration structure descriptors in a descriptor set.</summary>
    public uint MaxDescriptorSetUpdateAfterBindAccelerationStructures;
    /// <summary>The required alignment, in bytes, of the scratch buffer offset used in acceleration structure builds.</summary>
    public uint MinAccelerationStructureScratchOffsetAlignment;
}
