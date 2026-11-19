using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

// ABI mirrors of the VK_KHR_acceleration_structure structures Puck passes (vulkan_core.h, SDK 1.4.363), on the 64-bit
// ABI: each field in declaration order, a handle or device address as a 64-bit word, a VkDeviceOrHostAddress union as
// its device address alone. Only VulkanGpuAccelerationStructures fills them.

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureCreateInfoKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureCreateInfoKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_CREATE_INFO_KHR</c>, 1000150017.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>No creation flags.</summary>
    public uint CreateFlags;
    /// <summary>The <c>VkBuffer</c> the structure lives in.</summary>
    public nint Buffer;
    /// <summary>The byte offset into <see cref="Buffer"/>, a multiple of 256.</summary>
    public ulong Offset;
    /// <summary>The structure's bytes.</summary>
    public ulong Size;
    /// <summary>The <c>VkAccelerationStructureTypeKHR</c>.</summary>
    public uint Type;
    /// <summary>Zero: no capture replay address.</summary>
    public ulong DeviceAddress;
}

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureGeometryKHR</c>, its geometry union laid out as either the
/// triangles or the instances member (the union is 64 bytes, the triangles member's size).</summary>
[StructLayout(LayoutKind.Explicit, Size = 96)]
public struct VkAccelerationStructureGeometryKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_KHR</c>, 1000150006.</summary>
    [FieldOffset(0)] public uint SType;
    /// <summary>No extension chain.</summary>
    [FieldOffset(8)] public nint PNext;
    /// <summary>The <c>VkGeometryTypeKHR</c>.</summary>
    [FieldOffset(16)] public uint GeometryType;
    /// <summary>The triangles member's <c>sType</c>, 1000150005, or the instances member's, 1000150004.</summary>
    [FieldOffset(24)] public uint DataSType;
    /// <summary>The member's extension chain, none.</summary>
    [FieldOffset(32)] public nint DataPNext;
    /// <summary>Triangles: the <c>VkFormat</c> of a vertex's position. Instances: <c>arrayOfPointers</c>, false.</summary>
    [FieldOffset(40)] public uint VertexFormatOrArrayOfPointers;
    /// <summary>Triangles: the first vertex's device address. Instances: the first record's device address.</summary>
    [FieldOffset(48)] public ulong VertexDataOrInstanceData;
    /// <summary>Triangles: the bytes from one vertex to the next.</summary>
    [FieldOffset(56)] public ulong VertexStride;
    /// <summary>Triangles: the highest vertex index any index names.</summary>
    [FieldOffset(64)] public uint MaxVertex;
    /// <summary>Triangles: the <c>VkIndexType</c>.</summary>
    [FieldOffset(68)] public uint IndexType;
    /// <summary>Triangles: the first index's device address.</summary>
    [FieldOffset(72)] public ulong IndexData;
    /// <summary>Triangles: zero, no transform.</summary>
    [FieldOffset(80)] public ulong TransformData;
    /// <summary>The <c>VkGeometryFlagsKHR</c>.</summary>
    [FieldOffset(88)] public uint Flags;
}

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureBuildGeometryInfoKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildGeometryInfoKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_GEOMETRY_INFO_KHR</c>, 1000150000.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The <c>VkAccelerationStructureTypeKHR</c>.</summary>
    public uint Type;
    /// <summary>The <c>VkBuildAccelerationStructureFlagsKHR</c>.</summary>
    public uint Flags;
    /// <summary>The <c>VkBuildAccelerationStructureModeKHR</c>.</summary>
    public uint Mode;
    /// <summary>No source structure.</summary>
    public nint SrcAccelerationStructure;
    /// <summary>The structure built.</summary>
    public nint DstAccelerationStructure;
    /// <summary>The geometries, one.</summary>
    public uint GeometryCount;
    /// <summary>The geometries' array.</summary>
    public nint PGeometries;
    /// <summary>Unused.</summary>
    public nint PpGeometries;
    /// <summary>The scratch's device address.</summary>
    public ulong ScratchData;
}

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureBuildRangeInfoKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildRangeInfoKhr {
    /// <summary>The triangles or instances built.</summary>
    public uint PrimitiveCount;
    /// <summary>Zero: the addresses already point at the first.</summary>
    public uint PrimitiveOffset;
    /// <summary>Zero.</summary>
    public uint FirstVertex;
    /// <summary>Zero.</summary>
    public uint TransformOffset;
}

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureBuildSizesInfoKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildSizesInfoKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_SIZES_INFO_KHR</c>, 1000150020.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The structure's bytes.</summary>
    public ulong AccelerationStructureSize;
    /// <summary>The scratch an update needs; Puck never updates.</summary>
    public ulong UpdateScratchSize;
    /// <summary>The scratch a build needs.</summary>
    public ulong BuildScratchSize;
}

/// <summary>1:1 ABI mirror of <c>VkCopyAccelerationStructureInfoKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkCopyAccelerationStructureInfoKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_COPY_ACCELERATION_STRUCTURE_INFO_KHR</c>, 1000150010.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The structure copied.</summary>
    public nint Src;
    /// <summary>The structure written.</summary>
    public nint Dst;
    /// <summary>The <c>VkCopyAccelerationStructureModeKHR</c>.</summary>
    public uint Mode;
}

/// <summary>1:1 ABI mirror of <c>VkAccelerationStructureDeviceAddressInfoKHR</c> and of <c>VkBufferDeviceAddressInfo</c>,
/// which share one layout: a structure type, an extension chain and one handle.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkHandleAddressInfo {
    /// <summary>1000150002 for a structure, 1000244001 for a buffer.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The structure or buffer.</summary>
    public nint Handle;
}

/// <summary>1:1 ABI mirror of <c>VkWriteDescriptorSetAccelerationStructureKHR</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkWriteDescriptorSetAccelerationStructureKhr {
    /// <summary><c>VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET_ACCELERATION_STRUCTURE_KHR</c>, 1000150007.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The structures written, one.</summary>
    public uint AccelerationStructureCount;
    /// <summary>The structures' array.</summary>
    public nint PAccelerationStructures;
}

/// <summary>1:1 ABI mirror of <c>VkMemoryAllocateFlagsInfo</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkMemoryAllocateFlagsInfo {
    /// <summary><c>VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_FLAGS_INFO</c>, 1000060000.</summary>
    public uint SType;
    /// <summary>No extension chain.</summary>
    public nint PNext;
    /// <summary>The <c>VkMemoryAllocateFlags</c>.</summary>
    public uint Flags;
    /// <summary>Zero: every device of the group.</summary>
    public uint DeviceMask;
}

