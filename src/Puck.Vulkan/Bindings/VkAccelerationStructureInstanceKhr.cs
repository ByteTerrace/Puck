using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// A single bottom-level acceleration structure instance referenced by a top-level acceleration structure:
/// its transform, custom index, visibility mask, shader binding table offset, flags, and the structure it
/// references.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): C# has no bit-fields, so the two packed 32-bit words (instanceCustomIndex:24|mask:8 and
/// sbtRecordOffset:24|flags:8) are bound as packed uints, and VkTransformMatrixKHR (float[3][4]) is inlined as a fixed
/// float[12]. Layout and size (64 B) match VkAccelerationStructureInstanceKHR exactly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkAccelerationStructureInstanceKhr {
    /// <summary>The instance's row-major 3×4 transform matrix (a <c>VkTransformMatrixKHR</c>), stored as 12 floats.</summary>
    public fixed float Transform[12]; // VkTransformMatrixKHR: row-major 3x4
    /// <summary>The packed application-defined 24-bit custom index (low bits) and 8-bit visibility mask (high bits).</summary>
    public uint InstanceCustomIndexAndMask;       // customIndex:24 | mask:8
    /// <summary>The packed 24-bit shader binding table record offset (low bits) and 8-bit <c>VkGeometryInstanceFlagBitsKHR</c> flags (high bits).</summary>
    public uint SbtRecordOffsetAndFlags;          // sbtRecordOffset:24 | flags:8
    /// <summary>A reference to the bottom-level acceleration structure this instance uses (its device address, or a handle for host builds).</summary>
    public ulong AccelerationStructureReference;
}
