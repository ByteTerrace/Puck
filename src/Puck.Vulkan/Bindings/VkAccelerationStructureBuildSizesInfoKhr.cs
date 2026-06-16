using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the memory sizes required to build an acceleration structure, as returned by
/// <c>vkGetAccelerationStructureBuildSizesKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkAccelerationStructureBuildSizesInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureBuildSizesInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_BUILD_SIZES_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The required size, in bytes, of the buffer backing the acceleration structure.</summary>
    public ulong AccelerationStructureSize;
    /// <summary>The required size, in bytes, of the scratch buffer for an update build.</summary>
    public ulong UpdateScratchSize;
    /// <summary>The required size, in bytes, of the scratch buffer for an initial build.</summary>
    public ulong BuildScratchSize;
}
