using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the subset of a geometry's data built into an acceleration structure: the primitive count and
/// the byte offsets into the source data.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkAccelerationStructureBuildRangeInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildRangeInfoKhr {
    /// <summary>The number of primitives built into the corresponding geometry.</summary>
    public uint PrimitiveCount;
    /// <summary>The byte offset from the geometry's data address to the first primitive's data.</summary>
    public uint PrimitiveOffset;
    /// <summary>The index of the first vertex used to build the geometry (triangle geometry only).</summary>
    public uint FirstVertex;
    /// <summary>The byte offset from the geometry's transform data address to the transform used (triangle geometry only).</summary>
    public uint TransformOffset;
}
