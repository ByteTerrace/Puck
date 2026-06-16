using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Supplies values for a shader's specialization constants: a raw data block and a map locating each
/// constant within it.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSpecializationInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSpecializationInfo {
    /// <summary>The number of entries in the <see cref="PMapEntries"/> array.</summary>
    public uint MapEntryCount;
    /// <summary>A pointer to an array of <c>VkSpecializationMapEntry</c> structures mapping each constant to its location in <see cref="PData"/>.</summary>
    public nint PMapEntries;
    /// <summary>The size, in bytes, of the specialization data block pointed to by <see cref="PData"/>.</summary>
    public nuint DataSize;
    /// <summary>A pointer to the raw specialization constant data.</summary>
    public nint PData;
}
