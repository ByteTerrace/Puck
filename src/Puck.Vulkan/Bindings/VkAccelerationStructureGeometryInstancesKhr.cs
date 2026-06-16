using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// An acceleration structure geometry whose data is a buffer of bottom-level instances
/// (<c>VkAccelerationStructureInstanceKHR</c>), used to build a top-level acceleration structure.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): a flattened VkAccelerationStructureGeometryKHR with its 64-byte geometry-data union (sized by
/// the triangles member) inlined as the instances payload plus tail padding, then the trailing flags. 96 bytes, offsets
/// identical to the C struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureGeometryInstancesKhr {
    /// <summary>The type of the outer geometry structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_KHR</c>).</summary>
    public uint SType;          // VkAccelerationStructureGeometryKHR.sType
    /// <summary>A pointer to a structure extending the outer geometry structure, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The kind of geometry, as a <c>VkGeometryTypeKHR</c> value (<c>VK_GEOMETRY_TYPE_INSTANCES_KHR</c> for this binding).</summary>
    public uint GeometryType;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad0;
    // union payload: VkAccelerationStructureGeometryInstancesDataKHR (32 of 64 bytes)
    /// <summary>The type of the inner instances data structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_GEOMETRY_INSTANCES_DATA_KHR</c>).</summary>
    public uint InstancesSType;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad1;
    /// <summary>A pointer to a structure extending the inner instances data structure, or <see langword="null"/>.</summary>
    public nint InstancesPNext;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> if <see cref="DataDeviceAddress"/> points to an array of pointers to instances rather than an array of instances.</summary>
    public uint ArrayOfPointers;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad2;
    /// <summary>The device address of the instance data (the layout of which is governed by <see cref="ArrayOfPointers"/>).</summary>
    public ulong DataDeviceAddress;
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
    public uint m_pad3;
}
