using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the geometry and parameters of an acceleration structure build or update performed with
/// <c>vkCmdBuildAccelerationStructuresKHR</c> (and related commands).
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the m_padN fields make the C compiler's implicit alignment padding explicit (no real field is
/// reordered), and the scratchData VkDeviceOrHostAddressKHR union is bound as its 8-byte device-address member (ulong).
/// Offsets and size match the C struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildGeometryInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_GEOMETRY_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The level of the acceleration structure being built (top or bottom), as a <c>VkAccelerationStructureTypeKHR</c> value.</summary>
    public uint Type;
    /// <summary>A bitmask of <c>VkBuildAccelerationStructureFlagBitsKHR</c> specifying additional parameters of the build.</summary>
    public uint Flags;
    /// <summary>Whether the operation is a build or an update, as a <c>VkBuildAccelerationStructureModeKHR</c> value.</summary>
    public uint Mode;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad0;
    /// <summary>The source acceleration structure for an update (a <c>VkAccelerationStructureKHR</c> handle), or <see langword="null"/>.</summary>
    public nint SrcAccelerationStructure;
    /// <summary>The destination acceleration structure to build into (a <c>VkAccelerationStructureKHR</c> handle).</summary>
    public nint DstAccelerationStructure;
    /// <summary>The number of geometries referenced through <see cref="PGeometries"/> or <see cref="PpGeometries"/>.</summary>
    public uint GeometryCount;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad1;
    /// <summary>A pointer to an array of <c>VkAccelerationStructureGeometryKHR</c> structures, or <see langword="null"/> when <see cref="PpGeometries"/> is used.</summary>
    public nint PGeometries;
    /// <summary>A pointer to an array of pointers to <c>VkAccelerationStructureGeometryKHR</c> structures, or <see langword="null"/> when <see cref="PGeometries"/> is used.</summary>
    public nint PpGeometries;
    /// <summary>The device address of the scratch memory used by the build (the device-address member of the <c>VkDeviceOrHostAddressKHR</c> union).</summary>
    public ulong ScratchDataDeviceAddress;
}
