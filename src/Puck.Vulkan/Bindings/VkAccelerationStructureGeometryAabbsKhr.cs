using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// An acceleration structure geometry whose data is a buffer of axis-aligned bounding boxes
/// (<c>VkAabbPositionsKHR</c>), used to build bottom-level acceleration structures from AABB primitives.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): a flattened VkAccelerationStructureGeometryKHR (layouts transcribed from vulkan_core.h, SDK
/// 1.4.350). The Geometry variants flatten VkAccelerationStructureGeometryKHR with its 64-byte geometry-data union
/// (sized by the triangles member) inlined as the matching payload plus tail padding.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureGeometryAabbsKhr {
    /// <summary>The type of the outer geometry structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_KHR</c>).</summary>
    public uint SType;          // VkAccelerationStructureGeometryKHR.sType
    /// <summary>A pointer to a structure extending the outer geometry structure, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The kind of geometry, as a <c>VkGeometryTypeKHR</c> value (<c>VK_GEOMETRY_TYPE_AABBS_KHR</c> for this binding).</summary>
    public uint GeometryType;   // + 4 bytes pad before the 8-aligned union
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad0;
    // union payload: VkAccelerationStructureGeometryAabbsDataKHR (32 of 64 bytes)
    /// <summary>The type of the inner AABBs data structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_AABBS_DATA_KHR</c>).</summary>
    public uint AabbsSType;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad1;
    /// <summary>A pointer to a structure extending the inner AABBs data structure, or <see langword="null"/>.</summary>
    public nint AabbsPNext;
    /// <summary>The device address of the buffer of <c>VkAabbPositionsKHR</c> structures.</summary>
    public ulong DataDeviceAddress;
    /// <summary>The byte stride between consecutive AABBs in the buffer.</summary>
    public ulong Stride;
    /// <summary>Explicit padding sizing the inlined geometry-data union to its full width; carries no meaningful value.</summary>
    public ulong m_unionPad0;
    /// <summary>Explicit padding sizing the inlined geometry-data union to its full width; carries no meaningful value.</summary>
    public ulong m_unionPad1;
    /// <summary>Explicit padding sizing the inlined geometry-data union to its full width; carries no meaningful value.</summary>
    public ulong m_unionPad2;
    /// <summary>Explicit padding sizing the inlined geometry-data union to its full width; carries no meaningful value.</summary>
    public ulong m_unionPad3;
    /// <summary>A bitmask of <c>VkGeometryFlagBitsKHR</c> describing the geometry.</summary>
    public uint Flags;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad2;
}
